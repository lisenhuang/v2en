using System.Diagnostics;
using v2en.Data;
using v2en.Services;

namespace v2en.Middleware;

/// <summary>
/// Records one <see cref="AnalyticsEvent"/> per public page view.
///
/// Two rules govern everything here:
///   1. <b>Never break a page.</b> The recording happens after the response has been produced, reads
///      only headers already in memory, and is wrapped so any failure is swallowed. Nothing in this
///      class can change the response or make a request fail.
///   2. <b>Never store an IP.</b> The visitor's address is used once, in-process, to derive the
///      one-way hash in <see cref="VisitorHasher"/>, and is then discarded.
/// </summary>
public sealed class AnalyticsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AnalyticsRecorder _recorder;
    private readonly ILogger<AnalyticsMiddleware> _log;

    public AnalyticsMiddleware(RequestDelegate next, AnalyticsRecorder recorder, ILogger<AnalyticsMiddleware> log)
    {
        _next = next;
        _recorder = recorder;
        _log = log;
    }

    /// <summary>Paths that are infrastructure rather than content — never counted as page views.</summary>
    private static readonly string[] IgnoredPrefixes =
    {
        "/api/", "/healthz", "/css/", "/js/", "/lib/", "/_framework", "/_content", "/.well-known",
    };

    /// <summary>Asset extensions — the static-file middleware usually short-circuits these first,
    /// but MapStaticAssets serves some of them as endpoints, which run past this point.</summary>
    private static readonly string[] IgnoredExtensions =
    {
        ".css", ".js", ".map", ".ico", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif",
        ".woff", ".woff2", ".ttf", ".otf", ".txt", ".json", ".webmanifest",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        var snapshot = _recorder.Snapshot;

        // Decide BEFORE running the pipeline: if this request is not a page view we add no overhead.
        if (!snapshot.Enabled || !ShouldRecord(context.Request, snapshot))
        {
            await _next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        finally
        {
            try
            {
                Record(context, snapshot, Stopwatch.GetElapsedTime(started));
            }
            catch (Exception ex)
            {
                // A page view is never worth an error page.
                _log.LogDebug(ex, "Skipped recording a page view.");
            }
        }
    }

    /// <summary>
    /// Whether this request counts as a page view. Static assets, API calls and health probes are
    /// noise; /admin is excluded by default so the operator's own browsing does not skew the numbers.
    /// </summary>
    internal static bool ShouldRecord(HttpRequest request, AnalyticsSnapshot snapshot)
    {
        // A page view is a GET. Form posts and preflights are activity, not views.
        if (!HttpMethods.IsGet(request.Method)) return false;

        if (snapshot.RespectDoNotTrack && OptedOut(request)) return false;

        var path = request.Path.HasValue ? request.Path.Value! : "/";

        if (!snapshot.IncludeAdmin &&
            path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) &&
            (path.Length == 6 || path[6] == '/'))
            return false;

        foreach (var prefix in IgnoredPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        // "/healthz" and "/robots.txt" style exact matches (the prefix list covers "/healthz*").
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return false;

        var lastDot = path.LastIndexOf('.');
        if (lastDot > 0 && path.IndexOf('/', lastDot) < 0)
        {
            var ext = path[lastDot..];
            foreach (var ignored in IgnoredExtensions)
                if (ext.Equals(ignored, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>Honours the two widely-implemented opt-out signals.</summary>
    private static bool OptedOut(HttpRequest request) =>
        request.Headers["DNT"] == "1" || request.Headers["Sec-GPC"] == "1";

    private void Record(HttpContext context, AnalyticsSnapshot snapshot, TimeSpan elapsed)
    {
        var request = context.Request;
        var now = DateTimeOffset.UtcNow;

        var userAgent = Clip(request.Headers.UserAgent.ToString(), 320);
        var agent = UserAgentClassifier.Classify(userAgent);
        var location = CloudflareRequestReader.ReadLocation(request);

        // The only place a raw IP is ever touched — it goes straight into the one-way hash.
        var visitor = VisitorHasher.Compute(
            CloudflareRequestReader.ClientIp(context), userAgent, snapshot.Salt, now);

        var (referrerUrl, referrerHost) = ParseReferrer(request);

        _recorder.Enqueue(new AnalyticsEvent
        {
            Utc = now,
            Path = Clip(request.Path.HasValue ? request.Path.Value! : "/", 512)!,
            Method = request.Method.Length <= 8 ? request.Method : "GET",
            StatusCode = context.Response.StatusCode,
            DurationMs = (int)Math.Clamp(elapsed.TotalMilliseconds, 0, int.MaxValue),
            VisitorHash = visitor,

            Country = location.Country,
            Continent = location.Continent,
            Region = location.Region,
            City = location.City,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
            Timezone = location.Timezone,
            EdgeColo = location.EdgeColo,

            ReferrerUrl = referrerUrl,
            ReferrerHost = referrerHost,
            UserAgent = userAgent,
            Browser = agent.Browser,
            Os = agent.Os,
            DeviceType = agent.DeviceType,
            IsBot = agent.IsBot,
        });
    }

    /// <summary>
    /// Splits the Referer header into a display URL and its host. Same-site referrers are dropped:
    /// internal navigation is not a traffic source, and keeping it would bury the real ones.
    /// </summary>
    private static (string? Url, string? Host) ParseReferrer(HttpRequest request)
    {
        var raw = request.Headers.Referer.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return (null, null);
        if (uri.Scheme is not ("http" or "https")) return (null, null);

        var host = uri.Host;
        if (host.Length == 0) return (null, null);
        if (request.Host.HasValue && string.Equals(host, request.Host.Host, StringComparison.OrdinalIgnoreCase))
            return (null, null);

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return (Clip(raw, 512), Clip(host, 255));
    }

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

/// <summary>Registration helper so Program.cs reads as one line.</summary>
public static class AnalyticsMiddlewareExtensions
{
    /// <summary>Records public page views. Place it after routing/auth and before the endpoints.</summary>
    public static IApplicationBuilder UseWebAnalytics(this IApplicationBuilder app) =>
        app.UseMiddleware<AnalyticsMiddleware>();
}
