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

    // ── helpers ──

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
