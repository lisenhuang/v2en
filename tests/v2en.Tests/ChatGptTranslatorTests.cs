using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// Guards the ChatGPT/Codex Responses request contract. The Codex subscription backend
/// (chatgpt.com/backend-api/codex/responses) rejects <c>max_output_tokens</c> with HTTP 400
/// "Unsupported parameter" — the Codex CLI never sends it — so the body must not include it (nor any
/// other output-token cap), while every other supported field is preserved.
/// </summary>
public class ChatGptTranslatorTests
{
    private const string DummyToken = "SECRET-TOKEN-should-not-leak";
    private const string DummyAccount = "acct_123";

    private static JsonElement Body(string? effort) =>
        JsonSerializer.SerializeToElement(
            ChatGptTranslator.BuildResponsesRequestBody("标题", "<p>内容</p>", "gpt-5.6-sol", effort));

    // ── Requirement 7.1 + 7.2: no unsupported token-cap field; supported fields preserved ──

    [Theory]
    [InlineData("max_output_tokens")]
    [InlineData("max_tokens")]
    [InlineData("max_completion_tokens")]
    public void RequestBody_HasNoOutputTokenCapField(string forbiddenKey)
    {
        var root = Body("medium");
        Assert.False(
            root.TryGetProperty(forbiddenKey, out _),
            $"Codex /responses rejects '{forbiddenKey}' — it must not be serialized.");
    }

    [Fact]
    public void RequestBody_KeepsSupportedFields()
    {
        var root = Body("medium");

        Assert.Equal("gpt-5.6-sol", root.GetProperty("model").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("instructions").GetString()));
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
        Assert.False(root.GetProperty("store").GetBoolean());   // Codex requires store:false
        Assert.True(root.GetProperty("stream").GetBoolean());   // always streams SSE
        Assert.Equal(JsonValueKind.Array, root.GetProperty("input").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tools").ValueKind);
        Assert.False(string.IsNullOrEmpty(root.GetProperty("prompt_cache_key").GetString()));
    }

    [Fact]
    public void RequestBody_IncludesReasoning_WhenEffortGiven()
    {
        var root = Body("high");

        var reasoning = root.GetProperty("reasoning");
        Assert.Equal("high", reasoning.GetProperty("effort").GetString());
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());

        // include reasoning.encrypted_content so a store:false turn is valid
        var include = root.GetProperty("include").EnumerateArray().Select(e => e.GetString());
        Assert.Contains("reasoning.encrypted_content", include);
    }

    [Fact]
    public void RequestBody_OmitsReasoning_WhenNoEffort()
    {
        var root = Body(null);
        Assert.False(root.TryGetProperty("reasoning", out _));
        Assert.False(root.TryGetProperty("include", out _));
    }

    // ── Requirement 7.3: a mocked successful SSE response translates ──

    [Fact]
    public async Task Translate_ParsesSuccessfulSseStream()
    {
        var inner = JsonSerializer.Serialize(new { title = "Hello world", content = "<p>Hi there</p>" });
        var deltaEvent = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = inner });
        var sse = $"data: {deltaEvent}\n\ndata: [DONE]\n\n";

        var translator = TranslatorWith(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
            return resp;
        });

        var outcome = await translator.TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("Hello world", outcome.Title);
        Assert.Equal("<p>Hi there</p>", outcome.ContentHtml);
        Assert.False(outcome.RateLimited);
    }

    // ── Requirement 7.4: HTTP 400 stays visible & useful (and 7.1: prove the fix removes the 400 cause) ──

    [Fact]
    public async Task Translate_Surfaces400Error_WithDetail()
    {
        const string errBody = "{\"error\":{\"message\":\"Unsupported parameter: max_output_tokens\"}}";
        var translator = TranslatorWith(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errBody, Encoding.UTF8, "application/json"),
        });

        var outcome = await translator.TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
        Assert.Contains("400", outcome.Error);
        Assert.Contains("Unsupported parameter", outcome.Error);           // the raw API error stays visible
        Assert.Contains(outcome.Attempts, a => a.HttpStatus == 400);       // recorded for the dashboard
    }

    /// <summary>Requirement 8: the OAuth access token must never appear in surfaced errors/attempts.</summary>
    [Fact]
    public async Task Translate_DoesNotLeakAccessToken_OnError()
    {
        var translator = TranslatorWith(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"bad request\"}}", Encoding.UTF8, "application/json"),
        });

        var outcome = await translator.TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.DoesNotContain(DummyToken, outcome.Error ?? "");
        foreach (var a in outcome.Attempts)
            Assert.DoesNotContain(DummyToken, a.Detail ?? "");
    }

    /// <summary>Requirement 7.1 end-to-end: the bytes actually sent on the wire carry no max_output_tokens.</summary>
    [Fact]
    public async Task Translate_SendsBodyWithoutMaxOutputTokens_OnTheWire()
    {
        string? sentBody = null;
        var inner = JsonSerializer.Serialize(new { title = "Hi", content = "" });
        var deltaEvent = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = inner });
        var sse = $"data: {deltaEvent}\n\ndata: [DONE]\n\n";

        var translator = TranslatorWith(async req =>
        {
            sentBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
        });

        await translator.TranslateAsync(
            "标题", "", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.NotNull(sentBody);
        Assert.DoesNotContain("max_output_tokens", sentBody);
        Assert.Contains("\"stream\":true", sentBody);
    }

    // ── SSE parsing (bug: parser was chosen by Content-Type, which the live endpoint omits) ──

    [Fact]
    public async Task Translate_ParsesProductionSse_WithEventLinesAndCrlf()
    {
        var translator = TranslatorReturningSse(ProductionSse("Hello world", "<p>Hi there</p>"), "text/event-stream");

        var outcome = await translator.TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("Hello world", outcome.Title);           // accumulated from response.output_text.delta
        Assert.Equal("<p>Hi there</p>", outcome.ContentHtml);
    }

    /// <summary>THE regression: live Codex responses are chunked SSE with NO Content-Type header.</summary>
    [Fact]
    public async Task Translate_ParsesSse_WhenNoContentTypeHeader()
    {
        var translator = TranslatorReturningSse(ProductionSse("No content type", "<p>works</p>"), contentType: null);

        var outcome = await translator.TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("No content type", outcome.Title);
        Assert.Equal("<p>works</p>", outcome.ContentHtml);
    }

    [Fact]
    public async Task Translate_UsesCompletedOutput_AsFallback_WhenNoDeltas()
    {
        var payload = JsonSerializer.Serialize(new { title = "From completed", content = "<p>fallback</p>" });
        var completed = JsonSerializer.Serialize(new
        {
            type = "response.completed",
            response = new { output = new object[] { new { type = "message", content = new object[] { new { type = "output_text", text = payload } } } } },
        });
        // No output_text.delta events at all — only response.completed carries the text.
        var sse = $"event: response.created\r\ndata: {{\"type\":\"response.created\"}}\r\n\r\n"
                + $"event: response.completed\r\ndata: {completed}\r\n\r\ndata: [DONE]\r\n\r\n";

        var outcome = await TranslatorReturningSse(sse, contentType: null).TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("From completed", outcome.Title);
        Assert.Equal("<p>fallback</p>", outcome.ContentHtml);
    }

    [Fact]
    public async Task Translate_SurfacesResponseFailedEvent()
    {
        var failed = JsonSerializer.Serialize(new
        {
            type = "response.failed",
            response = new { error = new { message = "the model exploded" } },
        });
        var sse = $"event: response.failed\r\ndata: {failed}\r\n\r\n";

        var outcome = await TranslatorReturningSse(sse, "text/event-stream").TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
        Assert.Contains("the model exploded", outcome.Error);
        Assert.DoesNotContain(DummyToken, outcome.Error);            // requirement 8: never leak the token
    }

    /// <summary>Requirement 7: a 200 with no usable text is a clear failure, not a silent empty success.</summary>
    [Fact]
    public async Task Translate_EmptyStream_ReturnsClearDiagnostic()
    {
        var sse = "event: response.created\r\ndata: {\"type\":\"response.created\"}\r\n\r\ndata: [DONE]\r\n\r\n";

        var outcome = await TranslatorReturningSse(sse, contentType: null).TranslateAsync(
            "标题", "<p>内容</p>", DummyToken, DummyAccount, "gpt-5.6-sol", "medium", CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
        Assert.Contains("no usable text", outcome.Error);
    }

    // ── helpers ──

    /// <summary>A production-like Codex SSE stream: event:/data: pairs, CRLF endings, streamed deltas,
    /// then output_text.done and response.completed carrying the full text, and a trailing [DONE].</summary>
    private static string ProductionSse(string title, string html)
    {
        var payload = JsonSerializer.Serialize(new { title, content = html });
        var half = payload.Length / 2;
        static string Frame(string ev, object data) => $"event: {ev}\r\ndata: {JsonSerializer.Serialize(data)}\r\n\r\n";
        return Frame("response.created", new { type = "response.created" })
             + Frame("response.output_text.delta", new { type = "response.output_text.delta", delta = payload[..half] })
             + Frame("response.output_text.delta", new { type = "response.output_text.delta", delta = payload[half..] })
             + Frame("response.output_text.done", new { type = "response.output_text.done", text = payload })
             + Frame("response.completed", new
             {
                 type = "response.completed",
                 response = new { output = new object[] { new { type = "message", content = new object[] { new { type = "output_text", text = payload } } } } },
             })
             + "data: [DONE]\r\n\r\n";
    }

    /// <summary>200 OK returning <paramref name="sse"/>; pass contentType=null to omit the header entirely
    /// (mirroring the live Codex endpoint, which sends only chunked transfer-encoding).</summary>
    private static ChatGptTranslator TranslatorReturningSse(string sse, string? contentType) =>
        TranslatorWith(_ =>
        {
            var content = new StringContent(sse, Encoding.UTF8);
            content.Headers.ContentType = contentType is null ? null : new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });


    private static ChatGptTranslator TranslatorWith(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        TranslatorWith(req => Task.FromResult(responder(req)));

    private static ChatGptTranslator TranslatorWith(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var http = new HttpClient(new StubHandler(responder))
        {
            BaseAddress = new Uri("https://chatgpt.com/backend-api/codex/"),
        };
        return new ChatGptTranslator(http, NullLogger<ChatGptTranslator>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request);
    }
}
