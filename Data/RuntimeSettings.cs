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

    // ── Translation provider routing (primary → fallback) ─────────────────────────
    // Each slot picks a provider ("" | "openrouter" | "chatgpt") and a model. When BOTH slots are
    // empty (a freshly-upgraded site), translation keeps using the legacy OpenRouter free-model
    // chain in <see cref="ModelsJson"/> so nothing breaks. Set a provider to switch to explicit
    // primary/fallback routing. ChatGPT slots also carry a reasoning effort ("" = model default).
    public string TranslationPrimaryProvider { get; set; } = "";
    public string TranslationPrimaryModel { get; set; } = "";
    public string TranslationPrimaryReasoning { get; set; } = "";
    public string TranslationFallbackProvider { get; set; } = "";
    public string TranslationFallbackModel { get; set; } = "";
    public string TranslationFallbackReasoning { get; set; } = "";

    // ── ChatGPT (Codex) account, connected from /admin via device-code OAuth ──────
    // Tokens are obtained by the "Sign in with ChatGPT" flow (or by pasting a Codex auth.json) and
    // refreshed automatically. Empty ⇒ no ChatGPT account connected (ChatGPT translation paused).
    public string ChatGptAccessToken { get; set; } = "";
    public string ChatGptRefreshToken { get; set; } = "";
    public string ChatGptIdToken { get; set; } = "";
    /// <summary>ChatGPT account id (from the access-token JWT) sent as the chatgpt-account-id header.</summary>
    public string ChatGptAccountId { get; set; } = "";
    /// <summary>When the current access token expires. Refreshed ~a few minutes early.</summary>
    public DateTimeOffset? ChatGptAccessTokenExpiresUtc { get; set; }
    /// <summary>Plan type (e.g. "plus"/"pro") from the token, shown in the dashboard. Informational.</summary>
    public string ChatGptPlanType { get; set; } = "";
    /// <summary>Account email/label if present in the id token — display only.</summary>
    public string ChatGptAccountLabel { get; set; } = "";

    // ── Web analytics (privacy-preserving page-view counting) ─────────────────────
    /// <summary>
    /// Master switch for recording page views. Defaults to ON — collection is passive, never blocks
    /// a request, and stores no raw IP addresses. Turn it off to stop recording entirely (existing
    /// rows are kept and stay visible in the dashboard until the retention window prunes them).
    /// </summary>
    public bool EnableAnalytics { get; set; } = true;

    /// <summary>
    /// How many days of page views to keep. Older rows are pruned hourly by the analytics writer.
    /// 0 (or negative) disables pruning and keeps everything.
    /// </summary>
    public int AnalyticsRetentionDays { get; set; } = 90;

    /// <summary>
    /// Secret key for the one-way visitor hash. Generated on first startup and never shown in the UI
    /// — it is what makes <see cref="AnalyticsEvent.VisitorHash"/> irreversible. Rotating it simply
    /// makes previously-recorded visitors count as new ones.
    /// </summary>
    public string AnalyticsSalt { get; set; } = "";

    /// <summary>When true (default), requests sending <c>DNT: 1</c> or <c>Sec-GPC: 1</c> are not recorded.</summary>
    public bool AnalyticsRespectDoNotTrack { get; set; } = true;

    /// <summary>When true, /admin page views are recorded too. Off by default so your own visits don't skew the numbers.</summary>
    public bool AnalyticsIncludeAdmin { get; set; }

    public DateTimeOffset? UpdatedUtc { get; set; }
}
