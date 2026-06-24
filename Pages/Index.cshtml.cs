using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Pages;

public class IndexModel : PageModel
{
    private const int PageSize = 30;
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db) => _db = db;

    public IReadOnlyList<Post> Posts { get; private set; } = Array.Empty<Post>();
    public int CurrentPage { get; private set; } = 1;
    public bool HasNext { get; private set; }

    public int TotalCount { get; private set; }
    public int TranslatedCount { get; private set; }
    public int PendingCount { get; private set; }
    public DateTimeOffset? LastFetchUtc { get; private set; }

    // NOTE: the parameter is `p`, not `page`. In Razor Pages `page` is a reserved
    // route-value name (it identifies the page path), so a `?page=N` query never
    // binds to a handler parameter named `page` — pagination silently breaks.
    public async Task OnGetAsync(int p = 1)
    {
        CurrentPage = Math.Max(1, p);

        TotalCount = await _db.Posts.CountAsync();
        TranslatedCount = await _db.Posts.CountAsync(p => p.Status == TranslationStatus.Translated);
        PendingCount = await _db.Posts.CountAsync(p => p.Status == TranslationStatus.Pending);

        var state = await _db.FeedStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1);
        LastFetchUtc = state?.LastFetchUtc;

        var rows = await _db.Posts.AsNoTracking()
            .Where(p => p.Status == TranslationStatus.Translated)
            .OrderByDescending(p => p.Published)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize + 1) // peek one extra to know if there's a next page
            .ToListAsync();

        HasNext = rows.Count > PageSize;
        Posts = HasNext ? rows.Take(PageSize).ToList() : rows;
    }
}
