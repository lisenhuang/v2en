using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Services;

/// <summary>
/// Scoped orchestrator: fetch the feed (conditional GET), upsert posts by V2exId,
/// then translate a paced batch of pending posts. Created fresh per worker tick.
/// Pacing/quota/model config is read from <see cref="RuntimeSettings"/> (admin-editable).
/// </summary>
public class TranslationService
{
    /// <summary>Drop dashboard log rows older than this to keep the table bounded.</summary>
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(14);

    private readonly AppDbContext _db;
    private readonly V2exFeedClient _feed;
    private readonly OpenRouterTranslator _translator;
    private readonly HtmlSanitizerService _sanitizer;
    private readonly RuntimeSettingsService _settings;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(
        AppDbContext db,
        V2exFeedClient feed,
        OpenRouterTranslator translator,
        HtmlSanitizerService sanitizer,
        RuntimeSettingsService settings,
        ILogger<TranslationService> logger)
    {
        _db = db;
        _feed = feed;
        _translator = translator;
        _sanitizer = sanitizer;
        _settings = settings;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken ct)
    {
        var state = await _db.FeedStates.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is null)
        {
            state = new FeedState { Id = 1 };
            _db.FeedStates.Add(state);
        }

        var result = await _feed.FetchAsync(state.LastETag, ct);
        state.LastFetchUtc = DateTimeOffset.UtcNow;
        state.LastStatusCode = result.StatusCode;

        if (result.Outcome == FeedFetchOutcome.Fetched && result.Feed is not null)
        {
            await UpsertAsync(result.Feed, ct);
            if (!string.IsNullOrWhiteSpace(result.ETag)) state.LastETag = result.ETag;
            state.LastSourceFeedUpdated = result.Feed.Updated;
        }

        await _db.SaveChangesAsync(ct);
        await TranslatePendingAsync(state, ct);
    }

    private async Task UpsertAsync(ParsedFeed feed, CancellationToken ct)
    {
        var ids = feed.Entries.Select(e => e.V2exId).ToList();
        var existing = await _db.Posts
            .Where(p => ids.Contains(p.V2exId))
            .ToDictionaryAsync(p => p.V2exId, ct);

        var now = DateTimeOffset.UtcNow;
        int added = 0, requeued = 0;

        foreach (var e in feed.Entries)
        {
            var hash = Hash(e.Title, e.ContentHtml);

            if (existing.TryGetValue(e.V2exId, out var post))
            {
                post.SourceTagId = e.SourceTagId;
                post.SourceUrl = e.SourceUrl;
                post.AuthorName = e.AuthorName;
                post.AuthorUri = e.AuthorUri;
                post.Updated = e.Updated;

                // Re-translate only when the source content actually changed.
                if (post.SourceContentHash != hash)
                {
                    post.TitleZh = e.Title;
                    post.ContentZhHtml = e.ContentHtml;
                    post.SourceContentHash = hash;
                    post.Status = TranslationStatus.Pending;
                    post.Attempts = 0;
                    post.LastError = null;
                    requeued++;
                }
            }
            else
            {
                _db.Posts.Add(new Post
                {
                    V2exId = e.V2exId,
                    SourceTagId = e.SourceTagId,
                    SourceUrl = e.SourceUrl,
                    AuthorName = e.AuthorName,
                    AuthorUri = e.AuthorUri,
                    TitleZh = e.Title,
                    ContentZhHtml = e.ContentHtml,
                    Published = e.Published,
                    Updated = e.Updated,
                    SourceContentHash = hash,
                    Status = TranslationStatus.Pending,
                    FirstSeenUtc = now,
                });
                added++;
            }
        }

        if (added > 0 || requeued > 0)
            _logger.LogInformation("Upsert: {Added} new, {Requeued} re-queued for translation.", added, requeued);
    }

    private async Task TranslatePendingAsync(FeedState state, CancellationToken ct)
    {
        var cfg = await _settings.GetAsync(ct);
        var models = RuntimeSettingsService.ParseModels(cfg);
        var now = DateTimeOffset.UtcNow;

        // Bound the dashboard log table.
        await _db.TranslationLogs.Where(l => l.Utc < now - LogRetention).ExecuteDeleteAsync(ct);

        // Roll the daily quota window at UTC midnight.
        if (state.QuotaWindowResetUtc is null || now >= state.QuotaWindowResetUtc)
        {
            state.TranslationsToday = 0;
            state.QuotaWindowResetUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        }

        if (models.Count == 0)
        {
            _logger.LogWarning("No translation models configured; skipping translation.");
            Log(LogSeverity.Error, "no_models", "No AI models configured — set at least one :free model in the dashboard.");
            await _db.SaveChangesAsync(ct);
            return;
        }

        var remainingDaily = cfg.DailyQuota - state.TranslationsToday;
        if (!cfg.UnlimitedDaily && remainingDaily <= 0)
        {
            _logger.LogWarning("Daily translation quota reached ({Quota}); resumes after {Reset:u}.",
                cfg.DailyQuota, state.QuotaWindowResetUtc);
            Log(LogSeverity.Warning, "quota_reached",
                $"Daily quota of {cfg.DailyQuota} reached; resumes after {state.QuotaWindowResetUtc:u}. Raise DailyQuota or enable Unlimited to translate more.");
            await _db.SaveChangesAsync(ct);
            return;
        }

        // Unlimited mode: only OpenRouter's own free-tier limit (an account-level 429) stops us.
        var take = cfg.UnlimitedDaily ? cfg.MaxPerTick : Math.Min(cfg.MaxPerTick, remainingDaily);
        var pending = await _db.Posts
            .Where(p => p.Status == TranslationStatus.Pending && p.Attempts < cfg.MaxAttempts)
            .OrderByDescending(p => p.Published) // freshest first
            .Take(take)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            await _db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation("Translating {Count} post(s) this tick (daily {Used}/{Quota}).",
            pending.Count, state.TranslationsToday, cfg.DailyQuota);

        for (int i = 0; i < pending.Count; i++)
        {
            // Pace calls so we never hammer the free API.
            if (i > 0)
                await Task.Delay(TimeSpan.FromSeconds(cfg.MinDelaySecondsBetweenCalls), ct);

            var post = pending[i];
            var outcome = await _translator.TranslateAsync(
                post.TitleZh, post.ContentZhHtml, models, cfg.MaxOutputTokens, cfg.Temperature, ct);

            if (outcome.RateLimited)
            {
                _logger.LogWarning("Rate-limited by OpenRouter; stopping translation for this tick.");
                Log(LogSeverity.Warning, "rate_limited",
                    "OpenRouter rate-limited the account; paused translation for this tick.",
                    v2exId: post.V2exId, model: outcome.Model, httpStatus: 429,
                    detail: FormatAttempts(outcome.Attempts));
                await _db.SaveChangesAsync(ct);
                break; // leave remaining posts Pending, no attempt charged
            }

            post.Attempts++;
            state.TranslationsToday++; // a real API call was made (success or hard failure)

            if (outcome.Success)
            {
                post.TitleEn = string.IsNullOrWhiteSpace(outcome.Title) ? post.TitleZh : outcome.Title;
                post.ContentEnHtml = _sanitizer.Sanitize(outcome.ContentHtml);
                post.Status = TranslationStatus.Translated;
                post.TranslationModel = outcome.Model;
                post.TranslatedAt = DateTimeOffset.UtcNow;
                post.LastError = null;
                Log(LogSeverity.Info, "translated", $"Translated \"{Trim(post.TitleEn, 80)}\".",
                    v2exId: post.V2exId, model: outcome.Model);
            }
            else
            {
                post.LastError = outcome.Error;
                var exhausted = post.Attempts >= cfg.MaxAttempts;
                if (exhausted)
                    post.Status = TranslationStatus.Failed;

                _logger.LogWarning("Translation failed for post {Id} (attempt {Attempt}): {Error}",
                    post.V2exId, post.Attempts, outcome.Error);
                Log(exhausted ? LogSeverity.Error : LogSeverity.Warning,
                    exhausted ? "post_failed" : "model_failed",
                    exhausted
                        ? $"Post {post.V2exId} failed after {post.Attempts} attempt(s): {outcome.Error}"
                        : $"Attempt {post.Attempts} on post {post.V2exId} failed: {outcome.Error}",
                    v2exId: post.V2exId,
                    model: outcome.Attempts.LastOrDefault()?.Model,
                    httpStatus: outcome.Attempts.LastOrDefault()?.HttpStatus,
                    detail: FormatAttempts(outcome.Attempts));
            }

            await _db.SaveChangesAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
    }

    private void Log(LogSeverity level, string evt, string message,
        long? v2exId = null, string? model = null, int? httpStatus = null, string? detail = null)
    {
        _db.TranslationLogs.Add(new TranslationLog
        {
            Utc = DateTimeOffset.UtcNow,
            Level = level,
            Event = evt,
            Message = message,
            V2exId = v2exId,
            Model = model,
            HttpStatus = httpStatus,
            Detail = detail,
        });
    }

    /// <summary>Flatten the per-model attempt list into a readable block for the log detail.</summary>
    private static string FormatAttempts(IReadOnlyList<TranslationAttempt> attempts)
    {
        if (attempts.Count == 0) return "(no attempts)";
        return string.Join("\n\n", attempts.Select(a =>
        {
            var head = $"[{a.Model}] {a.Outcome}" + (a.HttpStatus is int s ? $" (HTTP {s})" : "");
            return string.IsNullOrEmpty(a.Detail) ? head : head + "\n" + a.Detail;
        }));
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Hash(string title, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(title + "\0" + content);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
