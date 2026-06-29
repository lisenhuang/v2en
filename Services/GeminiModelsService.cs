using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

public record GeminiModel(string Id, string DisplayName, IReadOnlyList<string> Methods)
{
    public bool SupportsEmbedding => Methods.Any(m => m.Equals("embedContent", StringComparison.OrdinalIgnoreCase));
    public bool SupportsGenerate => Methods.Any(m => m.Equals("generateContent", StringComparison.OrdinalIgnoreCase));
    // Live / real-time audio models expose the bidirectional streaming method.
    public bool SupportsLive => Methods.Any(m => m.Equals("bidiGenerateContent", StringComparison.OrdinalIgnoreCase));

    // A handful of generateContent models emit images / audio / video instead of chat text
    // (image generation, text-to-speech, video). The "ask the feed" chat only consumes text, so
    // those must be kept out of the chat-model list even though they technically support generateContent.
    public bool IsTextGeneration => SupportsGenerate && !LooksNonTextGeneration;

    private bool LooksNonTextGeneration
    {
        get
        {
            var s = (Id + " " + DisplayName).ToLowerInvariant();
            return s.Contains("image")
                || s.Contains("imagen")
                || s.Contains("tts")
                || s.Contains("text-to-speech")
                || s.Contains("native-audio")
                || s.Contains("audio")
                || s.Contains("veo")
                || s.Contains("video");
        }
    }
}

public record GeminiModelLists(
    IReadOnlyList<GeminiModel> Embedding,
    IReadOnlyList<GeminiModel> Chat,
    IReadOnlyList<GeminiModel> Live);

/// <summary>
/// Lists models live from the Gemini API (GET /v1beta/models), split into embedding-,
/// generation-, and live (bidi/real-time-audio)-capable lists for the dropdowns. Cached briefly
/// (per key) so a page can be opened repeatedly. Models are never hardcoded.
/// </summary>
public class GeminiModelsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeminiModelsService> _logger;

    public GeminiModelsService(HttpClient http, IMemoryCache cache, ILogger<GeminiModelsService> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<GeminiModelLists> GetModelsAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new GeminiModelLists(Array.Empty<GeminiModel>(), Array.Empty<GeminiModel>(), Array.Empty<GeminiModel>());

        // Cache per-key, not globally: different keys may have access to different models, and this
        // method is now also called with each PUBLIC visitor's own key (chat model picker), so one
        // visitor's list must never be served to another. The key itself is never used as the cache
        // key — only a short hash of it.
        var cacheKey = CacheKeyFor(apiKey);
        if (_cache.TryGetValue(cacheKey, out GeminiModelLists? cached) && cached is not null)
            return cached;

        var lists = await FetchAsync(apiKey, ct);
        if (lists.Embedding.Count > 0 || lists.Chat.Count > 0 || lists.Live.Count > 0)
            _cache.Set(cacheKey, lists, CacheTtl);
        return lists;
    }

    private static string CacheKeyFor(string apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return "gemini_models:" + Convert.ToHexString(hash, 0, 8);
    }

    private async Task<GeminiModelLists> FetchAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "v1beta/models?pageSize=200");
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var all = new List<GeminiModel>();
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() : null; // "models/gemini-…"
                    if (string.IsNullOrEmpty(name)) continue;
                    var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                    var display = m.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id;

                    var methods = new List<string>();
                    if (m.TryGetProperty("supportedGenerationMethods", out var sm) && sm.ValueKind == JsonValueKind.Array)
                        foreach (var s in sm.EnumerateArray())
                            if (s.GetString() is { } str) methods.Add(str);

                    all.Add(new GeminiModel(id, display, methods));
                }
            }

            return new GeminiModelLists(
                Embedding: all.Where(m => m.SupportsEmbedding).OrderBy(m => m.Id).ToList(),
                Chat: all.Where(m => m.IsTextGeneration).OrderBy(m => m.Id).ToList(),
                Live: all.Where(m => m.SupportsLive).OrderBy(m => m.Id).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Gemini model list.");
            return new GeminiModelLists(Array.Empty<GeminiModel>(), Array.Empty<GeminiModel>(), Array.Empty<GeminiModel>());
        }
    }
}
