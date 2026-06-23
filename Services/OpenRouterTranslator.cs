using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using v2en.Configuration;

namespace v2en.Services;

public record TranslationOutcome(
    bool Success,
    string? Title,
    string? ContentHtml,
    string? Model,
    bool RateLimited,
    string? Error);

/// <summary>
/// Typed HttpClient calling OpenRouter's OpenAI-compatible chat/completions endpoint.
/// Translates ONE post per call (title + the HTML body already in the RSS feed),
/// using a FREE-models-only fallback chain. On 429 it stops immediately (the limit is
/// account-wide, so trying another model would just burn quota).
/// </summary>
public class OpenRouterTranslator
{
    private const string SystemPrompt =
        "You are a professional Chinese-to-English translator for the tech forum V2EX. " +
        "You receive a JSON object {\"title\":\"...\",\"content\":\"...\"} where content is HTML. " +
        "Translate the Chinese text into natural, fluent English. " +
        "Respond with ONLY a JSON object of the same shape: {\"title\":\"...\",\"content\":\"...\"}. " +
        "In content, preserve every HTML tag, attribute, and URL (href/src) exactly as given; translate only human-readable text. " +
        "Do NOT translate code inside <code>/<pre>, URLs, or usernames. " +
        "Do NOT wrap the JSON in markdown code fences and do NOT add any commentary.";

    private readonly HttpClient _http;
    private readonly OpenRouterOptions _or;
    private readonly TranslationOptions _t;
    private readonly ILogger<OpenRouterTranslator> _logger;

    public OpenRouterTranslator(
        HttpClient http,
        IOptions<OpenRouterOptions> or,
        IOptions<TranslationOptions> t,
        ILogger<OpenRouterTranslator> logger)
    {
        _http = http;
        _or = or.Value;
        _t = t.Value;
        _logger = logger;
    }

    public async Task<TranslationOutcome> TranslateAsync(string titleZh, string contentHtmlZh, CancellationToken ct)
    {
        var userJson = JsonSerializer.Serialize(new { title = titleZh, content = contentHtmlZh });
        string? lastError = null;
        // True once any model gives a non-429 response (a genuine attempt). If every model is
        // 429, the failure is purely transient rate-limiting → the caller should back off and
        // retry later WITHOUT charging an attempt against this post.
        bool sawNon429 = false;

        foreach (var model in _or.Models)
        {
            // Hard guard: free models only, never paid.
            if (!model.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping non-free model '{Model}' (free-only policy).", model);
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
                temperature = _t.Temperature,
                max_tokens = _t.MaxOutputTokens,
                response_format = new { type = "json_object" },
            };

            try
            {
                using var resp = await _http.PostAsJsonAsync("chat/completions", body, ct);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var err = await SafeReadBodyAsync(resp, ct);
                    // OpenRouter forwards a busy upstream provider's throttle as a 429 too. Those
                    // are per-model, so we fall through to the next model. Only an account-level
                    // 429 (no provider metadata) means the whole account is limited → stop now.
                    if (IsAccountLevelRateLimit(err))
                    {
                        _logger.LogWarning("OpenRouter account-level 429 (rate/quota): {Body}", Clip(err));
                        return new TranslationOutcome(false, null, null, model, RateLimited: true, "429 (account rate/quota)");
                    }

                    lastError = $"429 (upstream provider throttle) on {model}";
                    _logger.LogWarning("{Error}; trying next model. {Body}", lastError, Clip(err));
                    continue;
                }

                // Anything other than a 429 is a real answer from a provider — a genuine attempt.
                sawNon429 = true;

                if (!resp.IsSuccessStatusCode)
                {
                    var err = await SafeReadBodyAsync(resp, ct);
                    lastError = $"HTTP {(int)resp.StatusCode} from {model}: {Clip(err)}";
                    _logger.LogWarning("{Error}; trying next model.", lastError);
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
                    continue;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    lastError = $"Empty content from {model}";
                    continue;
                }

                if (TryParseTranslation(content, out var title, out var html))
                    return new TranslationOutcome(true, title, html, model, RateLimited: false, null);

                lastError = $"Unparseable JSON from {model}";
                _logger.LogWarning("{Error}; trying next model.", lastError);
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
            }
        }

        // RateLimited only when every model we tried returned 429 (nothing actually served us):
        // transient, so the caller retries later without burning an attempt.
        return new TranslationOutcome(false, null, null, null, RateLimited: !sawNon429,
            lastError ?? "No free models configured");
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

    private static bool TryParseTranslation(string content, out string title, out string html)
    {
        title = string.Empty;
        html = string.Empty;
        var json = ExtractJson(content);
        // Many models emit raw newlines/tabs inside the "content" HTML string instead of the
        // required \n / \t escapes, which strict JSON parsing rejects. Try as-is first, then
        // retry with control characters inside strings escaped.
        foreach (var candidate in new[] { json, EscapeControlCharsInStrings(json) })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                html = root.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                if (!string.IsNullOrEmpty(html) || !string.IsNullOrEmpty(title))
                    return true;
            }
            catch
            {
                // try the next candidate
            }
        }
        return false;
    }

    /// <summary>
    /// Escapes raw control characters (newlines, tabs, …) that appear INSIDE JSON string values,
    /// which some models emit unescaped. Structural whitespace outside strings is left untouched,
    /// so both compact and pretty-printed JSON round-trip correctly.
    /// </summary>
    private static string EscapeControlCharsInStrings(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 32);
        bool inString = false, escaped = false;
        foreach (var ch in s)
        {
            if (inString)
            {
                if (escaped) { sb.Append(ch); escaped = false; continue; }
                if (ch == '\\') { sb.Append(ch); escaped = true; continue; }
                if (ch == '"') { sb.Append(ch); inString = false; continue; }
                if (ch < 0x20)
                {
                    sb.Append(ch switch
                    {
                        '\n' => "\\n",
                        '\r' => "\\r",
                        '\t' => "\\t",
                        '\b' => "\\b",
                        '\f' => "\\f",
                        _ => "\\u" + ((int)ch).ToString("x4"),
                    });
                    continue;
                }
                sb.Append(ch);
            }
            else
            {
                sb.Append(ch);
                if (ch == '"') inString = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>Defensive: strip ``` fences and isolate the outermost {...} object.</summary>
    private static string ExtractJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = s.IndexOf('\n');
            if (firstNewline >= 0) s = s[(firstNewline + 1)..];
            var lastFence = s.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence >= 0) s = s[..lastFence];
        }
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        return start >= 0 && end > start ? s[start..(end + 1)] : s.Trim();
    }
}
