using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Utilities;

namespace v2en.Services;

public enum TimeWindow { Last24h, Last7d, Last30d, All }

public record RetrievedPost(
    int PostId, long V2exId, string TitleZh, string? TitleEn,
    string SourceUrl, DateTimeOffset Published, string Snippet, double Score)
{
    /// <summary>Best title to show/cite — English when translated, else the Chinese original.</summary>
    public string Title => string.IsNullOrWhiteSpace(TitleEn) ? TitleZh : TitleEn!;
}

public record RetrievalResult(IReadOnlyList<RetrievedPost> Hits, string? Error);

/// <summary>
/// Embeds the question (same Gemini model/dim/key-pool as the documents → same vector space),
/// filters cached post vectors by the time window, and returns the top-K by cosine similarity
/// (dot product, since vectors are unit-normalized). Uses <see cref="VectorCache"/> to avoid
/// loading every BLOB from SQLite per query.
/// </summary>
public class RetrievalService
{
    private readonly AppDbContext _db;
    private readonly GeminiEmbeddingService _embed;
    private readonly VectorCache _cache;

    public RetrievalService(AppDbContext db, GeminiEmbeddingService embed, VectorCache cache)
    {
        _db = db;
        _embed = embed;
        _cache = cache;
    }

    public static TimeWindow ParseWindow(string? w) => (w ?? "").ToLowerInvariant() switch
    {
        "24h" or "1d" => TimeWindow.Last24h,
        "7d" or "week" => TimeWindow.Last7d,
        "30d" or "month" => TimeWindow.Last30d,
        _ => TimeWindow.All,
    };

    public async Task<RetrievalResult> SearchAsync(
        string question, string model, int dim, TimeWindow window, int topK,
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        var q = await _embed.EmbedAsync(question, model, dim, keys, "RETRIEVAL_QUERY", ct);
        if (!q.Success || q.Vector is null)
        {
            var msg = q.AllKeysExhausted ? "Search is busy right now, please try again shortly." : "Search isn't available right now.";
            return new RetrievalResult(Array.Empty<RetrievedPost>(), msg);
        }

        var cutoffTicks = CutoffTicks(window);
        var vectors = await _cache.GetAsync(_db, ct);

        // Only compare vectors in the SAME space as the query (same model + dim).
        var scored = new List<(int PostId, double Score)>();
        foreach (var v in vectors)
        {
            if (v.Dim != dim || !string.Equals(v.Model, model, StringComparison.OrdinalIgnoreCase)) continue;
            if (v.PublishedUtcTicks < cutoffTicks) continue;
            scored.Add((v.PostId, VectorBytes.Dot(q.Vector, v.Vector)));
        }

        if (scored.Count == 0)
            return new RetrievalResult(Array.Empty<RetrievedPost>(), null);

        var topIds = scored
            .OrderByDescending(s => s.Score)
            .Take(Math.Max(1, topK))
            .ToList();

        var idSet = topIds.Select(t => t.PostId).ToList();
        var meta = await _db.Posts.AsNoTracking()
            .Where(p => idSet.Contains(p.Id))
            .Select(p => new { p.Id, p.V2exId, p.TitleZh, p.TitleEn, p.SourceUrl, p.Published, p.ContentEnHtml, p.ContentZhHtml })
            .ToDictionaryAsync(p => p.Id, ct);

        var hits = topIds
            .Where(t => meta.ContainsKey(t.PostId))
            .Select(t =>
            {
                var m = meta[t.PostId];
                // Prefer the English translation for the prompt when available, else the Chinese original.
                var snippet = HtmlText.Preview(
                    string.IsNullOrWhiteSpace(m.ContentEnHtml) ? m.ContentZhHtml : m.ContentEnHtml, 600);
                return new RetrievedPost(m.Id, m.V2exId, m.TitleZh, m.TitleEn, m.SourceUrl, m.Published, snippet, t.Score);
            })
            .ToList();

        return new RetrievalResult(hits, null);
    }

    private static long CutoffTicks(TimeWindow window)
    {
        if (window == TimeWindow.All) return long.MinValue;
        var now = DateTimeOffset.UtcNow;
        var cutoff = window switch
        {
            TimeWindow.Last24h => now.AddHours(-24),
            TimeWindow.Last7d => now.AddDays(-7),
            TimeWindow.Last30d => now.AddDays(-30),
            _ => DateTimeOffset.MinValue,
        };
        return cutoff.UtcTicks;
    }
}
