using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

public record OpenRouterModel(string Id, string Name, long? ContextLength);

/// <summary>
/// Fetches the live model catalogue from OpenRouter and returns only the genuinely-free
/// ":free" models (prompt + completion priced at 0). Result is cached briefly so the admin
/// settings page can be opened repeatedly without hammering the API.
/// </summary>
public class OpenRouterModelsService
{
    private const string CacheKey = "openrouter_free_models";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<OpenRouterModelsService> _logger;

    public OpenRouterModelsService(HttpClient http, IMemoryCache cache, ILogger<OpenRouterModelsService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OpenRouterModel>> GetFreeModelsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<OpenRouterModel>? cached) && cached is not null)
            return cached;

        var models = await FetchFreeModelsAsync(ct);
        _cache.Set(CacheKey, models, CacheTtl);
        return models;
    }

    private async Task<IReadOnlyList<OpenRouterModel>> FetchFreeModelsAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync("models", ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var list = new List<OpenRouterModel>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (string.IsNullOrEmpty(id) || !id.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Confirm it's actually $0 — guard against ":free" ids that start charging.
                    if (m.TryGetProperty("pricing", out var pr))
                    {
                        var prompt = pr.TryGetProperty("prompt", out var p) ? p.GetString() : "0";
                        var completion = pr.TryGetProperty("completion", out var c) ? c.GetString() : "0";
                        if (!IsZero(prompt) || !IsZero(completion)) continue;
                    }

                    var name = m.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? id : id;
                    long? ctx = m.TryGetProperty("context_length", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number
                        ? ctxEl.GetInt64()
                        : null;
                    list.Add(new OpenRouterModel(id, name, ctx));
                }
            }

            return list.OrderByDescending(x => x.ContextLength ?? 0).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch OpenRouter model list.");
            return Array.Empty<OpenRouterModel>();
        }
    }

    private static bool IsZero(string? price) =>
        string.IsNullOrEmpty(price) || price == "0" || double.TryParse(price, out var v) && v == 0;
}
