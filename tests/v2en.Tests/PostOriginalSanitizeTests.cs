using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// The post detail page renders the pre-translation body so readers can flip to the original. That
/// body is stored exactly as it arrived in the V2EX Atom feed — only the TRANSLATED body is sanitized
/// at store time (TranslationService) — so the page must sanitize it on the way out. These pin the
/// behaviour the page depends on: raw user HTML from a third-party feed never reaches the browser
/// with script in it, while ordinary post formatting survives intact.
/// </summary>
public class PostOriginalSanitizeTests
{
    private readonly HtmlSanitizerService _sanitizer = new();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<div onclick=\"alert(1)\">hi</div>")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>")]
    public void StripsScriptFromOriginalBody(string html)
    {
        var clean = _sanitizer.Sanitize(html);

        Assert.DoesNotContain("<script", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", clean, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", clean, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsOrdinaryPostFormatting()
    {
        const string html =
            "<p>看看这个 <a href=\"https://v2ex.com/t/1\" target=\"_blank\" rel=\"nofollow\">链接</a></p>" +
            "<pre><code>dotnet build</code></pre>" +
            "<img src=\"https://i.v2ex.co/x.png\" class=\"embedded_image\">";

        var clean = _sanitizer.Sanitize(html);

        Assert.Contains("链接", clean);                    // CJK text survives
        Assert.Contains("https://v2ex.com/t/1", clean);   // links survive
        Assert.Contains("dotnet build", clean);           // code blocks survive
        Assert.Contains("embedded_image", clean);         // the class V2EX images rely on survives
    }

    [Fact]
    public void EmptyOriginalStaysEmpty()
    {
        // Drives HasOriginal on the page: a post stored without a body offers no toggle at all.
        Assert.True(string.IsNullOrWhiteSpace(_sanitizer.Sanitize("")));
        Assert.True(string.IsNullOrWhiteSpace(_sanitizer.Sanitize(null)));
    }
}
