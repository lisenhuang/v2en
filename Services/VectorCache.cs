using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Utilities;

namespace v2en.Services;

/// <summary>One cached post vector (unit-normalized) plus the metadata retrieval needs to filter/sort.</summary>
public sealed record CachedVec(int PostId, long V2exId, long PublishedUtcTicks, string Model, int Dim, float[] Vector);

/// <summary>
/// In-memory snapshot of all post embeddings, so a chat query doesn't load every BLOB from SQLite.
/// The embedding pipeline calls <see cref="Invalidate"/> after writing new vectors; the next query
/// lazily reloads. Thread-safe via an immutable-list swap.
/// </summary>
public sealed class VectorCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile IReadOnlyList<CachedVec> _items = Array.Empty<CachedVec>();
    private long _version;       // bumped when the DB changes
    private long _loadedVersion = -1;

    /// <summary>Signal that the underlying PostEmbeddings table changed; the next read reloads.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    public async Task<IReadOnlyList<CachedVec>> GetAsync(AppDbContext db, CancellationToken ct)
    {
        if (Interlocked.Read(ref _version) == Interlocked.Read(ref _loadedVersion))
            return _items;

        await _gate.WaitAsync(ct);
        try
        {
            var target = Interlocked.Read(ref _version);
            if (target == _loadedVersion) return _items;

            var epoch = DateTimeOffset.UnixEpoch;
            var rows = await db.PostEmbeddings
                .AsNoTracking()
                .Where(e => e.EmbeddedAt > epoch)   // skip failed-attempt rows (no vector yet)
                .Select(e => new { e.PostId, e.Post.V2exId, e.Post.Published, e.Model, e.Dim, e.Vector })
                .ToListAsync(ct);

            _items = rows
                .Select(r => new CachedVec(r.PostId, r.V2exId, r.Published.UtcTicks, r.Model, r.Dim, VectorBytes.Unpack(r.Vector)))
                .ToList();
            Interlocked.Exchange(ref _loadedVersion, target);
            return _items;
        }
        finally
        {
            _gate.Release();
        }
    }
}
