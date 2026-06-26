using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace v2en.Services;

public record ChatSource(long V2exId, string Title, string Url, DateTimeOffset Published);
public record ChatResult(bool Success, string? Answer, IReadOnlyList<ChatSource> Sources, int? HttpStatus, string? Error, string? Detail = null);

/// <summary>
/// Calls Gemini generateContent to answer a question from retrieved posts, using the PUBLIC
/// visitor's OWN API key supplied per request. The key is placed on a fresh HttpRequestMessage
/// (never on the shared client's DefaultRequestHeaders) and is never stored or logged.
///
/// The model may call the <c>get_post_details</c> function to pull a post's FULL body plus its
/// (Chinese) replies on demand; we run a short tool-call loop so the answer can be grounded in the
/// original discussion, not just the retrieval excerpts.
/// </summary>
public class GeminiChatService
{
    private const string SystemInstruction =
        "You are a helpful assistant for an English mirror of the Chinese tech forum V2EX. " +
        "You are given a set of forum posts as context (each marked [#n], with an id, title, time, " +
        "and an excerpt; some are translated to English, some are the Chinese original). " +
        "ENGLISH ONLY: the user's question must be written in English. If the question is NOT in " +
        "English, do not answer it — reply with exactly: \"Please ask your question in English.\" " +
        "When you need the full text of a post or what people replied/commented, call the " +
        "get_post_details function with that post's numeric id. Its replies come back in the original " +
        "Chinese — read them, but ALWAYS write your entire answer in English, even when the post or " +
        "replies are in Chinese. Answer the user's question USING ONLY these posts and any details you " +
        "fetch. Summarize the relevant posts' points of view, be concise, and cite the posts you used " +
        "with their [#n] markers. Only ever share links to THIS mirror (paths like /t/<id>); never link " +
        "to v2ex.com. If none of the posts are relevant, say so plainly.";

    // Tool surface offered to the model. Kept in a field so every turn sends the same declaration.
    private static readonly object Tools = new[]
    {
        new
        {
            functionDeclarations = new[]
            {
                new
                {
                    name = "get_post_details",
                    description = "Fetch the FULL original text of one post plus its discussion replies " +
                        "(replies are in the original Chinese). Call this when the user asks about the details " +
                        "of a post, the full post text, or what people replied/commented. Pass the post's " +
                        "numeric id from the context.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            v2ex_id = new { type = "integer", description = "The numeric post id from the context, e.g. 1222343." }
                        },
                        required = new[] { "v2ex_id" }
                    }
                }
            }
        }
    };

    private const int MaxToolTurns = 4;     // total model turns, including tool round-trips

    private readonly HttpClient _http;
    private readonly PostDetailsService _details;
    private readonly ILogger<GeminiChatService> _logger;

    public GeminiChatService(HttpClient http, PostDetailsService details, ILogger<GeminiChatService> logger)
    {
        _http = http;
        _details = details;
        _logger = logger;
    }

    public async Task<ChatResult> AnswerAsync(
        string question, string userApiKey, string model, IReadOnlyList<RetrievedPost> context, CancellationToken ct)
    {
        var sources = context
            .Select(p => new ChatSource(p.V2exId, p.Title, p.SourceUrl, p.Published))
            .ToList();

        var systemInstruction = new { parts = new[] { new { text = SystemInstruction } } };
        var generationConfig = new { temperature = 0.3 };

        // Mutable conversation; it grows as the model calls get_post_details and we feed results back.
        var contents = new List<object>
        {
            new { role = "user", parts = new object[] { new { text = BuildPrompt(question, context) } } }
        };

        for (int turn = 0; turn < MaxToolTurns; turn++)
        {
            // On the final turn, drop the tools so the model is forced to produce a text answer.
            var allowTools = turn < MaxToolTurns - 1;
            object body = allowTools
                ? new { systemInstruction, contents, tools = Tools, generationConfig }
                : new { systemInstruction, contents, generationConfig };

            var gen = await CallAsync(body, userApiKey, model, ct);
            if (!gen.Ok)
                return new ChatResult(false, null, sources, gen.Status == 0 ? null : gen.Status,
                    MapError(gen.Status, gen.Raw), Clip(gen.Raw));

            if (gen.Calls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(gen.Text))
                    return new ChatResult(false, null, sources, gen.Status,
                        "The model returned no answer (it may have been blocked by a safety filter). Try rephrasing.");
                // List only the posts the answer actually cited, not the whole retrieval context.
                return new ChatResult(true, gen.Text, FilterCitedSources(gen.Text, sources), gen.Status, null);
            }

            // Echo the model's function-call turn back verbatim, then append our function results.
            contents.Add(new { role = "model", parts = gen.Calls.Select(fc => new { functionCall = new { name = fc.Name, args = fc.Args } }).ToArray() });
            var responseParts = new List<object>();
            foreach (var fc in gen.Calls)
            {
                var resultText = await ExecuteToolAsync(fc, ct);
                responseParts.Add(new { functionResponse = new { name = fc.Name, response = new { result = resultText } } });
            }
            contents.Add(new { role = "user", parts = responseParts });
        }

        // Should be unreachable (the final turn has no tools), but guard anyway.
        return new ChatResult(false, null, sources, null,
            "The assistant couldn't finish the answer. Please try again.");
    }

    // ── Tool execution ──────────────────────────────────────────────────────────────────────────
    private async Task<string> ExecuteToolAsync(FuncCall fc, CancellationToken ct)
    {
        if (fc.Name != "get_post_details") return "Unknown function.";

        long id = 0;
        if (fc.Args is JsonElement je && je.ValueKind == JsonValueKind.Object && je.TryGetProperty("v2ex_id", out var idEl))
        {
            if (idEl.ValueKind == JsonValueKind.Number) idEl.TryGetInt64(out id);
            else if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var pid)) id = pid;
        }
        if (id <= 0) return "Invalid post id — pass the numeric id shown in the context.";

        var d = await _details.GetAsync(id, ct);
        if (!d.Found) return "That post was not found in the mirror.";

        var sb = new StringBuilder();
        sb.AppendLine($"Post (id {d.V2exId}): {d.Title}");
        sb.AppendLine($"Mirror link: {d.LocalUrl}");
        sb.AppendLine("Full body:");
        sb.AppendLine(string.IsNullOrWhiteSpace(d.Body) ? "(empty)" : d.Body);
        if (d.Replies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Replies ({d.Replies.Count}, in the original Chinese — answer in English):");
            int n = 1;
            foreach (var r in d.Replies)
                sb.AppendLine($"{n++}. {(string.IsNullOrEmpty(r.Author) ? "user" : r.Author)}: {r.Text}");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Replies: none available (the reply feed could not be retrieved).");
        }
        return sb.ToString();
    }

    // ── HTTP + parsing ──────────────────────────────────────────────────────────────────────────
    private sealed record FuncCall(string Name, object Args);
    private sealed record GenResult(bool Ok, int Status, string Raw, string? Text, IReadOnlyList<FuncCall> Calls);

    private async Task<GenResult> CallAsync(object body, string userApiKey, string model, CancellationToken ct)
    {
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
                    var (text, calls) = ParseCandidate(doc.RootElement);
                    return new GenResult(true, status, "", text, calls);
                }

                lastStatus = status;
                lastRaw = await SafeBodyAsync(resp, ct);

                if (status >= 500 && attempt < maxAttempts)
                {
                    _logger.LogInformation("Gemini chat got {Status} (attempt {Attempt}/{Max}); retrying.", status, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), ct);
                    continue;
                }
                return new GenResult(false, status, lastRaw, null, Array.Empty<FuncCall>());
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
                return new GenResult(false, 0, ex.Message, null, Array.Empty<FuncCall>());
            }
        }

        return new GenResult(false, lastStatus, lastRaw, null, Array.Empty<FuncCall>());
    }

    /// <summary>Pulls the concatenated text and any functionCall parts out of candidates[0].content.
    /// functionCall args are cloned so they remain valid after the JsonDocument is disposed.</summary>
    private static (string? Text, IReadOnlyList<FuncCall> Calls) ParseCandidate(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return (null, Array.Empty<FuncCall>());
        var first = cands[0];
        if (!first.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return (null, Array.Empty<FuncCall>());

        var sb = new StringBuilder();
        var calls = new List<FuncCall>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fcEl) && fcEl.ValueKind == JsonValueKind.Object)
            {
                var name = fcEl.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                object args = fcEl.TryGetProperty("args", out var a) ? a.Clone() : new { };
                if (name.Length > 0) calls.Add(new FuncCall(name, args));
            }
            else if (part.TryGetProperty("text", out var t) && t.GetString() is { } s)
            {
                sb.Append(s);
            }
        }
        return (sb.Length == 0 ? null : sb.ToString(), calls);
    }

    /// <summary>
    /// The answer cites posts with [#n] markers (n is the 1-based index into the context, the same
    /// numbering used to build <see cref="sources"/>). Return only the cited posts — in first-cited
    /// order — so the "Sources" list reflects what the answer actually used instead of the entire
    /// retrieval set. Falls back to all sources when the model wrote no markers, preserving attribution.
    /// </summary>
    private static IReadOnlyList<ChatSource> FilterCitedSources(string answer, IReadOnlyList<ChatSource> sources)
    {
        if (string.IsNullOrEmpty(answer) || sources.Count == 0) return sources;

        var cited = new List<ChatSource>();
        var seen = new HashSet<int>();
        foreach (Match m in Regex.Matches(answer, @"\[#(\d+)\]"))
        {
            if (int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= sources.Count && seen.Add(n))
                cited.Add(sources[n - 1]);
        }
        return cited.Count > 0 ? cited : sources;
    }

    private static string Clip(string s) => string.IsNullOrEmpty(s) || s.Length <= 2000 ? s : s[..2000];

    private static string BuildPrompt(string question, IReadOnlyList<RetrievedPost> context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context posts (each [#n] shows its numeric id):");
        for (int i = 0; i < context.Count; i++)
        {
            var p = context[i];
            sb.AppendLine($"[#{i + 1}] (id: {p.V2exId}) {p.Title}");
            sb.AppendLine($"    published: {p.Published:yyyy-MM-dd HH:mm} UTC");
            if (!string.IsNullOrWhiteSpace(p.Snippet))
                sb.AppendLine($"    excerpt: {p.Snippet}");
        }
        sb.AppendLine();
        sb.AppendLine($"Question: {question}");
        sb.AppendLine("If answering needs the full post text or what people replied, call get_post_details with " +
                      "that post's id. Summarize the relevant posts' views, cite [#n] markers, and ALWAYS answer in English.");
        return sb.ToString();
    }

    private static string MapError(int status, string raw) => status switch
    {
        400 => "Your API key was rejected or the request was invalid. Check your Google AI Studio key.",
        401 or 403 => "Your API key was rejected. Check your Google AI Studio key and its permissions.",
        429 => "Your API key hit its rate limit. Wait a bit and try again.",
        503 => "The AI model is busy right now (Google returned \"overloaded\"). Please try again in a few seconds, or pick a different chat model in the dashboard.",
        >= 500 => "Google's AI service had a temporary error. Try again shortly.",
        0 => "Couldn't reach the AI service. Try again shortly.",
        _ => "The AI request failed. Try again.",
    };

    private static async Task<string> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }
}
