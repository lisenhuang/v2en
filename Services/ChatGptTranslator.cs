using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace v2en.Services;

/// <summary>
/// Translates one post through a ChatGPT plan's Codex-backed models, using the same wire protocol as
/// the Codex CLI: the OpenAI <b>Responses</b> API at <c>chatgpt.com/backend-api/codex/responses</c>,
/// authenticated with the OAuth access token obtained by "Sign in with ChatGPT" (see
/// <see cref="ChatGptAuthService"/>). The endpoint always streams Server-Sent Events, so we read the
/// stream and reconstruct the final message text.
///
/// Output shape and the "did it actually translate?" guard are shared with OpenRouter via
/// <see cref="TranslationParsing"/>, so a ChatGPT primary and an OpenRouter fallback are interchangeable.
/// </summary>
public class ChatGptTranslator
{
    private readonly HttpClient _http;
    private readonly ILogger<ChatGptTranslator> _logger;

    public ChatGptTranslator(HttpClient http, ILogger<ChatGptTranslator> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<TranslationOutcome> TranslateAsync(
        string titleZh,
        string contentHtmlZh,
        string accessToken,
        string accountId,
        string model,
        string? reasoningEffort,
        int maxOutputTokens,
        CancellationToken ct)
    {
        var attempts = new List<TranslationAttempt>();
        if (string.IsNullOrWhiteSpace(model))
            return new TranslationOutcome(false, null, null, null, false, "No ChatGPT model configured", attempts);

        var userJson = JsonSerializer.Serialize(new { title = titleZh, content = contentHtmlZh });
        var effort = NormalizeEffort(reasoningEffort);

        // Responses API request body — Codex quirks: store:false and non-empty instructions are required;
        // include reasoning.encrypted_content whenever we ask for reasoning so a stored:false turn is valid.
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["instructions"] = TranslationParsing.SystemPrompt,
            ["input"] = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new object[] { new { type = "input_text", text = userJson } },
                },
            },
            ["tools"] = Array.Empty<object>(),
            ["tool_choice"] = "auto",
            ["parallel_tool_calls"] = false,
            ["store"] = false,
            ["stream"] = true,
            ["max_output_tokens"] = maxOutputTokens,
            ["prompt_cache_key"] = Guid.NewGuid().ToString(),
        };
        if (effort is not null)
        {
            body["reasoning"] = new { effort, summary = "auto" };
            body["include"] = new[] { "reasoning.encrypted_content" };
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "responses")
            {
                Content = JsonContent.Create(body),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            if (!string.IsNullOrWhiteSpace(accountId))
                req.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            req.Headers.TryAddWithoutValidation("session-id", Guid.NewGuid().ToString());
            // The Codex backend reads the CLI version from User-Agent and can degrade requests from
            // clients it deems outdated — so the "codex_cli_rs/<version>" prefix must be present and current.
            req.Headers.TryAddWithoutValidation("User-Agent", $"codex_cli_rs/{ChatGptModelsService.CodexCliVersion} (v2en-translator)");
            req.Headers.Accept.ParseAdd("text/event-stream");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var err = await SafeBodyAsync(resp, ct);
                attempts.Add(new TranslationAttempt(model, status, "429_account", ClipDetail(err)));
                _logger.LogWarning("ChatGPT 429 (rate/quota) on {Model}: {Body}", model, Clip(err));
                return new TranslationOutcome(false, null, null, model, RateLimited: true, "429 (ChatGPT rate/quota)", attempts);
            }

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                var err = await SafeBodyAsync(resp, ct);
                attempts.Add(new TranslationAttempt(model, status, "auth_error", ClipDetail(err)));
                return new TranslationOutcome(false, null, null, model, false,
                    $"ChatGPT rejected the token (HTTP {status}) — reconnect the account in Settings.", attempts);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var err = await SafeBodyAsync(resp, ct);
                attempts.Add(new TranslationAttempt(model, status, "http_error", ClipDetail(err)));
                return new TranslationOutcome(false, null, null, model, false, $"HTTP {status} from ChatGPT: {Clip(err)}", attempts);
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            var (content, streamError) = mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
                ? await ReadResponsesStreamAsync(resp, ct)
                : await ReadResponsesJsonAsync(resp, ct);

            if (streamError is not null)
            {
                attempts.Add(new TranslationAttempt(model, status, "api_error", ClipDetail(streamError)));
                return new TranslationOutcome(false, null, null, model, false, $"ChatGPT error: {Clip(streamError)}", attempts);
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                attempts.Add(new TranslationAttempt(model, status, "empty", null));
                return new TranslationOutcome(false, null, null, model, false, $"Empty content from {model}", attempts);
            }

            if (TranslationParsing.TryParseTranslation(content, out var title, out var html))
            {
                if (TranslationParsing.LooksUntranslated(title, html))
                {
                    attempts.Add(new TranslationAttempt(model, status, "untranslated",
                        "Output still contained Chinese (or had no title); English is required."));
                    return new TranslationOutcome(false, null, null, model, false, $"Model returned untranslated Chinese on {model}", attempts);
                }
                attempts.Add(new TranslationAttempt(model, status, "ok", null));
                return new TranslationOutcome(true, title, html, model, false, null, attempts);
            }

            attempts.Add(new TranslationAttempt(model, status, "unparseable", ClipDetail(content)));
            return new TranslationOutcome(false, null, null, model, false, $"Unparseable JSON from {model}", attempts);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT call failed on {Model}.", model);
            attempts.Add(new TranslationAttempt(model, null, "exception", ClipDetail(ex.ToString())));
            return new TranslationOutcome(false, null, null, model, false, ex.Message, attempts);
        }
    }

    /// <summary>
    /// Read the Responses SSE stream and reconstruct the assistant's text. We accumulate
    /// <c>response.output_text.delta</c> chunks and, as a fallback, read the final text out of the
    /// <c>response.completed</c> event. A <c>response.failed</c>/<c>error</c> event returns an error.
    /// </summary>
    private static async Task<(string? Content, string? Error)> ReadResponsesStreamAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var deltas = new StringBuilder();
        string? completedText = null;
        string? error = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            line = line.TrimEnd('\r');
            if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; } // ignore keep-alives / non-JSON frames

            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "response.output_text.delta":
                        if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                            deltas.Append(d.GetString());
                        break;
                    case "response.completed":
                    case "response.incomplete":
                        if (root.TryGetProperty("response", out var respEl))
                            completedText = ExtractOutputText(respEl) ?? completedText;
                        break;
                    case "response.failed":
                    case "error":
                        error = ExtractErrorMessage(root) ?? "The ChatGPT response failed.";
                        break;
                }
            }
        }

        if (error is not null) return (null, error);
        var text = deltas.Length > 0 ? deltas.ToString() : completedText;
        return (text, null);
    }

    /// <summary>Fallback: the backend replied with a single (non-streamed) Responses JSON object.</summary>
    private static async Task<(string? Content, string? Error)> ReadResponsesJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Either a bare response object, or { "response": { … } }.
            var response = root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object ? r : root;
            if (response.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return (null, m.GetString());
            return (ExtractOutputText(response), null);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Pull the concatenated output_text out of a Responses <c>response</c> object.</summary>
    private static string? ExtractOutputText(JsonElement response)
    {
        if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            return null;
        var sb = new StringBuilder();
        foreach (var item in output.EnumerateArray())
        {
            if (!(item.TryGetProperty("type", out var it) && it.GetString() == "message")) continue;
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in content.EnumerateArray())
            {
                var pt = part.TryGetProperty("type", out var ptEl) ? ptEl.GetString() : null;
                if ((pt == "output_text" || pt == "text") && part.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
                    sb.Append(txt.GetString());
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string? ExtractErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("response", out var r) && r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            root = e;
        else if (root.TryGetProperty("error", out var e2) && e2.ValueKind == JsonValueKind.Object)
            root = e2;
        if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            return m.GetString();
        return null;
    }

    /// <summary>Normalize a reasoning effort to a known lowercase value, or null to omit reasoning.</summary>
    private static string? NormalizeEffort(string? effort)
    {
        var e = effort?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(e) || e == "none" || e == "default") return null;
        return e;
    }

    private static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 300 ? s : s[..300]);

    private static string? ClipDetail(string? s) =>
        string.IsNullOrEmpty(s) ? null : (s.Length <= 4000 ? s : s[..4000] + " …[truncated]");

    private static async Task<string> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }
}
