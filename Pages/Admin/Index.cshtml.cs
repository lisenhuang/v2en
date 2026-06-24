using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;

namespace v2en.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RuntimeSettingsService _settings;
    private readonly OpenRouterAccountService _account;

    public IndexModel(AppDbContext db, RuntimeSettingsService settings, OpenRouterAccountService account)
    {
        _db = db;
        _settings = settings;
        _account = account;
    }

    public int Pending, Translated, Failed, Total;
    public FeedState State = new() { Id = 1 };
    public RuntimeSettings Cfg = new();
    public List<string> Models = new();
    public OpenRouterAccount? Account;
    public List<TranslationLog> RecentIssues = new();

    public int UsedToday => State.TranslationsToday;
    public int? FreeRemaining => Account is null ? null : Math.Max(0, Account.FreeDailyRequestLimit - UsedToday);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Pending = await _db.Posts.CountAsync(p => p.Status == TranslationStatus.Pending, ct);
        Translated = await _db.Posts.CountAsync(p => p.Status == TranslationStatus.Translated, ct);
        Failed = await _db.Posts.CountAsync(p => p.Status == TranslationStatus.Failed, ct);
        Total = await _db.Posts.CountAsync(ct);

        State = await _db.FeedStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct) ?? new FeedState { Id = 1 };
        Cfg = await _settings.GetAsync(ct);
        Models = RuntimeSettingsService.ParseModels(Cfg);

        RecentIssues = await _db.TranslationLogs.AsNoTracking()
            .Where(l => l.Level == LogSeverity.Warning || l.Level == LogSeverity.Error)
            .OrderByDescending(l => l.Utc)
            .Take(6)
            .ToListAsync(ct);

        Account = await _account.GetAsync(ct);
    }
}
