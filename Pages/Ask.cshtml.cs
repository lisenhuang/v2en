using Microsoft.AspNetCore.Mvc.RazorPages;
using v2en.Services;

namespace v2en.Pages;

public class AskModel : PageModel
{
    private readonly RuntimeSettingsService _settings;

    public AskModel(RuntimeSettingsService settings) => _settings = settings;

    public bool Enabled { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        Enabled = cfg.EnableChat;
    }
}
