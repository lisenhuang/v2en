namespace v2en.Data;

/// <summary>
/// Singleton row (Id = 1) holding cross-poll state: the conditional-GET validator
/// and the daily translation-quota counter.
/// </summary>
public class FeedState
{
    public int Id { get; set; }

    /// <summary>Weak ETag from the last 200 response, stored verbatim incl. the W/"…" prefix.</summary>
    public string? LastETag { get; set; }

    /// <summary>The feed-level &lt;updated&gt; value from the last successful parse.</summary>
    public DateTimeOffset? LastSourceFeedUpdated { get; set; }

    public DateTimeOffset? LastFetchUtc { get; set; }
    public int? LastStatusCode { get; set; }

    /// <summary>Translations performed in the current UTC-day quota window.</summary>
    public int TranslationsToday { get; set; }

    /// <summary>When the daily quota window resets (UTC midnight rollover).</summary>
    public DateTimeOffset? QuotaWindowResetUtc { get; set; }
}
