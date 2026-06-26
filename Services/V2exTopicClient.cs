using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

public record V2exReply(string Author, string Text, DateTimeOffset Created);

/// <summary>
/// Best-effort fetch of a V2EX topic's replies via the legacy public JSON API
/// (https://www.v2ex.com/api/replies/show.json?topic_id=) — NO auth token required.
/// V2EX may rate-limit or deprecate this endpoint at any time; on ANY failure we return an empty
/// list so callers degrade cleanly to "post body only". Results (including empties) are cached
/// briefly so a blocked endpoint isn't hammered once per question.
/// </summary>
public class V2exTopicClient
{
    private const int MaxReplies = 40;       // cap context size fed to the model
    private const int MaxReplyChars = 600;   // per-reply cap
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<V2exTopicClient> _logger;

    public V2exTopicClient(HttpClient http, IMemoryCache cache, ILogger<V2exTopicClient> logger)
    {
        _http = http;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<V2exReply>> GetRepliesAsync(long v2exId, CancellationToken ct)
    {
        if (v2exId <= 0) return Array.Empty<V2exReply>();
        var cacheKey = $"v2ex_replies:{v2exId}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<V2exReply>? cached) && cached is not null)
            return cached;

        var replies = await FetchAsync(v2exId, ct);
        _cache.Set(cacheKey, replies, CacheTtl);
        return replies;
    }

    private async Task<IReadOnlyList<V2exReply>> FetchAsync(long v2exId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"api/replies/show.json?topic_id={v2exId}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "V2EX replies fetch for {Id} returned HTTP {Status}; surfacing no replies.", v2exId, (int)resp.StatusCode);
                return Array.Empty<V2exReply>();
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<V2exReply>();

            var list = new List<V2exReply>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= MaxReplies) break;

                var content = (el.TryGetProperty("content", out var c) ? c.GetString() : null)?.Trim();
                if (string.IsNullOrEmpty(content)) continue;
                if (content.Length > MaxReplyChars) content = content[..MaxReplyChars];

                var author = "";
                if (el.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.Object &&
                    m.TryGetProperty("username", out var u))
                    author = u.GetString() ?? "";

                var created = DateTimeOffset.MinValue;
                if (el.TryGetProperty("created", out var cr) && cr.TryGetInt64(out var unix))
                    created = DateTimeOffset.FromUnixTimeSeconds(unix);

                list.Add(new V2exReply(author, content, created));
            }

            _logger.LogInformation("V2EX replies fetched for {Id}: {Count}.", v2exId, list.Count);
            return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "V2EX replies fetch for {Id} failed; surfacing no replies.", v2exId);
            return Array.Empty<V2exReply>();
        }
    }
}
