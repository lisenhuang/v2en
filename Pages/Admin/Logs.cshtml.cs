using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Pages.Admin;

public class LogsModel : PageModel
{
    private readonly AppDbContext _db;

    public LogsModel(AppDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public string Filter { get; set; } = "all"; // all | issues | error
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public List<TranslationLog> Logs { get; private set; } = new();
    public int Total { get; private set; }
    public int TotalPages { get; private set; }
    public const int PageSize = 50;

    public async Task OnGetAsync(CancellationToken ct)
    {
        IQueryable<TranslationLog> q = _db.TranslationLogs.AsNoTracking();
        if (Filter == "error")
            q = q.Where(l => l.Level == LogSeverity.Error);
        else if (Filter == "issues")
            q = q.Where(l => l.Level == LogSeverity.Warning || l.Level == LogSeverity.Error);

        Total = await q.CountAsync(ct);
        TotalPages = Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
        P = Math.Clamp(P, 1, TotalPages);
        Logs = await q.OrderByDescending(l => l.Utc)
            .Skip((P - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostClearAsync(CancellationToken ct)
    {
        await _db.TranslationLogs.ExecuteDeleteAsync(ct);
        return RedirectToPage(new { Filter });
    }
}
