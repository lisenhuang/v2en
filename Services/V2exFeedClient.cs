using System.Net;
using Microsoft.Extensions.Options;
using v2en.Configuration;

namespace v2en.Services;

public enum FeedFetchOutcome { NotModified, Fetched, Error }

public record FeedFetchResult(
    FeedFetchOutcome Outcome,
    int StatusCode,
    string? ETag,
    ParsedFeed? Feed,
    string? Error);

/// <summary>
/// Typed HttpClient that fetches the V2EX Atom feed with a conditional GET.
/// The source sends a weak ETag and Cache-Control: max-age=300 but NO Last-Modified,
/// so we validate with If-None-Match only and treat 304 as "unchanged".
/// </summary>
public class V2exFeedClient
{
    private readonly HttpClient _http;
    private readonly FeedOptions _options;
    private readonly ILogger<V2exFeedClient> _logger;

    public V2exFeedClient(HttpClient http, IOptions<FeedOptions> options, ILogger<V2exFeedClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FeedFetchResult> FetchAsync(string? etag, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
            if (!string.IsNullOrWhiteSpace(etag))
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.LogInformation("V2EX feed not modified (304).");
                return new FeedFetchResult(FeedFetchOutcome.NotModified, status, etag, null, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("V2EX feed fetch failed: HTTP {Status}.", status);
                return new FeedFetchResult(FeedFetchOutcome.Error, status, null, null, $"HTTP {status}");
            }

            var newEtag = response.Headers.ETag?.ToString();
            var xml = await response.Content.ReadAsStringAsync(ct);
            var feed = AtomParser.Parse(xml);
            _logger.LogInformation("V2EX feed fetched (200): {Count} entries.", feed.Entries.Count);
            return new FeedFetchResult(FeedFetchOutcome.Fetched, status, newEtag, feed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching/parsing V2EX feed.");
            return new FeedFetchResult(FeedFetchOutcome.Error, 0, null, null, ex.Message);
        }
    }
}
