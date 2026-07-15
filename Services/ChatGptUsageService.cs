using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

/// <summary>One rate-limit window: how much of the rolling window is used, and when it resets.</summary>
public record ChatGptUsageWindow(double UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsUtc)
{
    /// <summary>Human window label derived from the server-provided length, e.g. "5-hour", "7-day".</summary>
    public string Label => ChatGptUsageService.WindowLabel(WindowMinutes);
}

/// <summary>
/// Snapshot of the connected ChatGPT plan's Codex rate limits: the short (≈5h) window and the long
/// (≈weekly) window, plus the plan type. <see cref="FetchedUtc"/> is when we read it.
/// </summary>
public record ChatGptUsage(
    string? PlanType,
    bool LimitReached,
    ChatGptUsageWindow? Primary,
    ChatGptUsageWindow? Secondary,
    DateTimeOffset FetchedUtc);

/// <summary>
/// Reads the connected ChatGPT plan's Codex usage / rate-limit status <b>without spending any tokens</b>,
/// from the same private backend the Codex CLI uses for its account UI:
/// <c>GET https://chatgpt.com/backend-api/wham/usage</c>, authenticated with the OAuth access token +
/// account id (see <see cref="ChatGptAuthService"/>).
///
/// The response mirrors Codex's <c>RateLimitStatusPayload</c>: <c>{ plan_type, rate_limit: {
/// limit_reached, primary_window, secondary_window } }</c> where each window carries
/// <c>used_percent</c>, <c>limit_window_seconds</c> and an absolute <c>reset_at</c> (unix seconds).
/// "primary" is the short rolling window (≈5h), "secondary" the long one (≈weekly) — but the durations
/// come from the server, so we render the actual <c>limit_window_seconds</c> rather than assuming 5h/7d.
///
/// Result is cached briefly. Any failure (no account, endpoint blocked, parse error) returns null, so
/// the settings page degrades to "usage unavailable" and never breaks.
/// </summary>
public class ChatGptUsageService
{
    private const string CacheKey = "chatgpt_codex_usage";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly ChatGptAuthService _auth;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ChatGptUsageService> _logger;

    public ChatGptUsageService(HttpClient http, ChatGptAuthService auth, IMemoryCache cache, ILogger<ChatGptUsageService> logger)
    {
        _http = http;
        _auth = auth;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Latest usage snapshot (cached ≤60s), or null when unavailable / no account connected.</summary>
    public async Task<ChatGptUsage?> GetUsageAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out ChatGptUsage? cached) && cached is not null)
            return cached;

        var token = await _auth.GetValidAccessTokenAsync(ct);
        if (token is null) return null; // no account connected

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "wham/usage");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value.AccessToken);
            if (!string.IsNullOrWhiteSpace(token.Value.AccountId))
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", token.Value.AccountId);
            req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            req.Headers.TryAddWithoutValidation("User-Agent", $"codex_cli_rs/{ChatGptModelsService.CodexCliVersion} (v2en-translator)");
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Codex usage fetch failed: HTTP {Status}.", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var usage = Parse(doc.RootElement);
            if (usage is not null) _cache.Set(CacheKey, usage, CacheTtl);
            return usage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex usage fetch failed.");
            return null;
        }
    }

    private static ChatGptUsage? Parse(JsonElement root)
    {
        var plan = GetString(root, "plan_type");
        if (!root.TryGetProperty("rate_limit", out var rl) || rl.ValueKind != JsonValueKind.Object)
            return null;

        var limitReached = rl.TryGetProperty("limit_reached", out var lr) && lr.ValueKind == JsonValueKind.True;
        var primary = ParseWindow(rl, "primary_window");
        var secondary = ParseWindow(rl, "secondary_window");
        if (primary is null && secondary is null) return null;

        return new ChatGptUsage(plan, limitReached, primary, secondary, DateTimeOffset.UtcNow);
    }

    private static ChatGptUsageWindow? ParseWindow(JsonElement rateLimit, string name)
    {
        if (!rateLimit.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;

        var used = GetDouble(w, "used_percent") ?? 0;
        var windowSeconds = GetLong(w, "limit_window_seconds");
        int? windowMinutes = windowSeconds is { } s and > 0 ? (int)(s / 60) : null;

        // Prefer the absolute reset timestamp; fall back to now + relative seconds.
        DateTimeOffset? resetsUtc = null;
        var resetAt = GetLong(w, "reset_at");
        if (resetAt is { } at and > 0)
            resetsUtc = DateTimeOffset.FromUnixTimeSeconds(at);
        else if (GetLong(w, "reset_after_seconds") is { } after and >= 0)
            resetsUtc = DateTimeOffset.UtcNow.AddSeconds(after);

        return new ChatGptUsageWindow(Math.Clamp(used, 0, 100), windowMinutes, resetsUtc);
    }

    // ── display helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>Turn a window length in minutes into a friendly label: "5-hour", "7-day", "30-minute".</summary>
    public static string WindowLabel(int? minutes)
    {
        if (minutes is not { } m || m <= 0) return "rolling";
        if (m % 1440 == 0) return $"{m / 1440}-day";
        if (m % 60 == 0) return $"{m / 60}-hour";
        return $"{m}-minute";
    }

    /// <summary>Compact "time until reset": "3d 4h", "2h 14m", "5m", or "now".</summary>
    public static string Humanize(TimeSpan span)
    {
        if (span <= TimeSpan.Zero) return "now";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{span.Hours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m";
        return "<1m";
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static double? GetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
}
