namespace v2en.Services;

/// <summary>
/// Singleton round-robin cursor over the server-side Gemini embedding key pool, so successive
/// embed calls don't always start at key[0] and burn its quota first. Thread-safe.
/// </summary>
public sealed class GeminiKeyCursor
{
    private int _index = -1;

    /// <summary>Next starting offset into a pool of <paramref name="count"/> keys (0-based).</summary>
    public int Next(int count)
    {
        if (count <= 0) return 0;
        var i = Interlocked.Increment(ref _index);
        return (int)((uint)i % (uint)count);
    }
}
