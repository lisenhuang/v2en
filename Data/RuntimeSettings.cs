namespace v2en.Data;

/// <summary>
/// Single-row (Id = 1) runtime configuration the admin can edit from the dashboard without a
/// redeploy. Seeded from appsettings/Options on first startup, then the DB is the source of truth.
/// The worker reloads this each tick, so changes take effect within one poll interval.
/// </summary>
public class RuntimeSettings
{
    public int Id { get; set; }

    /// <summary>Ordered fallback chain of OpenRouter model ids — MUST be ":free" only. Stored as JSON.</summary>
    public string ModelsJson { get; set; } = "[]";

    /// <summary>Daily cap on OpenRouter calls (success + hard failure both count). Ignored when <see cref="UnlimitedDaily"/>.</summary>
    public int DailyQuota { get; set; } = 200;

    /// <summary>
    /// When true, ignore <see cref="DailyQuota"/> and keep translating every tick until OpenRouter's
    /// own free-tier limit is hit — an account-level 429 pauses the tick automatically, then it
    /// resumes on the next tick (and ultimately at OpenRouter's daily reset).
    /// </summary>
    public bool UnlimitedDaily { get; set; }

    /// <summary>Max posts translated per worker tick.</summary>
    public int MaxPerTick { get; set; } = 8;

    /// <summary>Minimum delay between OpenRouter calls (seconds).</summary>
    public int MinDelaySecondsBetweenCalls { get; set; } = 4;

    /// <summary>Give up on a post after this many failed attempts.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Cap on completion tokens per call.</summary>
    public int MaxOutputTokens { get; set; } = 4096;

    public double Temperature { get; set; } = 0.2;

    // ── API keys (admin-managed, stored here in the DB) ───────────────────────────
    /// <summary>OpenRouter API key for translation. Empty ⇒ translation paused.</summary>
    public string OpenRouterApiKey { get; set; } = "";

    /// <summary>JSON string[] of Google AI Studio keys for server-side embeddings (rotated/failed-over).</summary>
    public string GeminiEmbedKeysJson { get; set; } = "[]";

    // ── Embedding (semantic index) ────────────────────────────────────────────────
    /// <summary>Gemini embedding model id, chosen by the admin from the live model list. Empty ⇒ embedding paused.</summary>
    public string EmbeddingModel { get; set; } = "";

    /// <summary>Embedding output dimensionality (must be supported by the chosen model).</summary>
    public int EmbeddingDim { get; set; } = 768;

    public int EmbedMaxPerTick { get; set; } = 16;
    public int EmbedMaxAttempts { get; set; } = 5;

    // ── Chat / retrieval (public "ask the feed") ──────────────────────────────────
    /// <summary>Master switch for the public chat feature.</summary>
    public bool EnableChat { get; set; }

    /// <summary>Gemini generateContent model used for chat answers (run on the visitor's own key).</summary>
    public string ChatModel { get; set; } = "gemini-2.5-flash";

    public int RetrievalTopK { get; set; } = 8;
    public int ChatMaxContextPosts { get; set; } = 8;
    public int ChatRateLimitPerMinutePerIp { get; set; } = 6;

    public DateTimeOffset? UpdatedUtc { get; set; }
}
