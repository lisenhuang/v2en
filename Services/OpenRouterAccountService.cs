using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

/// <summary>
/// OpenRouter account snapshot from GET /key. Credit fields are in USD; for :free models they
/// stay $0. There is no API field for "free requests remaining today", so the dashboard combines
/// <see cref="IsFreeTier"/> (which implies the ~50/day vs ~1000/day free-model limit) with our own
/// today's-call counter to estimate remaining free quota.
/// </summary>
public record OpenRouterAccount(
    bool IsFreeTier,
    double? Limit,
    double? LimitRemaining,
    double Usage,
    double UsageDaily)
{
    /// <summary>Best-effort free-model request limit per day implied by the account tier.</summary>
    public int FreeDailyRequestLimit => IsFreeTier ? 50 : 1000;
}

public class OpenRouterAccountService
{
    private const string CacheKey = "openrouter_account";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenRouterAccountService> _logger;

    public OpenRouterAccountService(HttpClient http, IMemoryCache cache, ILogger<OpenRouterAccountService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Returns the account snapshot, or null if no key is set / OpenRouter is unreachable.</summary>
    public async Task<OpenRouterAccount?> GetAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        if (_cache.TryGetValue(CacheKey, out OpenRouterAccount? cached))
            return cached;

        var acct = await FetchAsync(apiKey, ct);
        if (acct is not null) _cache.Set(CacheKey, acct, CacheTtl);
        return acct;
    }

    private async Task<OpenRouterAccount?> FetchAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "key");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var d = doc.RootElement.GetProperty("data");

            return new OpenRouterAccount(
                IsFreeTier: d.TryGetProperty("is_free_tier", out var ft) && ft.ValueKind == JsonValueKind.True,
                Limit: GetNullableDouble(d, "limit"),
                LimitRemaining: GetNullableDouble(d, "limit_remaining"),
                Usage: GetNullableDouble(d, "usage") ?? 0,
                UsageDaily: GetNullableDouble(d, "usage_daily") ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch OpenRouter account info.");
            return null;
        }
    }

    private static double? GetNullableDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;
}
