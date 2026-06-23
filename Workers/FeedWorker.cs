using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Services;

namespace v2en.Workers;

/// <summary>
/// Polls the V2EX feed on a fixed interval (default 5 min, matching the source's
/// Cache-Control: max-age=300). Runs once immediately on startup, then every interval.
/// Each tick gets a fresh DI scope (the worker is a singleton; DbContext is scoped).
/// </summary>
public class FeedWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FeedOptions _options;
    private readonly ILogger<FeedWorker> _logger;

    public FeedWorker(IServiceScopeFactory scopeFactory, IOptions<FeedOptions> options, ILogger<FeedWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.PollIntervalSeconds));
        _logger.LogInformation("FeedWorker started; poll interval {Interval}.", interval);

        // Run once immediately (PeriodicTimer's first tick is delayed by one interval).
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TranslationService>();
            await service.SyncAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            // Never let one bad tick kill the loop.
            _logger.LogError(ex, "FeedWorker tick failed.");
        }
    }
}
