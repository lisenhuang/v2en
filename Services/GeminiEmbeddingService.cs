using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using v2en.Utilities;

namespace v2en.Services;

public record EmbedOutcome(bool Success, float[]? Vector, bool AllKeysExhausted, string? Model, int? HttpStatus, string? Error);

/// <summary>
/// Embeds ONE text via Gemini using a server-side key pool, rotating to the next key on a
/// rate-limit/auth error. If every key is rate-limited/exhausted, returns
/// <see cref="EmbedOutcome.AllKeysExhausted"/> so the caller defers (no attempt charged) — the
/// same semantics as the OpenRouter translator's account-level 429. Vectors are unit-normalized.
/// </summary>
public class GeminiEmbeddingService
{
    private readonly HttpClient _http;
    private readonly GeminiKeyCursor _cursor;
    private readonly ILogger<GeminiEmbeddingService> _logger;

    public GeminiEmbeddingService(HttpClient http, GeminiKeyCursor cursor, ILogger<GeminiEmbeddingService> logger)
    {
        _http = http;
        _cursor = cursor;
        _logger = logger;
    }

    /// <param name="taskType">RETRIEVAL_DOCUMENT when indexing posts, RETRIEVAL_QUERY when searching.</param>
    public async Task<EmbedOutcome> EmbedAsync(
        string text, string model, int dim, IReadOnlyList<string> keys, string taskType, CancellationToken ct)
    {
        if (keys.Count == 0)
            return new EmbedOutcome(false, null, AllKeysExhausted: true, model, null, "No embedding keys configured.");
        if (string.IsNullOrWhiteSpace(model))
            return new EmbedOutcome(false, null, AllKeysExhausted: false, model, null, "No embedding model selected.");

        var body = new
        {
            model = $"models/{model}",
            content = new { parts = new[] { new { text = text } } },
            taskType,
            outputDimensionality = dim,
        };

        int n = keys.Count;
        int start = _cursor.Next(n);
        string? lastError = null;
        int? lastStatus = null;

        for (int k = 0; k < n; k++)
        {
            var keyIndex = (start + k) % n;
            var key = keys[keyIndex];
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{model}:embedContent")
                {
                    Content = JsonContent.Create(body),
                };
                req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                using var resp = await _http.SendAsync(req, ct);
                var status = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var values = doc.RootElement.GetProperty("embedding").GetProperty("values");
                    var vec = new float[values.GetArrayLength()];
                    int i = 0;
                    foreach (var v in values.EnumerateArray()) vec[i++] = (float)v.GetDouble();
                    return new EmbedOutcome(true, VectorBytes.Normalize(vec), false, model, status, null);
                }

                var err = await SafeBodyAsync(resp, ct);
                lastStatus = status;
                lastError = Clip(err);

                // Rate-limit / auth → try the next key. Other 4xx (e.g. 400 bad input) → real failure.
                if (resp.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("Gemini embed key #{Index} returned {Status}; rotating.", keyIndex, status);
                    continue;
                }

                return new EmbedOutcome(false, null, AllKeysExhausted: false, model, status, lastError);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Gemini embed call failed on key #{Index}; rotating.", keyIndex);
            }
        }

        // Every key was rate-limited / errored — transient, defer to next tick.
        return new EmbedOutcome(false, null, AllKeysExhausted: true, model, lastStatus, lastError);
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 1000 ? s : s[..1000]);
}
