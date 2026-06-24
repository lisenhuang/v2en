namespace v2en.Data;

/// <summary>
/// The vector embedding of a post, stored 1:1 with <see cref="Post"/> in a side table so the
/// multi-KB vector never weighs down the hot read paths (homepage, /t/{id}, /index.xml).
/// The embedding is of the ORIGINAL Chinese text, so every post is searchable regardless of
/// whether it has been translated yet.
/// </summary>
public class PostEmbedding
{
    public int Id { get; set; }

    public int PostId { get; set; }
    public Post Post { get; set; } = default!;

    /// <summary>Little-endian float32[] (length = <see cref="Dim"/>), L2-normalized so cosine == dot.</summary>
    public byte[] Vector { get; set; } = [];

    public int Dim { get; set; }

    /// <summary>The embedding model used, e.g. "gemini-embedding-001". A change invalidates the vector.</summary>
    public string Model { get; set; } = "";

    /// <summary>Hash of the post text that was embedded; mismatch vs Post.SourceContentHash ⇒ re-embed.</summary>
    public string SourceContentHash { get; set; } = "";

    public DateTimeOffset EmbeddedAt { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
