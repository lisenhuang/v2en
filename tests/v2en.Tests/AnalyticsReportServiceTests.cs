using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// End-to-end reporting tests over a real (in-memory) SQLite database, because the aggregation is
/// hand-written SQL — mocking it away would test nothing. Also pins the two API guarantees the issue
/// calls out: bots are excluded by default, and no response can carry a raw IP.
/// </summary>
public class AnalyticsReportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly AnalyticsSnapshot Snapshot =
        new(Enabled: true, Salt: "salt", RespectDoNotTrack: true, IncludeAdmin: false, RetentionDays: 90);

    public AnalyticsReportServiceTests()
    {
        // A shared in-memory database lives only as long as this open connection.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Add(params AnalyticsEvent[] events)
    {
        _db.AnalyticsEvents.AddRange(events);
        _db.SaveChanges();
    }

    private static AnalyticsEvent View(
        DateTimeOffset utc, string visitor = "aaaa1111", string path = "/", string? country = "NZ",
        bool bot = false, string? city = null, double? lat = null, double? lon = null,
        string? referrer = null, string device = "desktop", int status = 200) =>
        new()
        {
            Utc = utc,
            Path = path,
            Method = "GET",
            StatusCode = status,
            DurationMs = 5,
            VisitorHash = visitor,
            Country = country,
            City = city,
            Latitude = lat,
            Longitude = lon,
            ReferrerHost = referrer,
            Browser = "Chrome",
            Os = "macOS",
            DeviceType = bot ? "bot" : device,
            IsBot = bot,
        };

    private Task<AnalyticsReport> BuildAsync(string range = "7d", bool includeBots = false) =>
        new AnalyticsReportService(_db).BuildAsync(
            AnalyticsReportService.ResolveRange(range, null, null, 0, Now), includeBots, Snapshot, default);

    // ── Totals ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountsViewsAndUniqueVisitorsSeparately()
    {
        Add(View(Now.AddHours(-1), visitor: "aaa"),
            View(Now.AddHours(-2), visitor: "aaa"),      // same visitor, second page view
            View(Now.AddHours(-3), visitor: "bbb"));

        var report = await BuildAsync();

        Assert.Equal(3, report.Totals.Views);
        Assert.Equal(2, report.Totals.Visitors);
    }

    [Fact]
    public async Task BotsAreExcludedUnlessAskedFor()
    {
        Add(View(Now.AddHours(-1), visitor: "human"),
            View(Now.AddHours(-1), visitor: "crawler", bot: true));

        var withoutBots = await BuildAsync();
        Assert.Equal(1, withoutBots.Totals.Views);
        Assert.Equal(1, withoutBots.Totals.BotViews);   // still reported, just not counted in views

        var withBots = await BuildAsync(includeBots: true);
        Assert.Equal(2, withBots.Totals.Views);
    }

    [Fact]
    public async Task ViewsOutsideTheWindowAreIgnored()
    {
        Add(View(Now.AddDays(-1)), View(Now.AddDays(-30)));

        Assert.Equal(1, (await BuildAsync("7d")).Totals.Views);
        Assert.Equal(2, (await BuildAsync("90d")).Totals.Views);
    }

    [Fact]
    public async Task ThePreviousPeriodIsTheSameLengthImmediatelyBefore()
    {
        Add(View(Now.AddDays(-1)), View(Now.AddDays(-2)),   // this period
            View(Now.AddDays(-9)));                        // previous period

        var report = await BuildAsync("7d");

        Assert.Equal(2, report.Totals.Views);
        Assert.Equal(1, report.Totals.PreviousViews);
    }

    [Fact]
    public async Task VisitsWithNoVisitorHashCountAsViewsButNotAsVisitors()
    {
        Add(View(Now.AddHours(-1), visitor: ""));

        var report = await BuildAsync();

        Assert.Equal(1, report.Totals.Views);
        Assert.Equal(0, report.Totals.Visitors);
    }

    // ── Location ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestsWithoutEdgeHeadersAreReportedAsUnknownLocation()
    {
        Add(View(Now.AddHours(-1), country: "NZ", visitor: "a"),
            View(Now.AddHours(-1), country: null, visitor: "b"));   // direct/local request

        var report = await BuildAsync();

        Assert.Equal(2, report.Totals.Views);
        Assert.Equal(1, report.Totals.Countries);           // only NZ is a country
        Assert.Equal(1, report.Totals.LocatedViews);
        Assert.Contains(report.Countries, c => c.Code is null && c.Name == "Unknown");
        // …and an unlocated request is never plotted on the map.
        Assert.All(report.Map, point => Assert.NotNull(point.Country));
    }

    [Fact]
    public async Task CityCoordinatesArePlottedExactlyAndCountryOnlyDataIsApproximated()
    {
        Add(View(Now.AddHours(-1), country: "NZ", city: "Auckland", lat: -36.85, lon: 174.76, visitor: "a"),
            View(Now.AddHours(-1), country: "DE", visitor: "b"));   // free plan: country code only

        var report = await BuildAsync();

        var auckland = Assert.Single(report.Map, p => p.Label == "Auckland");
        Assert.False(auckland.Approximate);
        Assert.Equal(-36.9, auckland.Latitude, 1);

        var germany = Assert.Single(report.Map, p => p.Country == "DE");
        Assert.True(germany.Approximate);
        Assert.Equal("Germany", germany.Label);
        Assert.InRange(germany.Latitude, 47, 55);      // somewhere sensible inside Germany
        Assert.InRange(germany.Longitude, 5, 16);
    }

    [Fact]
    public async Task CountriesAreNamedAndFlagged()
    {
        Add(View(Now.AddHours(-1), country: "JP"));

        var country = Assert.Single((await BuildAsync()).Countries);

        Assert.Equal("Japan", country.Name);
        Assert.Equal("🇯🇵", country.Flag);
    }

    // ── Breakdowns and series ──────────────────────────────────────────────────

    [Fact]
    public async Task TopPagesAndReferrersAreRankedByViews()
    {
        Add(View(Now.AddHours(-1), path: "/t/1", referrer: "news.ycombinator.com", visitor: "a"),
            View(Now.AddHours(-2), path: "/t/1", referrer: "news.ycombinator.com", visitor: "b"),
            View(Now.AddHours(-3), path: "/", referrer: null, visitor: "c"));

        var report = await BuildAsync();

        Assert.Equal("/t/1", report.Pages[0].Key);
        Assert.Equal(2, report.Pages[0].Views);
        Assert.Equal(2, report.Pages[0].Visitors);

        var referrer = Assert.Single(report.Referrers);      // direct traffic has no referrer row
        Assert.Equal("news.ycombinator.com", referrer.Key);
    }

    [Fact]
    public async Task TheSeriesCoversTheWholeWindowIncludingEmptyBuckets()
    {
        Add(View(Now.AddDays(-1)));

        var report = await BuildAsync("7d");

        Assert.Equal("day", report.Range.Granularity);
        Assert.InRange(report.Series.Count, 7, 8);                        // 7 days, partial ends
        Assert.Equal(1, report.Series.Sum(bucket => bucket.Views));
        Assert.Contains(report.Series, bucket => bucket.Views == 0);      // gaps read as zero
        // Buckets are contiguous and ascending.
        for (var i = 1; i < report.Series.Count; i++)
            Assert.True(report.Series[i].Start > report.Series[i - 1].Start);
    }

    [Fact]
    public async Task ShortWindowsAreBucketedByHour()
    {
        Add(View(Now.AddHours(-2)), View(Now.AddHours(-2)), View(Now.AddHours(-5)));

        var report = await BuildAsync("24h");

        Assert.Equal("hour", report.Range.Granularity);
        Assert.Equal(3, report.Series.Sum(bucket => bucket.Views));
        Assert.Contains(report.Series, bucket => bucket.Views == 2);
    }

    [Fact]
    public async Task AnEmptyDatabaseProducesAnEmptyReportRatherThanAnError()
    {
        var report = await BuildAsync();

        Assert.Equal(0, report.Totals.Views);
        Assert.Empty(report.Countries);
        Assert.Empty(report.Map);
        Assert.Empty(report.Recent);
        Assert.NotEmpty(report.Series);          // the chart still spans the window, all zeros
    }

    [Fact]
    public async Task AllTimeStartsAtTheOldestRecordedView()
    {
        Add(View(Now.AddDays(-40)), View(Now.AddHours(-1)));

        var report = await BuildAsync("all");

        Assert.Equal(2, report.Totals.Views);
        Assert.True(report.Range.From >= Now.AddDays(-41), "'All' should not span years of empty buckets.");
    }

    // ── Recent visits + the privacy guarantee ──────────────────────────────────

    [Fact]
    public async Task RecentVisitsAreNewestFirstAndExposeOnlyAHashPrefix()
    {
        Add(View(Now.AddHours(-2), visitor: "0123456789abcdef", path: "/old"),
            View(Now.AddHours(-1), visitor: "0123456789abcdef", path: "/new"));

        var report = await BuildAsync();

        Assert.Equal("/new", report.Recent[0].Path);
        Assert.Equal("/old", report.Recent[1].Path);
        Assert.Equal("01234567", report.Recent[0].Visitor);   // prefix only
    }

    [Fact]
    public async Task NoIpAddressCanReachTheApiResponse()
    {
        // The IP is never stored, so serializing the whole report must contain neither an address
        // nor any property that looks like one.
        Add(View(Now.AddHours(-1), country: "NZ", city: "Auckland", lat: -36.85, lon: 174.76,
                 referrer: "news.ycombinator.com"));

        var json = JsonSerializer.Serialize(await BuildAsync(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.DoesNotContain("\"ip\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipAddress", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("203.0.113", json);
        Assert.DoesNotContain("userAgent", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Range resolution ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    [InlineData("30d", 720)]
    [InlineData("90d", 2160)]
    public void NamedRangesResolveToTheRightWindow(string key, double hours)
    {
        var range = AnalyticsReportService.ResolveRange(key, null, null, 0, Now);

        Assert.Equal(key, range.Key);
        Assert.Equal(hours, (range.To - range.From).TotalHours, 1);
    }

    [Fact]
    public void AnUnknownRangeFallsBackToSevenDaysInsteadOfFailing()
    {
        var range = AnalyticsReportService.ResolveRange("../etc/passwd", null, null, 0, Now);

        Assert.Equal("7d", range.Key);
        Assert.Equal(7, (range.To - range.From).TotalDays, 1);
    }

    [Fact]
    public void CustomDatesAreInclusiveAndReadInTheViewersTimezone()
    {
        var range = AnalyticsReportService.ResolveRange(
            null, new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 3), offsetMinutes: 720, now: Now);

        Assert.Equal("custom", range.Key);
        // Local midnight on the 1st is 11:00 UTC on 31 July when the viewer is 12 hours ahead.
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero), range.From);
        // …and the end date is included in full, so the window closes at local midnight on the 4th.
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero), range.To);
    }

    [Fact]
    public void ReversedCustomDatesAreSwappedRatherThanRejected()
    {
        var range = AnalyticsReportService.ResolveRange(
            null, new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 1), 0, Now);

        Assert.True(range.From < range.To);
    }

    [Fact]
    public void ACustomRangeNeverExtendsIntoTheFuture()
    {
        var range = AnalyticsReportService.ResolveRange(
            null, new DateOnly(2026, 8, 10), new DateOnly(2027, 1, 1), 0, Now);

        Assert.True(range.To <= Now);
        Assert.True(range.From < range.To);
    }

    [Fact]
    public void AbsurdTimezoneOffsetsAreClamped()
    {
        var range = AnalyticsReportService.ResolveRange("7d", null, null, 99_999, Now);
        Assert.Equal(14 * 60, range.OffsetMinutes);
    }

    [Fact]
    public void DailyBucketsStartAtTheViewersLocalMidnight()
    {
        var range = AnalyticsReportService.ResolveRange("7d", null, null, offsetMinutes: 720, now: Now);

        var origin = AnalyticsReportService.BucketOrigin(range);

        Assert.Equal(TimeSpan.TicksPerDay, AnalyticsReportService.BucketTicks(range.Granularity));
        Assert.Equal(12, origin.UtcDateTime.Hour);         // local midnight at UTC+12 is 12:00 UTC
        Assert.Equal(0, origin.UtcDateTime.Minute);
        Assert.True(origin <= range.From);
    }
}
