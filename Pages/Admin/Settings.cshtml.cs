using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using v2en.Data;
using v2en.Services;

namespace v2en.Pages.Admin;

public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RuntimeSettingsService _settings;
    private readonly OpenRouterModelsService _models;

    public SettingsModel(AppDbContext db, RuntimeSettingsService settings, OpenRouterModelsService models)
    {
        _db = db;
        _settings = settings;
        _models = models;
    }

    // Newline-separated ordered model chain (one ":free" id per line).
    [BindProperty] public string ModelsText { get; set; } = "";
    [BindProperty] public bool UnlimitedDaily { get; set; }
    [BindProperty] public int DailyQuota { get; set; }
    [BindProperty] public int MaxPerTick { get; set; }
    [BindProperty] public int MinDelaySecondsBetweenCalls { get; set; }
    [BindProperty] public int MaxAttempts { get; set; }
    [BindProperty] public int MaxOutputTokens { get; set; }
    [BindProperty] public double Temperature { get; set; }

    public IReadOnlyList<OpenRouterModel> FreeModels { get; private set; } = Array.Empty<OpenRouterModel>();
    public List<string> SelectedModels { get; private set; } = new();
    public bool Saved { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        SelectedModels = RuntimeSettingsService.ParseModels(cfg);
        ModelsText = string.Join("\n", SelectedModels);
        UnlimitedDaily = cfg.UnlimitedDaily;
        DailyQuota = cfg.DailyQuota;
        MaxPerTick = cfg.MaxPerTick;
        MinDelaySecondsBetweenCalls = cfg.MinDelaySecondsBetweenCalls;
        MaxAttempts = cfg.MaxAttempts;
        MaxOutputTokens = cfg.MaxOutputTokens;
        Temperature = cfg.Temperature;
        FreeModels = await _models.GetFreeModelsAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Free-only policy: keep just ":free" ids, preserving order and removing dupes.
        var models = (ModelsText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => m.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (models.Count == 0)
        {
            Error = "Select at least one free (:free) model.";
            SelectedModels = (ModelsText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            FreeModels = await _models.GetFreeModelsAsync(ct);
            return Page();
        }

        var cfg = await _settings.GetAsync(ct);
        cfg.ModelsJson = RuntimeSettingsService.SerializeModels(models);
        cfg.UnlimitedDaily = UnlimitedDaily;
        cfg.DailyQuota = Math.Clamp(DailyQuota, 1, 100_000);
        cfg.MaxPerTick = Math.Clamp(MaxPerTick, 1, 100);
        cfg.MinDelaySecondsBetweenCalls = Math.Clamp(MinDelaySecondsBetweenCalls, 0, 300);
        cfg.MaxAttempts = Math.Clamp(MaxAttempts, 1, 20);
        cfg.MaxOutputTokens = Math.Clamp(MaxOutputTokens, 256, 32_000);
        cfg.Temperature = Math.Clamp(Temperature, 0, 2);
        cfg.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Re-render with saved values.
        SelectedModels = models;
        ModelsText = string.Join("\n", models);
        DailyQuota = cfg.DailyQuota;
        MaxPerTick = cfg.MaxPerTick;
        MinDelaySecondsBetweenCalls = cfg.MinDelaySecondsBetweenCalls;
        MaxAttempts = cfg.MaxAttempts;
        MaxOutputTokens = cfg.MaxOutputTokens;
        Temperature = cfg.Temperature;
        FreeModels = await _models.GetFreeModelsAsync(ct);
        Saved = true;
        return Page();
    }
}
