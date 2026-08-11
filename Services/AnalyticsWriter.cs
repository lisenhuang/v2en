using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Services;

/// <summary>
/// Drains the <see cref="AnalyticsRecorder"/> queue into SQLite in batches, keeps the request path's
/// settings snapshot fresh, and prunes rows past the retention window.
///
/// Batching matters here: this database is a single SQLite file that the feed/translation worker also
/// writes to, so one INSERT per page view would multiply lock contention for no benefit. Instead the
/// writer waits for work, grabs everything queued (up to a cap), and commits it in one transaction.
/// Every step is wrapped so a failure — a locked DB, a malformed row — is logged and skipped rather
/// than tearing down the host.
/// </summary>
public sealed class AnalyticsWriter : BackgroundService
{
    /// <summary>Max rows per transaction. Also bounds how much is lost if a batch fails.</summary>
    private const int MaxBatch = 400;

    /// <summary>How often the middleware's settings snapshot is refreshed from the DB.</summary>
    private static readonly TimeSpan SettingsRefresh = TimeSpan.FromSeconds(30);

    /// <summary>How often expired rows are pruned.</summary>
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly AnalyticsRecorder _recorder;
    private readonly ILogger<AnalyticsWriter> _log;

    private DateTimeOffset _nextSettingsRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPurge = DateTimeOffset.MinValue;
    private long _lastReportedDrops;

    public AnalyticsWriter(IServiceScopeFactory scopes, AnalyticsRecorder recorder, ILogger<AnalyticsWriter> log)
    {
        _scopes = scopes;
        _recorder = recorder;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load settings before serving traffic so the very first request is already recorded with the
        // right salt (the snapshot starts disabled precisely so nothing is recorded unsalted).
        await RefreshSettingsAsync(stoppingToken);

        var reader = _recorder.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wake on either a queued event or the housekeeping tick, whichever comes first.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idle.CancelAfter(SettingsRefresh);

                var hasWork = false;
                try
                {
                    hasWork = await reader.WaitToReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Housekeeping tick — no events arrived.
                }

                if (hasWork)
                    await DrainAsync(stoppingToken);

                await HousekeepingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Analytics must never take the app down — log and keep going.
                _log.LogWarning(ex, "Analytics writer loop failed; continuing.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }

        // Best-effort flush of whatever is still queued at shutdown.
        await DrainAsync(CancellationToken.None);
    }

    /// <summary>
    /// Writes everything currently queued, one transaction per <see cref="MaxBatch"/> rows. Anything
    /// that arrives mid-drain is picked up by the next pass, so this always terminates.
    /// </summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        var reader = _recorder.Reader;

        while (true)
        {
            var batch = new List<AnalyticsEvent>(Math.Min(MaxBatch, 64));
            while (batch.Count < MaxBatch && reader.TryRead(out var evt))
                batch.Add(evt);

            if (batch.Count == 0) return;

            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.AnalyticsEvents.AddRange(batch);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Dropped {Count} analytics event(s) — write failed.", batch.Count);
            }

            if (batch.Count < MaxBatch) return;   // the queue was emptied by this pass
        }
    }

    /// <summary>Periodic settings refresh, retention purge, and a note about any shed events.</summary>
    private async Task HousekeepingAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (now >= _nextSettingsRefresh)
            await RefreshSettingsAsync(ct);

        if (now >= _nextPurge)
        {
            _nextPurge = now + PurgeInterval;
            await PurgeAsync(ct);
        }

        var dropped = _recorder.Dropped;
        if (dropped > _lastReportedDrops)
        {
            _log.LogInformation(
                "Analytics queue shed {New} event(s) under load ({Total} total since startup).",
                dropped - _lastReportedDrops, dropped);
            _lastReportedDrops = dropped;
        }
    }

    /// <summary>
    /// Reloads the settings the middleware needs, generating the hash salt on first run. The salt is
    /// created here (not in a migration) so it is unique per deployment and never ships in source.
    /// </summary>
    private async Task RefreshSettingsAsync(CancellationToken ct)
    {
        _nextSettingsRefresh = DateTimeOffset.UtcNow + SettingsRefresh;
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<RuntimeSettingsService>();
            var cfg = await settings.GetAsync(ct);

            if (string.IsNullOrWhiteSpace(cfg.AnalyticsSalt))
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                cfg.AnalyticsSalt = VisitorHasher.NewSalt();
                cfg.UpdatedUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                _log.LogInformation("Generated a new analytics visitor-hash salt.");
            }

            _recorder.UpdateSnapshot(new AnalyticsSnapshot(
                Enabled: cfg.EnableAnalytics,
                Salt: cfg.AnalyticsSalt,
                RespectDoNotTrack: cfg.AnalyticsRespectDoNotTrack,
                IncludeAdmin: cfg.AnalyticsIncludeAdmin,
                RetentionDays: cfg.AnalyticsRetentionDays));
        }
        catch (Exception ex)
        {
            // Keep the previous snapshot; a transient DB hiccup must not silently disable collection.
            _log.LogWarning(ex, "Could not refresh analytics settings; keeping the previous snapshot.");
        }
    }

    /// <summary>Deletes page views older than the retention window. A non-positive window keeps everything.</summary>
    private async Task PurgeAsync(CancellationToken ct)
    {
        var days = _recorder.Snapshot.RetentionDays;
        if (days <= 0) return;

        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var removed = await db.AnalyticsEvents.Where(e => e.Utc < cutoff).ExecuteDeleteAsync(ct);
            if (removed > 0)
                _log.LogInformation("Pruned {Count} analytics event(s) older than {Days} day(s).", removed, days);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Analytics retention purge failed; will retry next hour.");
        }
    }
}
