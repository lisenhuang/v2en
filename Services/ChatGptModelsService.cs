using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace v2en.Services;

/// <summary>
/// One ChatGPT/Codex model the admin can pick for translation, with the reasoning-effort levels it
/// accepts. Effort levels differ per model, which is why they're carried here rather than assumed.
/// </summary>
public record ChatGptModel(string Id, string DisplayName, IReadOnlyList<string> ReasoningEfforts, string DefaultReasoning);

/// <summary>
/// Supplies the list of ChatGPT (Codex-backed) models available to the connected ChatGPT plan.
///
/// This is fetched <b>live</b> from the same private Codex backend the CLI uses:
/// <c>GET https://chatgpt.com/backend-api/codex/models?client_version=…</c>, authenticated with the
/// OAuth access token from "Sign in with ChatGPT" (see <see cref="ChatGptAuthService"/>). That's the
/// exact endpoint the Codex CLI hits at startup to discover the account's models, so the picker always
/// reflects OpenAI's current line-up (e.g. the GPT-5.6 family) instead of a stale hard-coded list.
///
/// The response mirrors the CLI's <c>models.json</c>: <c>{ "models": [ { slug, display_name,
/// visibility, priority, default_reasoning_level, supported_reasoning_levels:[{effort,…}] } ] }</c>.
/// We keep only <c>visibility=="list"</c> models and order them by <c>priority</c> (the CLI's picker
/// order), so the first entry is the account default.
///
/// The result is cached briefly (per <see cref="CacheTtl"/>). When no account is connected, or the
/// fetch/parse fails for any reason, we fall back to <see cref="FallbackCatalog"/> — a small bundled
/// catalogue that mirrors the CLI's offline <c>models.json</c> — so the admin UI never breaks and is
/// never blocked. The admin UI also always allows a free-text model id + effort.
/// </summary>
public class ChatGptModelsService
{
    /// <summary>
    /// Codex CLI version we advertise to the backend (in the <c>client_version</c> query param and the
    /// <c>User-Agent</c>). The backend gates which models it returns — and can degrade requests from
    /// clients it considers outdated — so this must look like a current CLI. Bump it as the real CLI
    /// (npm <c>@openai/codex</c>) moves forward. Shared with <see cref="ChatGptTranslator"/>.
    /// </summary>
    public const string CodexCliVersion = "0.144.4";

    private const string CacheKey = "chatgpt_codex_models";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Offline fallback catalogue — mirrors the Codex CLI's bundled <c>models.json</c> (the current
    /// GPT-5.6 line-up). Used only when no ChatGPT account is connected or the live fetch fails.
    /// Order = suggested preference (best general translation first). Keep it roughly current so the
    /// fallback isn't ancient, but the live fetch is the real source of truth.
    /// </summary>
    private static readonly IReadOnlyList<ChatGptModel> FallbackCatalog = new List<ChatGptModel>
    {
        new("gpt-5.6-sol",   "GPT-5.6-Sol",   new[] { "low", "medium", "high", "xhigh", "max", "ultra" }, "medium"),
        new("gpt-5.6-terra", "GPT-5.6-Terra", new[] { "low", "medium", "high", "xhigh", "max", "ultra" }, "medium"),
        new("gpt-5.6-luna",  "GPT-5.6-Luna",  new[] { "low", "medium", "high", "xhigh", "max" }, "medium"),
        new("gpt-5.5",       "GPT-5.5",       new[] { "low", "medium", "high", "xhigh" }, "medium"),
        new("gpt-5.4",       "GPT-5.4",       new[] { "low", "medium", "high", "xhigh" }, "medium"),
        new("gpt-5.4-mini",  "GPT-5.4 Mini",  new[] { "low", "medium", "high", "xhigh" }, "medium"),
    };

    /// <summary>All reasoning efforts we know about, used to validate free-text entries loosely.</summary>
    public static readonly IReadOnlyList<string> AllEfforts = new[] { "minimal", "low", "medium", "high", "xhigh", "max", "ultra" };

    private readonly HttpClient _http;
    private readonly ChatGptAuthService _auth;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ChatGptModelsService> _logger;

    public ChatGptModelsService(HttpClient http, ChatGptAuthService auth, IMemoryCache cache, ILogger<ChatGptModelsService> logger)
    {
        _http = http;
        _auth = auth;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// The live model list for the connected ChatGPT plan (cached briefly). Falls back to the bundled
    /// catalogue when no account is connected or the fetch fails, so this never throws and never returns
    /// empty.
    /// </summary>
    public async Task<IReadOnlyList<ChatGptModel>> GetModelsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<ChatGptModel>? cached) && cached is { Count: > 0 })
            return cached;

        var live = await FetchLiveAsync(ct);
        if (live.Count > 0)
        {
            _cache.Set(CacheKey, live, CacheTtl);
            return live;
        }
        return FallbackCatalog;
    }

    /// <summary>The bundled catalogue only (no network) — a safe default when a token isn't available.</summary>
    public IReadOnlyList<ChatGptModel> GetFallbackModels() => FallbackCatalog;

    /// <summary>Look up a bundled-catalogue model by id (case-insensitive), or null for a custom/unknown id.</summary>
    public ChatGptModel? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : FallbackCatalog.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<ChatGptModel>> FetchLiveAsync(CancellationToken ct)
    {
        var token = await _auth.GetValidAccessTokenAsync(ct);
        if (token is null) return Array.Empty<ChatGptModel>(); // no account connected → caller uses fallback

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"models?client_version={CodexCliVersion}");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value.AccessToken);
            if (!string.IsNullOrWhiteSpace(token.Value.AccountId))
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-ID", token.Value.AccountId);
            req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            req.Headers.TryAddWithoutValidation("User-Agent", $"codex_cli_rs/{CodexCliVersion} (v2en-translator)");
            req.Headers.Accept.ParseAdd("application/json");

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Codex models fetch failed: HTTP {Status}. Using fallback catalogue.", (int)resp.StatusCode);
                return Array.Empty<ChatGptModel>();
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var models = ParseModels(doc.RootElement);
            if (models.Count == 0)
                _logger.LogWarning("Codex models response had no usable models. Using fallback catalogue.");
            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Codex models fetch failed. Using fallback catalogue.");
            return Array.Empty<ChatGptModel>();
        }
    }

    /// <summary>
    /// Parse the Codex <c>{ "models": [...] }</c> body into the picker's models: keep only
    /// <c>visibility=="list"</c>, order by <c>priority</c> ascending (the CLI's picker order).
    /// Tolerant of missing/renamed fields so a backend tweak degrades gracefully rather than throwing.
    /// </summary>
    private static IReadOnlyList<ChatGptModel> ParseModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            return Array.Empty<ChatGptModel>();

        var ranked = new List<(int Priority, ChatGptModel Model)>();
        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;

            var slug = GetString(m, "slug");
            if (string.IsNullOrWhiteSpace(slug)) continue;

            // Mirror the CLI's picker: only user-selectable ("list") models, never hidden/internal ones.
            var visibility = GetString(m, "visibility");
            if (!string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase)) continue;

            var display = GetString(m, "display_name") ?? slug!;
            var priority = m.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32()
                : int.MaxValue;

            var efforts = ParseEfforts(m);
            var defaultEffort = GetString(m, "default_reasoning_level");
            if (string.IsNullOrWhiteSpace(defaultEffort)
                || (efforts.Count > 0 && !efforts.Contains(defaultEffort!, StringComparer.OrdinalIgnoreCase)))
            {
                defaultEffort = efforts.Contains("medium", StringComparer.OrdinalIgnoreCase)
                    ? "medium"
                    : efforts.FirstOrDefault() ?? "medium";
            }

            ranked.Add((priority, new ChatGptModel(slug!, display, efforts, defaultEffort!)));
        }

        return ranked.OrderBy(x => x.Priority).Select(x => x.Model).ToList();
    }

    /// <summary>
    /// Read <c>supported_reasoning_levels</c>. The real backend uses an array of objects
    /// (<c>{ "effort": "low", … }</c>); we also accept a plain array of strings for resilience.
    /// </summary>
    private static IReadOnlyList<string> ParseEfforts(JsonElement model)
    {
        var defaults = new[] { "low", "medium", "high", "xhigh" };
        if (!model.TryGetProperty("supported_reasoning_levels", out var levels) || levels.ValueKind != JsonValueKind.Array)
            return defaults;

        var efforts = new List<string>();
        foreach (var lvl in levels.EnumerateArray())
        {
            var e = lvl.ValueKind switch
            {
                JsonValueKind.String => lvl.GetString(),
                JsonValueKind.Object => GetString(lvl, "effort") ?? GetString(lvl, "level") ?? GetString(lvl, "slug"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(e) && !efforts.Contains(e!, StringComparer.OrdinalIgnoreCase))
                efforts.Add(e!.ToLowerInvariant());
        }
        return efforts.Count > 0 ? efforts : defaults;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
