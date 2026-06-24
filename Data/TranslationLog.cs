namespace v2en.Data;

public enum LogSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// An append-only record of translation events for the admin dashboard — especially failures,
/// where <see cref="Detail"/> holds the raw error body returned by the AI API so the admin can
/// see exactly why a call failed (e.g. the OpenRouter 404/429 JSON).
/// </summary>
public class TranslationLog
{
    public int Id { get; set; }

    public DateTimeOffset Utc { get; set; }

    public LogSeverity Level { get; set; }

    /// <summary>Short event key: "translated", "model_failed", "post_failed", "rate_limited", "quota_reached".</summary>
    public string Event { get; set; } = "";

    /// <summary>The post this is about, if any (numeric V2EX id).</summary>
    public long? V2exId { get; set; }

    /// <summary>The model involved, if any.</summary>
    public string? Model { get; set; }

    /// <summary>HTTP status from the AI API, if applicable.</summary>
    public int? HttpStatus { get; set; }

    /// <summary>Human-readable summary.</summary>
    public string Message { get; set; } = "";

    /// <summary>Full detail — e.g. the raw AI API error body. May be long.</summary>
    public string? Detail { get; set; }
}
