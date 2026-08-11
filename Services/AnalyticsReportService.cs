using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Utilities;

namespace v2en.Services;

// ── Report shapes (serialized straight to JSON by the admin API) ────────────────────

/// <summary>The resolved reporting window plus how it is bucketed for the trend chart.</summary>
public sealed record AnalyticsRange(
    DateTimeOffset From, DateTimeOffset To, string Key, string Granularity, int OffsetMinutes);

/// <summary>Headline numbers, each paired with the same figure for the immediately preceding window.</summary>
public sealed record AnalyticsTotals(
    long Views, long Visitors, long Countries, long BotViews,
    long PreviousViews, long PreviousVisitors,
    long LocatedViews);

/// <summary>One point on the traffic trend.</summary>
public sealed record AnalyticsBucket(DateTimeOffset Start, long Views, long Visitors);

/// <summary>A country row: code may be null, which the UI renders as "Unknown".</summary>
public sealed record AnalyticsCountry(string? Code, string Name, string Flag, long Views, long Visitors);

/// <summary>A plottable location. Coordinates are city-level from Cloudflare, or a country label point.</summary>
public sealed record AnalyticsMapPoint(
    string Label, string? Country, double Latitude, double Longitude, long Views, long Visitors, bool Approximate);

/// <summary>A generic "top N" row — pages, referrers, devices, browsers, operating systems.</summary>
public sealed record AnalyticsStat(string Key, long Views, long Visitors);

/// <summary>One row of the recent-visits table. Deliberately carries no IP — only the rotating hash.</summary>
public sealed record AnalyticsVisit(
    DateTimeOffset Utc, string Path, int Status, int DurationMs,
    string? Country, string CountryName, string Flag, string? City, string? Region,
    string? Device, string? Browser, string? Os, string? ReferrerHost, string Visitor, bool IsBot);

/// <summary>Everything the dashboard needs for one render.</summary>
public sealed record AnalyticsReport(
    AnalyticsRange Range,
    AnalyticsTotals Totals,
    IReadOnlyList<AnalyticsBucket> Series,
    IReadOnlyList<AnalyticsCountry> Countries,
    IReadOnlyList<AnalyticsMapPoint> Map,
    IReadOnlyList<AnalyticsStat> Pages,
    IReadOnlyList<AnalyticsStat> Referrers,
    IReadOnlyList<AnalyticsStat> Devices,
    IReadOnlyList<AnalyticsStat> Browsers,
    IReadOnlyList<AnalyticsStat> OperatingSystems,
    IReadOnlyList<AnalyticsVisit> Recent,
    bool IncludeBots,
    bool CollectionEnabled,
    int RetentionDays);

/// <summary>
/// Aggregates recorded page views into the dashboard's report.
///
/// The queries are hand-written SQL rather than LINQ for two reasons. First, timestamps are persisted
/// as UTC tick counts (see <see cref="AppDbContext"/>), so bucketing a trend line is plain integer
/// arithmetic on the stored column — <c>(Utc - origin) / bucketTicks</c> — which no date function or
/// client-side grouping can beat, and which lets the reporting window start at the ADMIN's local
/// midnight rather than UTC's. Second, "unique visitors" is <c>COUNT(DISTINCT …)</c>, which EF Core
/// does not translate. Every value is passed as a parameter; the only interpolated text is the
/// constant column/table names below.
///
/// Nothing here can return an IP address, because none is stored: <see cref="AnalyticsEvent.VisitorHash"/>
/// is the sole visitor identifier, and even that is truncated before it leaves the server.
/// </summary>
public sealed class AnalyticsReportService
{
    private const string Table = "AnalyticsEvents";

    /// <summary>Rows in each "top N" list. Enough to be useful, small enough to scan.</summary>
    private const int TopN = 12;

    /// <summary>Rows in the recent-visits table.</summary>
    private const int RecentN = 50;

    /// <summary>Max markers drawn on the map — beyond this the picture is mud, not information.</summary>
    private const int MaxMapPoints = 400;

    /// <summary>Characters of the visitor hash exposed to the UI: enough to group rows, useless alone.</summary>
    private const int VisitorPrefix = 8;

    private readonly AppDbContext _db;

    public AnalyticsReportService(AppDbContext db) => _db = db;

    /// <summary>Distinct-visitor expression — blank hashes (no client address) must not count as a visitor.</summary>
    private const string DistinctVisitors = "COUNT(DISTINCT NULLIF(VisitorHash, ''))";

    public async Task<AnalyticsReport> BuildAsync(
        AnalyticsRange range, bool includeBots, AnalyticsSnapshot snapshot, CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        // "All time" is nominally ten years back; pull it forward to the first recorded view so the
        // chart shows the data that exists instead of years of leading zeros.
        if (range.Key == "all")
        {
            var earliest = await ReadEarliestAsync(conn, ct);
            if (earliest is not null && earliest > range.From)
                range = range with
                {
                    From = earliest.Value,
                    Granularity = GranularityFor(range.To - earliest.Value),
                };
        }

        var from = range.From.UtcTicks;
        var to = range.To.UtcTicks;
        var span = range.To - range.From;
        var prevFrom = range.From.Add(-span).UtcTicks;

        var totals = await ReadTotalsAsync(conn, from, to, prevFrom, includeBots, ct);
        var series = await ReadSeriesAsync(conn, range, includeBots, ct);
        var countries = await ReadCountriesAsync(conn, from, to, includeBots, ct);
        var map = await ReadMapAsync(conn, from, to, includeBots, ct);
        var pages = await ReadTopAsync(conn, "Path", from, to, includeBots, requireNotNull: true, ct);
        var referrers = await ReadTopAsync(conn, "ReferrerHost", from, to, includeBots, requireNotNull: true, ct);
        var devices = await ReadTopAsync(conn, "DeviceType", from, to, includeBots, requireNotNull: true, ct);
        var browsers = await ReadTopAsync(conn, "Browser", from, to, includeBots, requireNotNull: true, ct);
        var systems = await ReadTopAsync(conn, "Os", from, to, includeBots, requireNotNull: true, ct);
        var recent = await ReadRecentAsync(conn, from, to, includeBots, ct);

        return new AnalyticsReport(
            range, totals, series, countries, map, pages, referrers, devices, browsers, systems, recent,
            includeBots, snapshot.Enabled, snapshot.RetentionDays);
    }

    // ── Individual queries ─────────────────────────────────────────────────────────

    /// <summary>Timestamp of the oldest surviving page view, or null when nothing has been recorded.</summary>
    private static async Task<DateTimeOffset?> ReadEarliestAsync(DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT MIN(Utc) FROM {Table}";
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : new DateTimeOffset(Convert.ToInt64(value), TimeSpan.Zero);
    }

    private async Task<AnalyticsTotals> ReadTotalsAsync(
        DbConnection conn, long from, long to, long prevFrom, bool includeBots, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              COUNT(*),
              {DistinctVisitors},
              COUNT(DISTINCT Country),
              SUM(CASE WHEN Country IS NOT NULL THEN 1 ELSE 0 END)
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
            """;
        Bind(cmd, "$from", from);
        Bind(cmd, "$to", to);

        long views = 0, visitors = 0, countries = 0, located = 0;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                views = reader.GetInt64(0);
                visitors = reader.GetInt64(1);
                countries = reader.GetInt64(2);
                located = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            }
        }

        // Bot traffic is counted WITHOUT the bot filter on purpose: the tile answers "how much of
        // what hit the site was a crawler", which would always read zero if it were filtered too.
        await using var botCmd = conn.CreateCommand();
        botCmd.CommandText = $"SELECT COUNT(*) FROM {Table} WHERE Utc >= $from AND Utc < $to AND IsBot = 1";
        Bind(botCmd, "$from", from);
        Bind(botCmd, "$to", to);
        var bots = Convert.ToInt64(await botCmd.ExecuteScalarAsync(ct) ?? 0L);

        // The same window immediately before this one, for the "vs previous period" deltas.
        await using var prev = conn.CreateCommand();
        prev.CommandText = $"""
            SELECT COUNT(*), {DistinctVisitors}
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
            """;
        Bind(prev, "$from", prevFrom);
        Bind(prev, "$to", from);

        long prevViews = 0, prevVisitors = 0;
        await using (var reader = await prev.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                prevViews = reader.GetInt64(0);
                prevVisitors = reader.GetInt64(1);
            }
        }

        return new AnalyticsTotals(views, visitors, countries, bots, prevViews, prevVisitors, located);
    }

    /// <summary>
    /// The trend line. Buckets are fixed-width tick ranges anchored at <see cref="BucketOrigin"/>, so
    /// a "day" really is the admin's local day, and empty buckets are filled in afterwards — a gap in
    /// traffic must read as a zero on the chart, not as a missing point.
    /// </summary>
    private async Task<IReadOnlyList<AnalyticsBucket>> ReadSeriesAsync(
        DbConnection conn, AnalyticsRange range, bool includeBots, CancellationToken ct)
    {
        var bucketTicks = BucketTicks(range.Granularity);
        var origin = BucketOrigin(range);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT (Utc - $origin) / $bucket AS slot, COUNT(*), {DistinctVisitors}
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
            GROUP BY slot
            ORDER BY slot
            """;
        Bind(cmd, "$origin", origin.UtcTicks);
        Bind(cmd, "$bucket", bucketTicks);
        Bind(cmd, "$from", range.From.UtcTicks);
        Bind(cmd, "$to", range.To.UtcTicks);

        var counted = new Dictionary<long, (long Views, long Visitors)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                counted[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }

        var buckets = new List<AnalyticsBucket>();
        var slots = (range.To.UtcTicks - origin.UtcTicks + bucketTicks - 1) / bucketTicks;
        for (long slot = 0; slot < slots; slot++)
        {
            var start = origin.AddTicks(slot * bucketTicks);
            if (start >= range.To) break;
            var hit = counted.TryGetValue(slot, out var v) ? v : (0, 0);
            buckets.Add(new AnalyticsBucket(start, hit.Item1, hit.Item2));
        }
        return buckets;
    }

    private async Task<IReadOnlyList<AnalyticsCountry>> ReadCountriesAsync(
        DbConnection conn, long from, long to, bool includeBots, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Country, COUNT(*) AS views, {DistinctVisitors}
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
            GROUP BY Country
            ORDER BY views DESC
            LIMIT $limit
            """;
        Bind(cmd, "$from", from);
        Bind(cmd, "$to", to);
        Bind(cmd, "$limit", TopN + 8);   // countries are cheap to list; show a longer tail

        var rows = new List<AnalyticsCountry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(0) ? null : reader.GetString(0);
            rows.Add(new AnalyticsCountry(
                code,
                code is null ? "Unknown" : Countries.Name(code),
                Countries.FlagEmoji(code),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }
        return rows;
    }

    /// <summary>
    /// Points for the map, from the best location each row has.
    ///
    /// Cloudflare only sends city-level latitude/longitude on some plans, so this runs two passes:
    /// exact coordinates first (grouped to ~11 km so a city is one marker, not a smear), then every
    /// remaining located row folded onto its country's label point and flagged as approximate. Rows
    /// with no country at all — local and direct requests — are simply absent from the map, which is
    /// exactly how "unknown location" should render.
    /// </summary>
    private async Task<IReadOnlyList<AnalyticsMapPoint>> ReadMapAsync(
        DbConnection conn, long from, long to, bool includeBots, CancellationToken ct)
    {
        var points = new List<AnalyticsMapPoint>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Country, City,
                       ROUND(Latitude, 1) AS lat, ROUND(Longitude, 1) AS lon,
                       COUNT(*) AS views, {DistinctVisitors}
                FROM {Table}
                WHERE Utc >= $from AND Utc < $to AND Latitude IS NOT NULL AND Longitude IS NOT NULL
                      {BotFilter(includeBots)}
                GROUP BY Country, City, lat, lon
                ORDER BY views DESC
                LIMIT $limit
                """;
            Bind(cmd, "$from", from);
            Bind(cmd, "$to", to);
            Bind(cmd, "$limit", MaxMapPoints);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var country = reader.IsDBNull(0) ? null : reader.GetString(0);
                var city = reader.IsDBNull(1) ? null : reader.GetString(1);
                points.Add(new AnalyticsMapPoint(
                    Label: city ?? Countries.Name(country),
                    Country: country,
                    Latitude: reader.GetDouble(2),
                    Longitude: reader.GetDouble(3),
                    Views: reader.GetInt64(4),
                    Visitors: reader.GetInt64(5),
                    Approximate: false));
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Country, COUNT(*) AS views, {DistinctVisitors}
                FROM {Table}
                WHERE Utc >= $from AND Utc < $to AND Country IS NOT NULL AND Latitude IS NULL
                      {BotFilter(includeBots)}
                GROUP BY Country
                ORDER BY views DESC
                LIMIT $limit
                """;
            Bind(cmd, "$from", from);
            Bind(cmd, "$to", to);
            Bind(cmd, "$limit", MaxMapPoints);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0);
                var point = Countries.Point(code);
                if (point is null) continue;   // e.g. Tor: a real country code with no place on a map
                points.Add(new AnalyticsMapPoint(
                    Label: Countries.Name(code),
                    Country: code,
                    Latitude: point.Value.Latitude,
                    Longitude: point.Value.Longitude,
                    Views: reader.GetInt64(1),
                    Visitors: reader.GetInt64(2),
                    Approximate: true));
            }
        }

        return points
            .OrderByDescending(p => p.Views)
            .Take(MaxMapPoints)
            .ToList();
    }

    /// <summary>Generic "top N by views" over one column. The column name is a compile-time constant.</summary>
    private async Task<IReadOnlyList<AnalyticsStat>> ReadTopAsync(
        DbConnection conn, string column, long from, long to, bool includeBots, bool requireNotNull, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {column}, COUNT(*) AS views, {DistinctVisitors}
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
                  {(requireNotNull ? $"AND {column} IS NOT NULL AND {column} <> ''" : "")}
            GROUP BY {column}
            ORDER BY views DESC
            LIMIT $limit
            """;
        Bind(cmd, "$from", from);
        Bind(cmd, "$to", to);
        Bind(cmd, "$limit", TopN);

        var rows = new List<AnalyticsStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new AnalyticsStat(reader.IsDBNull(0) ? "—" : reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        return rows;
    }

    private async Task<IReadOnlyList<AnalyticsVisit>> ReadRecentAsync(
        DbConnection conn, long from, long to, bool includeBots, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Utc, Path, StatusCode, DurationMs, Country, City, Region,
                   DeviceType, Browser, Os, ReferrerHost, VisitorHash, IsBot
            FROM {Table}
            WHERE Utc >= $from AND Utc < $to {BotFilter(includeBots)}
            ORDER BY Utc DESC
            LIMIT $limit
            """;
        Bind(cmd, "$from", from);
        Bind(cmd, "$to", to);
        Bind(cmd, "$limit", RecentN);

        var rows = new List<AnalyticsVisit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var code = reader.IsDBNull(4) ? null : reader.GetString(4);
            var hash = reader.IsDBNull(11) ? "" : reader.GetString(11);
            rows.Add(new AnalyticsVisit(
                Utc: new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                Path: reader.IsDBNull(1) ? "/" : reader.GetString(1),
                Status: reader.GetInt32(2),
                DurationMs: reader.GetInt32(3),
                Country: code,
                CountryName: code is null ? "Unknown" : Countries.Name(code),
                Flag: Countries.FlagEmoji(code),
                City: reader.IsDBNull(5) ? null : reader.GetString(5),
                Region: reader.IsDBNull(6) ? null : reader.GetString(6),
                Device: reader.IsDBNull(7) ? null : reader.GetString(7),
                Browser: reader.IsDBNull(8) ? null : reader.GetString(8),
                Os: reader.IsDBNull(9) ? null : reader.GetString(9),
                ReferrerHost: reader.IsDBNull(10) ? null : reader.GetString(10),
                // Only a prefix leaves the server: it groups a session's rows without handing the UI
                // the full hash. (The full value is not an IP either — see VisitorHasher.)
                Visitor: hash.Length > VisitorPrefix ? hash[..VisitorPrefix] : hash,
                IsBot: !reader.IsDBNull(12) && reader.GetInt64(12) != 0));
        }
        return rows;
    }

    // ── Range + bucket maths ───────────────────────────────────────────────────────

    /// <summary>Ranges the dashboard offers, with the bucket size each one reads best at.</summary>
    public const string GranularityHour = "hour";
    public const string GranularityDay = "day";
    public const string GranularityWeek = "week";

    /// <summary>
    /// Resolves the requested window. <paramref name="key"/> is one of 24h/7d/30d/90d/12mo/all, or
    /// "custom" when explicit dates are supplied. Anything unrecognised falls back to 7 days, so a
    /// hand-edited URL can never produce an error page.
    ///
    /// Custom dates are calendar dates in the VIEWER's timezone — picking "1–7 August" must mean
    /// eight local midnights, not a UTC window that starts mid-afternoon somewhere else — so they are
    /// shifted by <paramref name="offsetMinutes"/> (minutes east of UTC) on the way in.
    /// </summary>
    public static AnalyticsRange ResolveRange(
        string? key, DateOnly? fromDate, DateOnly? toDate, int offsetMinutes, DateTimeOffset now)
    {
        offsetMinutes = Math.Clamp(offsetMinutes, -14 * 60, 14 * 60);
        var normalized = (key ?? "").Trim().ToLowerInvariant();

        if (fromDate is not null || toDate is not null)
        {
            var offset = TimeSpan.FromMinutes(offsetMinutes);
            var first = fromDate ?? toDate!.Value.AddDays(-6);
            var last = toDate ?? fromDate!.Value;
            if (first > last) (first, last) = (last, first);

            var from = LocalMidnight(first) - offset;
            var to = LocalMidnight(last.AddDays(1)) - offset;   // inclusive end date ⇒ up to its last moment
            if (to > now) to = now;
            if (from >= to) from = to.AddDays(-1);
            return new AnalyticsRange(from, to, "custom", GranularityFor(to - from), offsetMinutes);
        }

        var window = normalized switch
        {
            "24h" or "1d" => TimeSpan.FromHours(24),
            "7d" => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            "90d" => TimeSpan.FromDays(90),
            "12mo" or "365d" => TimeSpan.FromDays(365),
            "all" => TimeSpan.FromDays(3650),
            _ => TimeSpan.FromDays(7),
        };
        var resolvedKey = normalized is "24h" or "7d" or "30d" or "90d" or "12mo" or "all" ? normalized : "7d";
        return new AnalyticsRange(now - window, now, resolvedKey, GranularityFor(window), offsetMinutes);
    }

    private static DateTimeOffset LocalMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Roughly 24–120 points per chart: dense enough to show shape, sparse enough to label.</summary>
    private static string GranularityFor(TimeSpan span) =>
        span <= TimeSpan.FromDays(3) ? GranularityHour
        : span <= TimeSpan.FromDays(120) ? GranularityDay
        : GranularityWeek;

    internal static long BucketTicks(string granularity) => granularity switch
    {
        GranularityHour => TimeSpan.TicksPerHour,
        GranularityWeek => TimeSpan.TicksPerDay * 7,
        _ => TimeSpan.TicksPerDay,
    };

    /// <summary>
    /// The instant bucket 0 starts at: the range's start rounded DOWN to a boundary in the viewer's
    /// local time. Without the offset, "yesterday" on the chart would mean a UTC day, which is wrong
    /// for every admin who is not on UTC.
    /// </summary>
    internal static DateTimeOffset BucketOrigin(AnalyticsRange range)
    {
        var offset = TimeSpan.FromMinutes(range.OffsetMinutes);
        var local = range.From + offset;

        var aligned = range.Granularity switch
        {
            GranularityHour => new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, TimeSpan.Zero),
            GranularityWeek => StartOfWeek(local),
            _ => new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero),
        };
        return aligned - offset;
    }

    /// <summary>Weeks start on Monday — the convention every other traffic tool uses.</summary>
    private static DateTimeOffset StartOfWeek(DateTimeOffset local)
    {
        var day = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
        var back = ((int)day.DayOfWeek + 6) % 7;
        return day.AddDays(-back);
    }

    private static string BotFilter(bool includeBots) => includeBots ? "" : "AND IsBot = 0";

    private static void Bind(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
