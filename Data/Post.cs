namespace v2en.Data;

public enum TranslationStatus
{
    Pending = 0,
    Translated = 1,
    Failed = 2,
    QuotaDeferred = 3,
}

/// <summary>
/// One V2EX front-page post, mirrored and translated to English.
/// Dedup key is <see cref="V2exId"/> (the numeric post id, e.g. 1222343).
/// </summary>
public class Post
{
    public int Id { get; set; }

    /// <summary>Numeric V2EX post id (e.g. 1222343). Unique — the dedup key.</summary>
    public long V2exId { get; set; }

    /// <summary>Original Atom &lt;id&gt;, e.g. "tag:www.v2ex.com,2026-06-23:/t/1222343". Preserved verbatim for our feed output.</summary>
    public string SourceTagId { get; set; } = "";

    /// <summary>Original alternate link, e.g. https://www.v2ex.com/t/1222343#reply1</summary>
    public string SourceUrl { get; set; } = "";

    public string AuthorName { get; set; } = "";
    public string AuthorUri { get; set; } = "";

    public string TitleZh { get; set; } = "";
    public string ContentZhHtml { get; set; } = "";

    public string? TitleEn { get; set; }
    public string? ContentEnHtml { get; set; }

    /// <summary>Original publish time (UTC). The sort key for the homepage and feed.</summary>
    public DateTimeOffset Published { get; set; }

    /// <summary>Original last-updated time (UTC), as reported by V2EX.</summary>
    public DateTimeOffset Updated { get; set; }

    /// <summary>SHA-256 of (title \0 content). Re-translate only when this changes.</summary>
    public string SourceContentHash { get; set; } = "";

    public TranslationStatus Status { get; set; } = TranslationStatus.Pending;

    public string? TranslationModel { get; set; }
    public DateTimeOffset? TranslatedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }

    /// <summary>When we first stored this post (NOT used for ordering).</summary>
    public DateTimeOffset FirstSeenUtc { get; set; }

    /// <summary>Optional 1:1 vector embedding (side table) for semantic search / chat.</summary>
    public PostEmbedding? Embedding { get; set; }
}
