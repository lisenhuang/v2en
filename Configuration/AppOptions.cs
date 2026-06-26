namespace v2en.Configuration;

public class FeedOptions
{
    public const string Section = "Feed";

    public string SourceUrl { get; set; } = "https://www.v2ex.com/index.xml";

    /// <summary>Poll interval. 300s matches the source's Cache-Control: max-age=300.</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Only posts published within this many hours appear in /index.xml (no count cap — every
    /// post inside the window is included). Defaults to 24h. Set to 0 (or negative) to disable
    /// the window and fall back to emitting all translated posts.
    /// </summary>
    public int RecentWindowHours { get; set; } = 24;

    public string UserAgent { get; set; } = "v2en/1.0 (+https://github.com/v2en; translated RSS mirror)";
}

public class OpenRouterOptions
{
    public const string Section = "OpenRouter";

    public string ApiKey { get; set; } = "";

    /// <summary>Ordered fallback chain. MUST be ":free" models only — never paid.</summary>
    public List<string> Models { get; set; } = new();

    /// <summary>Optional ranking headers used by OpenRouter (HTTP-Referer / X-Title).</summary>
    public string? Referer { get; set; }
    public string? Title { get; set; }

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1/";
}

public class TranslationOptions
{
    public const string Section = "Translation";

    /// <summary>Max posts translated per worker tick (keeps calls infrequent).</summary>
    public int MaxPerTick { get; set; } = 8;

    /// <summary>Minimum delay between OpenRouter calls (seconds).</summary>
    public int MinDelaySecondsBetweenCalls { get; set; } = 4;

    /// <summary>Client-side per-minute cap (stay under the free 20 req/min).</summary>
    public int RequestsPerMinute { get; set; } = 15;

    /// <summary>Daily translation cap (free tier ~50/day; leave margin).</summary>
    public int DailyQuota { get; set; } = 48;

    /// <summary>Give up on a post after this many failed attempts.</summary>
    public int MaxAttempts { get; set; } = 5;

    public double Temperature { get; set; } = 0.2;

    /// <summary>Cap on completion tokens per call.</summary>
    public int MaxOutputTokens { get; set; } = 4096;
}

public class GeminiOptions
{
    public const string Section = "Gemini";

    /// <summary>Seed for the server-side embedding key pool (the dashboard/DB is the source of truth after first run).</summary>
    public List<string> EmbedKeys { get; set; } = new();

    /// <summary>Seed default embedding model id (the admin picks from the live list afterwards).</summary>
    public string EmbeddingModel { get; set; } = "";

    public int EmbeddingDim { get; set; } = 768;

    /// <summary>Seed default generateContent model used for chat answers.</summary>
    public string ChatModel { get; set; } = "gemini-2.5-flash";
}

public class SiteOptions
{
    public const string Section = "Site";

    /// <summary>Public base URL (no trailing slash), e.g. https://your-domain. Used for feed self/alternate links.</summary>
    public string BaseUrl { get; set; } = "";

    public string FeedTitle { get; set; } = "V2EX (English)";
    public string FeedSubtitle { get; set; } = "way to explore — machine-translated to English";
    public string FeedRights { get; set; } = "Content © V2EX and respective authors. English translation by v2en.";
}
