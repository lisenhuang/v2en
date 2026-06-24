using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Data;

namespace v2en.Services;

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
