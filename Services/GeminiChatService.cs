using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace v2en.Services;

public record ChatSource(long V2exId, string Title, string Url, DateTimeOffset Published);
public record ChatResult(bool Success, string? Answer, IReadOnlyList<ChatSource> Sources, int? HttpStatus, string? Error, string? Detail = null);

/// <summary>
/// Calls Gemini generateContent to answer a question from retrieved posts, using the PUBLIC
/// visitor's OWN API key supplied per request. The key is placed on a fresh HttpRequestMessage
/// (never on the shared client's DefaultRequestHeaders) and is never stored or logged.
/// </summary>
public class GeminiChatService
{
    private const string SystemInstruction =
        "You are a helpful assistant for an English mirror of the Chinese tech forum V2EX. " +
        "You are given a set of forum posts as context (each marked [#n], with a title, time, URL, " +
        "and an excerpt; some are translated to English, some are the Chinese original). " +
        "ENGLISH ONLY: the user's question must be written in English. If the question is NOT in " +
        "English, do not answer it — reply with exactly: \"Please ask your question in English.\" " +
        "Always write your entire answer in English, even when the source posts are in Chinese. " +
        "Answer the user's question USING ONLY these posts. Summarize the relevant posts' points of " +
        "view, be concise, and cite the posts you used with their [#n] markers. If none of the posts " +
        "are relevant, say so plainly.";

    private readonly HttpClient _http;
    private readonly ILogger<GeminiChatService> _logger;

    public GeminiChatService(HttpClient http, ILogger<GeminiChatService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ChatResult> AnswerAsync(
        string question, string userApiKey, string model, IReadOnlyList<RetrievedPost> context, CancellationToken ct)
    {
        var sources = context
            .Select(p => new ChatSource(p.V2exId, p.Title, p.SourceUrl, p.Published))
            .ToList();

        var prompt = BuildPrompt(question, context);
        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = SystemInstruction } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.3 },
        };

        // Gemini answers 503 UNAVAILABLE when a model is momentarily overloaded and 500 on transient
        // internal errors — Google's guidance is to retry these with backoff. Client errors (key/quota,
        // 4xx) won't change on retry, so we surface those immediately.
        const int maxAttempts = 3;
        int lastStatus = 0;
        string lastRaw = "";
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{model}:generateContent")
                {
                    Content = JsonContent.Create(body),
                };
                req.Headers.TryAddWithoutValidation("x-goog-api-key", userApiKey); // per-request; never stored/logged
                using var resp = await _http.SendAsync(req, ct);
                var status = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                    var answer = ExtractText(doc.RootElement);
                    if (string.IsNullOrWhiteSpace(answer))
                        return new ChatResult(false, null, sources, status,
                            "The model returned no answer (it may have been blocked by a safety filter). Try rephrasing.");

                    return new ChatResult(true, answer, sources, status, null);
                }

                lastStatus = status;
                lastRaw = await SafeBodyAsync(resp, ct);

                if (status >= 500 && attempt < maxAttempts)
                {
                    _logger.LogInformation("Gemini chat got {Status} (attempt {Attempt}/{Max}); retrying.", status, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), ct);
                    continue;
                }
                return new ChatResult(false, null, sources, status, MapError(status, lastRaw), Clip(lastRaw));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(ex, "Gemini chat call failed (attempt {Attempt}/{Max}); retrying.", attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gemini chat call failed.");
                return new ChatResult(false, null, sources, null, "Couldn't reach the AI service. Try again shortly.", ex.Message);
            }
        }

        // All attempts exhausted on a 5xx (or a transient exception on the final attempt).
        return new ChatResult(false, null, sources, lastStatus == 0 ? null : lastStatus,
            MapError(lastStatus, lastRaw), Clip(lastRaw));
    }

    private static string Clip(string s) => string.IsNullOrEmpty(s) || s.Length <= 2000 ? s : s[..2000];

    private static string BuildPrompt(string question, IReadOnlyList<RetrievedPost> context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context posts:");
        for (int i = 0; i < context.Count; i++)
        {
            var p = context[i];
            sb.AppendLine($"[#{i + 1}] {p.Title}");
            sb.AppendLine($"    published: {p.Published:yyyy-MM-dd HH:mm} UTC | {p.SourceUrl}");
            if (!string.IsNullOrWhiteSpace(p.Snippet))
                sb.AppendLine($"    excerpt: {p.Snippet}");
        }
        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        sb.AppendLine("Answer in English, summarize the relevant posts' views, and cite [#n] markers.");
        return sb.ToString();
    }

    private static string? ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return null;
        var first = cands[0];
        if (!first.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            return null;
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.GetString() is { } s)
                sb.Append(s);
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string MapError(int status, string raw) => status switch
    {
        400 => "Your API key was rejected or the request was invalid. Check your Google AI Studio key.",
        401 or 403 => "Your API key was rejected. Check your Google AI Studio key and its permissions.",
        429 => "Your API key hit its rate limit. Wait a bit and try again.",
        503 => "The AI model is busy right now (Google returned \"overloaded\"). Please try again in a few seconds, or pick a different chat model in the dashboard.",
        >= 500 => "Google's AI service had a temporary error. Try again shortly.",
        _ => "The AI request failed. Try again.",
    };

    private static async Task<string> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }
}
