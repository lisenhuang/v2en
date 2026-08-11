using Microsoft.AspNetCore.Http;
using v2en.Middleware;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>Which requests count as a page view — the filter that keeps the numbers meaningful.</summary>
public class AnalyticsMiddlewareTests
{
    private static readonly AnalyticsSnapshot Default =
        new(Enabled: true, Salt: "salt", RespectDoNotTrack: true, IncludeAdmin: false, RetentionDays: 90);

    private static HttpRequest Get(string path, params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Get;
        ctx.Request.Path = path;
        foreach (var (name, value) in headers)
            ctx.Request.Headers[name] = value;
        return ctx.Request;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/t/1222343")]
    [InlineData("/ask")]
    [InlineData("/search")]
    [InlineData("/index.xml")]          // the Atom feed is real readership, so it counts
    [InlineData("/definitely-missing")] // 404s are worth seeing in the dashboard
    public void ContentRequestsAreRecorded(string path)
    {
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get(path), Default));
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/chat")]
    [InlineData("/api/admin/analytics")]
    [InlineData("/css/site.css")]
    [InlineData("/js/site.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/robots.txt")]
    [InlineData("/.well-known/security.txt")]
    [InlineData("/logo.png")]
    [InlineData("/fonts/inter.woff2")]
    public void InfrastructureAndAssetRequestsAreNot(string path)
    {
        Assert.False(AnalyticsMiddleware.ShouldRecord(Get(path), Default));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/")]
    [InlineData("/admin/settings")]
    [InlineData("/ADMIN/Logs")]
    public void AdminPagesAreExcludedByDefault(string path)
    {
        Assert.False(AnalyticsMiddleware.ShouldRecord(Get(path), Default));
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get(path), Default with { IncludeAdmin = true }));
    }

    [Fact]
    public void APathThatMerelyStartsWithAdminIsStillRecorded()
    {
        // "/administrivia" is a public page, not the dashboard.
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get("/administrivia"), Default));
    }

    [Fact]
    public void OnlyGetRequestsCountAsPageViews()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = HttpMethods.Post;
        ctx.Request.Path = "/ask";

        Assert.False(AnalyticsMiddleware.ShouldRecord(ctx.Request, Default));
    }

    [Theory]
    [InlineData("DNT", "1")]
    [InlineData("Sec-GPC", "1")]
    public void OptOutSignalsAreHonoured(string header, string value)
    {
        Assert.False(AnalyticsMiddleware.ShouldRecord(Get("/", (header, value)), Default));
        // …unless the operator has turned that behaviour off.
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get("/", (header, value)), Default with { RespectDoNotTrack = false }));
    }

    [Fact]
    public void DntZeroIsNotAnOptOut()
    {
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get("/", ("DNT", "0")), Default));
    }

    [Fact]
    public void ADotInAPathSegmentDoesNotLookLikeAFileExtension()
    {
        // A post slug can contain a dot; only a trailing, known asset extension is excluded.
        Assert.True(AnalyticsMiddleware.ShouldRecord(Get("/t/1.2/replies"), Default));
    }
}
