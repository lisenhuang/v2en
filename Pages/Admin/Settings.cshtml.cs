using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;

namespace v2en.Pages.Admin;

public class SettingsModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly RuntimeSettingsService _settings;
    private readonly OpenRouterModelsService _orModels;
    private readonly GeminiModelsService _gModels;
    private readonly VectorCache _vectorCache;

    public SettingsModel(AppDbContext db, RuntimeSettingsService settings,
        OpenRouterModelsService orModels, GeminiModelsService gModels, VectorCache vectorCache)
    {
        _db = db;
        _settings = settings;
        _orModels = orModels;
        _gModels = gModels;
        _vectorCache = vectorCache;
    }

    // ── Translation (OpenRouter) ──
    [BindProperty] public string ModelsText { get; set; } = "";
    [BindProperty] public bool UnlimitedDaily { get; set; }
    [BindProperty] public int DailyQuota { get; set; }
    [BindProperty] public int MaxPerTick { get; set; }
    [BindProperty] public int MinDelaySecondsBetweenCalls { get; set; }
    [BindProperty] public int MaxAttempts { get; set; }
    [BindProperty] public int MaxOutputTokens { get; set; }
    [BindProperty] public double Temperature { get; set; }

    // ── API keys (blank submit = keep existing) ──
    [BindProperty] public string? OpenRouterApiKey { get; set; }
    [BindProperty] public string? GeminiEmbedKeysText { get; set; }

    // ── Embedding ──
    [BindProperty] public string EmbeddingModel { get; set; } = "";
    [BindProperty] public int EmbeddingDim { get; set; }
    [BindProperty] public int EmbedMaxPerTick { get; set; }
    [BindProperty] public int EmbedMaxAttempts { get; set; }

    // ── Chat / retrieval ──
    [BindProperty] public bool EnableChat { get; set; }
    [BindProperty] public string ChatModel { get; set; } = "";
    [BindProperty] public int RetrievalTopK { get; set; }
    [BindProperty] public int ChatMaxContextPosts { get; set; }
    [BindProperty] public int ChatRateLimitPerMinutePerIp { get; set; }

    // ── Display-only ──
    public IReadOnlyList<OpenRouterModel> FreeModels { get; private set; } = Array.Empty<OpenRouterModel>();
    public List<string> SelectedModels { get; private set; } = new();
    public IReadOnlyList<GeminiModel> EmbeddingModelOptions { get; private set; } = Array.Empty<GeminiModel>();
    public IReadOnlyList<GeminiModel> ChatModelOptions { get; private set; } = Array.Empty<GeminiModel>();
    public bool OpenRouterKeySet { get; private set; }
    public int GeminiKeyCount { get; private set; }
    public int EmbeddedCount { get; private set; }
    public bool EmbeddingLocked => EmbeddedCount > 0;  // model/dim fixed once posts are indexed
    public bool Saved { get; set; }
    public string? Error { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        BindFrom(cfg);
        await LoadDisplayAsync(cfg, ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);

        // Translation models (free-only, keep order, dedupe). Empty is allowed (translation just pauses).
        var models = (ModelsText ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(m => m.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.ModelsJson = RuntimeSettingsService.SerializeModels(models);
        cfg.UnlimitedDaily = UnlimitedDaily;
        cfg.DailyQuota = Math.Clamp(DailyQuota, 1, 100_000);
        cfg.MaxPerTick = Math.Clamp(MaxPerTick, 1, 100);
        cfg.MinDelaySecondsBetweenCalls = Math.Clamp(MinDelaySecondsBetweenCalls, 0, 300);
        cfg.MaxAttempts = Math.Clamp(MaxAttempts, 1, 20);
        cfg.MaxOutputTokens = Math.Clamp(MaxOutputTokens, 256, 32_000);
        cfg.Temperature = Math.Clamp(Temperature, 0, 2);

        // Keys: only overwrite when a value was actually entered (blank = keep current).
        if (!string.IsNullOrWhiteSpace(OpenRouterApiKey))
            cfg.OpenRouterApiKey = OpenRouterApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(GeminiEmbedKeysText))
        {
            var keys = GeminiEmbedKeysText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal);
            cfg.GeminiEmbedKeysJson = RuntimeSettingsService.SerializeEmbedKeys(keys);
        }

        // Embedding. The model + dimensions define the vector space, so once any post is indexed
        // they are LOCKED (changing them would make existing vectors uncomparable). Use "Clear
        // index" to change. EmbedMaxPerTick/Attempts are always safe to change.
        var epoch = DateTimeOffset.UnixEpoch;
        if (await _db.PostEmbeddings.AnyAsync(e => e.EmbeddedAt > epoch, ct) == false)
        {
            cfg.EmbeddingModel = (EmbeddingModel ?? "").Trim();
            cfg.EmbeddingDim = Math.Clamp(EmbeddingDim, 128, 3072);
        }
        cfg.EmbedMaxPerTick = Math.Clamp(EmbedMaxPerTick, 1, 200);
        cfg.EmbedMaxAttempts = Math.Clamp(EmbedMaxAttempts, 1, 20);

        // Chat / retrieval.
        cfg.EnableChat = EnableChat;
        cfg.ChatModel = string.IsNullOrWhiteSpace(ChatModel) ? RuntimeSettings.DefaultChatModel : ChatModel.Trim();
        cfg.RetrievalTopK = Math.Clamp(RetrievalTopK, 1, 50);
        cfg.ChatMaxContextPosts = Math.Clamp(ChatMaxContextPosts, 1, 30);
        cfg.ChatRateLimitPerMinutePerIp = Math.Clamp(ChatRateLimitPerMinutePerIp, 1, 120);

        cfg.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        BindFrom(cfg);
        await LoadDisplayAsync(cfg, ct);
        Saved = true;
        return Page();
    }

    private void BindFrom(RuntimeSettings cfg)
    {
        SelectedModels = RuntimeSettingsService.ParseModels(cfg);
        ModelsText = string.Join("\n", SelectedModels);
        UnlimitedDaily = cfg.UnlimitedDaily;
        DailyQuota = cfg.DailyQuota;
        MaxPerTick = cfg.MaxPerTick;
        MinDelaySecondsBetweenCalls = cfg.MinDelaySecondsBetweenCalls;
        MaxAttempts = cfg.MaxAttempts;
        MaxOutputTokens = cfg.MaxOutputTokens;
        Temperature = cfg.Temperature;

        EmbeddingModel = cfg.EmbeddingModel;
        EmbeddingDim = cfg.EmbeddingDim;
        EmbedMaxPerTick = cfg.EmbedMaxPerTick;
        EmbedMaxAttempts = cfg.EmbedMaxAttempts;

        EnableChat = cfg.EnableChat;
        ChatModel = cfg.ChatModel;
        RetrievalTopK = cfg.RetrievalTopK;
        ChatMaxContextPosts = cfg.ChatMaxContextPosts;
        ChatRateLimitPerMinutePerIp = cfg.ChatRateLimitPerMinutePerIp;

        // Never echo the secrets back into the inputs.
        OpenRouterApiKey = null;
        GeminiEmbedKeysText = null;
    }

    public async Task<IActionResult> OnPostClearIndexAsync(CancellationToken ct)
    {
        await _db.PostEmbeddings.ExecuteDeleteAsync(ct);
        _vectorCache.Invalidate();
        return RedirectToPage();
    }

    private async Task LoadDisplayAsync(RuntimeSettings cfg, CancellationToken ct)
    {
        OpenRouterKeySet = !string.IsNullOrWhiteSpace(cfg.OpenRouterApiKey);
        GeminiKeyCount = RuntimeSettingsService.ParseEmbedKeys(cfg).Count;
        var embEpoch = DateTimeOffset.UnixEpoch;
        EmbeddedCount = await _db.PostEmbeddings.CountAsync(e => e.EmbeddedAt > embEpoch, ct);

        FreeModels = await _orModels.GetFreeModelsAsync(cfg.OpenRouterApiKey, ct);

        var geminiKey = RuntimeSettingsService.ParseEmbedKeys(cfg).FirstOrDefault();
        var lists = await _gModels.GetModelsAsync(geminiKey, ct);
        EmbeddingModelOptions = lists.Embedding;
        ChatModelOptions = lists.Chat;
    }
}
