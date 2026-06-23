using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Data;

namespace v2en.Services;

/// <summary>
/// Scoped orchestrator: fetch the feed (conditional GET), upsert posts by V2exId,
/// then translate a paced batch of pending posts. Created fresh per worker tick.
/// </summary>
public class TranslationService
{
    private readonly AppDbContext _db;
    private readonly V2exFeedClient _feed;
    private readonly OpenRouterTranslator _translator;
    private readonly HtmlSanitizerService _sanitizer;
    private readonly TranslationOptions _t;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(
        AppDbContext db,
        V2exFeedClient feed,
        OpenRouterTranslator translator,
        HtmlSanitizerService sanitizer,
        IOptions<TranslationOptions> t,
        ILogger<TranslationService> logger)
    {
        _db = db;
        _feed = feed;
        _translator = translator;
        _sanitizer = sanitizer;
        _t = t.Value;
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
        var now = DateTimeOffset.UtcNow;

        // Roll the daily quota window at UTC midnight.
        if (state.QuotaWindowResetUtc is null || now >= state.QuotaWindowResetUtc)
        {
            state.TranslationsToday = 0;
            state.QuotaWindowResetUtc = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        }

        var remainingDaily = _t.DailyQuota - state.TranslationsToday;
        if (remainingDaily <= 0)
        {
            _logger.LogWarning("Daily translation quota reached ({Quota}); resumes after {Reset:u}.",
                _t.DailyQuota, state.QuotaWindowResetUtc);
            await _db.SaveChangesAsync(ct);
            return;
        }

        var take = Math.Min(_t.MaxPerTick, remainingDaily);
        var pending = await _db.Posts
            .Where(p => p.Status == TranslationStatus.Pending && p.Attempts < _t.MaxAttempts)
            .OrderByDescending(p => p.Published) // freshest first
            .Take(take)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            await _db.SaveChangesAsync(ct);
            return;
        }

        _logger.LogInformation("Translating {Count} post(s) this tick (daily {Used}/{Quota}).",
            pending.Count, state.TranslationsToday, _t.DailyQuota);

        for (int i = 0; i < pending.Count; i++)
        {
            // Pace calls so we never hammer the free API.
            if (i > 0)
                await Task.Delay(TimeSpan.FromSeconds(_t.MinDelaySecondsBetweenCalls), ct);

            var post = pending[i];
            var outcome = await _translator.TranslateAsync(post.TitleZh, post.ContentZhHtml, ct);

            if (outcome.RateLimited)
            {
                _logger.LogWarning("Rate-limited by OpenRouter; stopping translation for this tick.");
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
            }
            else
            {
                post.LastError = outcome.Error;
                if (post.Attempts >= _t.MaxAttempts)
                    post.Status = TranslationStatus.Failed;
                _logger.LogWarning("Translation failed for post {Id} (attempt {Attempt}): {Error}",
                    post.V2exId, post.Attempts, outcome.Error);
            }

            await _db.SaveChangesAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
    }

    private static string Hash(string title, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(title + "\0" + content);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
