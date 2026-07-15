using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace v2en.Services;

/// <summary>One model attempt within a translation call — surfaced to the admin log.</summary>
public record TranslationAttempt(string Model, int? HttpStatus, string Outcome, string? Detail);

public record TranslationOutcome(
    bool Success,
    string? Title,
    string? ContentHtml,
    string? Model,
    bool RateLimited,
    string? Error,
    IReadOnlyList<TranslationAttempt> Attempts);

/// <summary>
/// Typed HttpClient calling OpenRouter's OpenAI-compatible chat/completions endpoint.
/// Translates ONE post per call (title + the HTML body already in the RSS feed), using a
/// FREE-models-only fallback chain supplied by the caller (admin-configured at runtime).
/// Each model attempt is recorded so the dashboard can show the raw AI API error.
/// </summary>
public class OpenRouterTranslator
{
    private const string SystemPrompt = TranslationParsing.SystemPrompt;

    private readonly HttpClient _http;
    private readonly ILogger<OpenRouterTranslator> _logger;

    public OpenRouterTranslator(HttpClient http, ILogger<OpenRouterTranslator> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<TranslationOutcome> TranslateAsync(
        string titleZh,
        string contentHtmlZh,
        string apiKey,
        IReadOnlyList<string> models,
        int maxOutputTokens,
        double temperature,
        CancellationToken ct)
    {
        var userJson = JsonSerializer.Serialize(new { title = titleZh, content = contentHtmlZh });
        var attempts = new List<TranslationAttempt>();
        string? lastError = null;
        // True once any model gives a non-429 response (a genuine attempt). If every model is
        // 429, the failure is purely transient rate-limiting → the caller should back off and
        // retry later WITHOUT charging an attempt against this post.
        bool sawNon429 = false;

        foreach (var model in models)
        {
            // Hard guard: free models only, never paid.
            if (!model.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping non-free model '{Model}' (free-only policy).", model);
                attempts.Add(new TranslationAttempt(model, null, "skipped_nonfree", "Free-only policy: model id must end with ':free'."));
                continue;
            }

            var body = new
            {
                model,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = userJson },
                },
                temperature,
                max_tokens = maxOutputTokens,
                response_format = new { type = "json_object" },
            };

            try
            {
                using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
                {
                    Content = JsonContent.Create(body),
                };
                httpReq.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await _http.SendAsync(httpReq, ct);
                var status = (int)resp.StatusCode;

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var err = await SafeReadBodyAsync(resp, ct);
                    // OpenRouter forwards a busy upstream provider's throttle as a 429 too. Those
                    // are per-model, so we fall through to the next model. Only an account-level
                    // 429 (no provider metadata) means the whole account is limited → stop now.
                    if (IsAccountLevelRateLimit(err))
                    {
                        _logger.LogWarning("OpenRouter account-level 429 (rate/quota): {Body}", Clip(err));
                        attempts.Add(new TranslationAttempt(model, status, "429_account", ClipDetail(err)));
                        return new TranslationOutcome(false, null, null, model, RateLimited: true, "429 (account rate/quota)", attempts);
                    }

                    lastError = $"429 (upstream provider throttle) on {model}";
                    _logger.LogWarning("{Error}; trying next model. {Body}", lastError, Clip(err));
                    attempts.Add(new TranslationAttempt(model, status, "429_upstream", ClipDetail(err)));
                    continue;
                }

                // Anything other than a 429 is a real answer from a provider — a genuine attempt.
                sawNon429 = true;

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await SafeReadBodyAsync(resp, ct);
                    lastError = $"HTTP {status} from {model}: {Clip(err)}";
                    _logger.LogWarning("{Error}; trying next model.", lastError);
                    attempts.Add(new TranslationAttempt(model, status, "http_error", ClipDetail(err)));
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var choice = json.RootElement.GetProperty("choices")[0];
                var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
                var content = choice.GetProperty("message").GetProperty("content").GetString();

                if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
                {
                    // Post too long for this model's effective output cap — the HTML is likely truncated/broken.
                    lastError = $"Truncated (finish_reason=length) on {model}";
                    _logger.LogWarning("{Error}; trying next model.", lastError);
                    attempts.Add(new TranslationAttempt(model, status, "truncated", "finish_reason=length — output exceeded max_tokens; raise it or the post is too long."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    lastError = $"Empty content from {model}";
                    attempts.Add(new TranslationAttempt(model, status, "empty", null));
                    continue;
                }

                if (TranslationParsing.TryParseTranslation(content, out var title, out var html))
                {
                    // Reject output the model failed to translate (missing title, or title/body still
                    // Chinese) so the next model in the chain gets a try — we never store Chinese as
                    // the "English" version.
                    if (TranslationParsing.LooksUntranslated(title, html))
                    {
                        lastError = $"Model returned untranslated Chinese on {model}";
                        _logger.LogWarning("{Error}; trying next model.", lastError);
                        attempts.Add(new TranslationAttempt(model, status, "untranslated",
                            "Output still contained Chinese (or had no title); English is required."));
                        continue;
                    }
                    attempts.Add(new TranslationAttempt(model, status, "ok", null));
                    return new TranslationOutcome(true, title, html, model, RateLimited: false, null, attempts);
                }

                lastError = $"Unparseable JSON from {model}";
                _logger.LogWarning("{Error}; trying next model.", lastError);
                attempts.Add(new TranslationAttempt(model, status, "unparseable", ClipDetail(content)));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A network/transport failure is a genuine attempt, not rate-limiting.
                sawNon429 = true;
                lastError = ex.Message;
                _logger.LogWarning(ex, "OpenRouter call failed on {Model}; trying next model.", model);
                attempts.Add(new TranslationAttempt(model, null, "exception", ClipDetail(ex.ToString())));
            }
        }

        // RateLimited only when every model we tried returned 429 (nothing actually served us):
        // transient, so the caller retries later without burning an attempt.
        return new TranslationOutcome(false, null, null, null, RateLimited: !sawNon429,
            lastError ?? "No free models configured", attempts);
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    /// <summary>
    /// An OpenRouter 429 is account-wide only when it carries no upstream-provider context.
    /// Provider/upstream throttles include a provider name or an "upstream" hint and should be
    /// retried on the next model instead of aborting the whole tick.
    /// </summary>
    private static bool IsAccountLevelRateLimit(string body)
    {
        if (string.IsNullOrEmpty(body)) return false; // unknown → assume per-model, try next
        var b = body.ToLowerInvariant();
        var upstream = b.Contains("provider_name")
            || b.Contains("rate-limited upstream")
            || b.Contains("temporarily rate-limited");
        return !upstream;
    }

    private static string Clip(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s[..300]);

    /// <summary>Longer clip for the stored admin log detail (raw AI API error body).</summary>
    private static string? ClipDetail(string? s) =>
        string.IsNullOrEmpty(s) ? null : (s.Length <= 4000 ? s : s[..4000] + " …[truncated]");
}
