using System.Threading.Channels;
using v2en.Data;

namespace v2en.Services;

/// <summary>
/// The analytics knobs the request path needs, captured as an immutable snapshot so the middleware
/// never touches the database while serving a page. Refreshed periodically by <see cref="AnalyticsWriter"/>.
/// </summary>
public sealed record AnalyticsSnapshot(
    bool Enabled,
    string Salt,
    bool RespectDoNotTrack,
    bool IncludeAdmin,
    int RetentionDays)
{
    /// <summary>Used until the first refresh completes: collect nothing (there is no salt yet either).</summary>
    public static readonly AnalyticsSnapshot Disabled = new(false, "", true, false, 90);
}

/// <summary>
/// The hand-off point between "a request just finished" and "a row eventually lands in SQLite".
///
/// Page views are pushed into a bounded in-memory channel and written later, in batches, by
/// <see cref="AnalyticsWriter"/>. That ordering is the whole point: a visitor's request never waits
/// on a database write, and if analytics ever falls behind (or the DB is locked by the feed worker),
/// the channel drops the overflow instead of slowing down or failing the page. Analytics is
/// best-effort by design — losing a page view is always preferable to breaking a page.
/// </summary>
public sealed class AnalyticsRecorder
{
    /// <summary>Roughly a minute of heavy traffic; beyond this we shed load rather than grow memory.</summary>
    private const int QueueCapacity = 4096;

    private readonly Channel<AnalyticsEvent> _queue = Channel.CreateBounded<AnalyticsEvent>(
        new BoundedChannelOptions(QueueCapacity)
        {
            // Never block the request thread; the newest event is dropped when the queue is full.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

    private volatile AnalyticsSnapshot _snapshot = AnalyticsSnapshot.Disabled;
    private long _dropped;
    private long _recorded;

    /// <summary>Current settings snapshot — safe to read on every request.</summary>
    public AnalyticsSnapshot Snapshot => _snapshot;

    /// <summary>Replaces the snapshot the request path reads. Called by the writer after loading settings.</summary>
    public void UpdateSnapshot(AnalyticsSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>Total events dropped because the queue was full — surfaced in the dashboard footer.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Total events accepted into the queue since startup.</summary>
    public long Recorded => Interlocked.Read(ref _recorded);

    public ChannelReader<AnalyticsEvent> Reader => _queue.Reader;

    /// <summary>Queues a page view. Returns false when it was shed; never throws, never blocks.</summary>
    public bool Enqueue(AnalyticsEvent evt)
    {
        if (_queue.Writer.TryWrite(evt))
        {
            Interlocked.Increment(ref _recorded);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}
