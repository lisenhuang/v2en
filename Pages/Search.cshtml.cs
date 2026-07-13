using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Pages;

/// <summary>
/// Plain keyword search over the mirrored posts — a relational LIKE query on the translated title,
/// original title, and translated body. Deliberately NOT the embedding/vector search used by /ask:
/// it needs no API key, runs entirely server-side, and matches literal words. Only Translated posts
/// are shown, ordered newest-first, and paginated like the home page.
/// </summary>
public class SearchModel : PageModel
{
    private const int PageSize = 30;
    private const int MaxQueryLen = 100;
    private readonly AppDbContext _db;

    public SearchModel(AppDbContext db) => _db = db;

    public string Query { get; private set; } = "";
    public bool Searched { get; private set; }
    public IReadOnlyList<Post> Posts { get; private set; } = Array.Empty<Post>();
    public int CurrentPage { get; private set; } = 1;
    public bool HasNext { get; private set; }
    public int ResultCount { get; private set; }

    // `page` is reserved by Razor Pages routing, so pagination uses `p` (same as the home page).
    public async Task OnGetAsync(string? q, int p = 1, CancellationToken ct = default)
    {
        Query = (q ?? "").Trim();
        if (Query.Length > MaxQueryLen) Query = Query[..MaxQueryLen];
        CurrentPage = Math.Max(1, p);
        if (Query.Length == 0) return;

        Searched = true;
        var term = Query.ToLower();

        var matches = _db.Posts.AsNoTracking()
            .Where(x => x.Status == TranslationStatus.Translated && (
                (x.TitleEn != null && x.TitleEn.ToLower().Contains(term)) ||
                x.TitleZh.ToLower().Contains(term) ||
                (x.ContentEnHtml != null && x.ContentEnHtml.ToLower().Contains(term))))
            .OrderByDescending(x => x.Published);

        ResultCount = await matches.CountAsync(ct);
        var rows = await matches
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize + 1) // peek one extra to know if there's a next page
            .ToListAsync(ct);

        HasNext = rows.Count > PageSize;
        Posts = HasNext ? rows.Take(PageSize).ToList() : rows;
    }
}
