using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Data;

namespace v2en.Services;

/// <summary>One ordered translation attempt: which provider + model (+ reasoning for ChatGPT) to use.</summary>
public record TranslationStep(string Provider, string Model, string? Reasoning);

/// <summary>
/// Loads and persists the single <see cref="RuntimeSettings"/> row. On first run the row is
/// seeded from appsettings/Options so the dashboard starts with sensible values; after that the
/// DB is the source of truth and the worker reads it each tick.
/// </summary>
public class RuntimeSettingsService
{
    private readonly AppDbContext _db;
    private readonly OpenRouterOptions _or;
    private readonly TranslationOptions _t;
    private readonly GeminiOptions _g;

    public RuntimeSettingsService(
        AppDbContext db,
        IOptions<OpenRouterOptions> or,
        IOptions<TranslationOptions> t,
        IOptions<GeminiOptions> g)
    {
        _db = db;
        _or = or.Value;
        _t = t.Value;
        _g = g.Value;
    }

    /// <summary>Get the settings row, creating it (seeded from Options) if it doesn't exist yet.</summary>
    public async Task<RuntimeSettings> GetAsync(CancellationToken ct = default)
    {
        var s = await _db.RuntimeSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (s is not null) return s;

        s = new RuntimeSettings
        {
            Id = 1,
            ModelsJson = SerializeModels(_or.Models),
            DailyQuota = _t.DailyQuota,
            MaxPerTick = _t.MaxPerTick,
            MinDelaySecondsBetweenCalls = _t.MinDelaySecondsBetweenCalls,
            MaxAttempts = _t.MaxAttempts,
            MaxOutputTokens = _t.MaxOutputTokens,
            Temperature = _t.Temperature,
            // Seed keys/models from config/env on first run; the DB is the source of truth after.
            OpenRouterApiKey = _or.ApiKey ?? "",
            GeminiEmbedKeysJson = SerializeEmbedKeys(_g.EmbedKeys),
            EmbeddingModel = _g.EmbeddingModel,
            EmbeddingDim = _g.EmbeddingDim,
            ChatModel = string.IsNullOrWhiteSpace(_g.ChatModel) ? "gemini-2.5-flash" : _g.ChatModel,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        _db.RuntimeSettings.Add(s);
        await _db.SaveChangesAsync(ct);
        return s;
    }

    /// <summary>Canonical provider ids the routing understands.</summary>
    public const string ProviderOpenRouter = "openrouter";
    public const string ProviderChatGpt = "chatgpt";

    /// <summary>
    /// Build the ordered primary→fallback translation steps from the settings. A slot is only included
    /// when it names a known provider AND a model. Returns an empty list when neither slot is configured,
    /// which signals the caller to fall back to the legacy OpenRouter free-model chain in
    /// <see cref="RuntimeSettings.ModelsJson"/> (so an upgraded site keeps translating unchanged).
    /// </summary>
    public static List<TranslationStep> BuildTranslationSteps(RuntimeSettings s)
    {
        var steps = new List<TranslationStep>();
        AddStep(steps, s.TranslationPrimaryProvider, s.TranslationPrimaryModel, s.TranslationPrimaryReasoning);
        AddStep(steps, s.TranslationFallbackProvider, s.TranslationFallbackModel, s.TranslationFallbackReasoning);
        return steps;
    }

    private static void AddStep(List<TranslationStep> steps, string? provider, string? model, string? reasoning)
    {
        var p = (provider ?? "").Trim().ToLowerInvariant();
        var m = (model ?? "").Trim();
        if ((p == ProviderOpenRouter || p == ProviderChatGpt) && m.Length > 0)
            steps.Add(new TranslationStep(p, m, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning.Trim().ToLowerInvariant()));
    }

    public static List<string> ParseModels(RuntimeSettings s)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(s.ModelsJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static string SerializeModels(IEnumerable<string> models) =>
        JsonSerializer.Serialize(models
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .ToList());

    public static List<string> ParseEmbedKeys(RuntimeSettings s)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(s.GeminiEmbedKeysJson) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static string SerializeEmbedKeys(IEnumerable<string> keys) =>
        JsonSerializer.Serialize(keys
            .Select(k => k.Trim())
            .Where(k => k.Length > 0)
            .ToList());
}
