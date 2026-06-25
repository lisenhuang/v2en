using Microsoft.AspNetCore.Mvc.RazorPages;
using v2en.Services;

namespace v2en.Pages;

public class LiveModel : PageModel
{
    private readonly RuntimeSettingsService _settings;

    public LiveModel(RuntimeSettingsService settings) => _settings = settings;

    public bool Enabled { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        // Reuses the public-chat master switch — no separate DB setting needed.
        Enabled = cfg.EnableChat;
    }
}
