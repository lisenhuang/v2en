using v2en.Utilities;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// <see cref="HtmlText.IsBlank"/> decides whether a post actually has a body. A plain length check is
/// not enough — V2EX serves empty wrapper markup for title-only topics, and the post page renders that
/// as a blank panel (#22/#25). It must also not call an image-only post empty.
/// </summary>
public class HtmlTextIsBlankTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<div></div>")]
    [InlineData("<div class=\"topic_content\"></div>")]
    [InlineData("<p></p>\n<p>  </p>")]
    [InlineData("<p>&nbsp;</p>")]
    [InlineData("<!-- nothing here -->")]
    public void BlankHtmlIsBlank(string? html) => Assert.True(HtmlText.IsBlank(html));

    [Theory]
    [InlineData("<p>正文</p>")]
    [InlineData("hello")]
    [InlineData("<img src=\"https://i.v2ex.co/x.png\">")]            // media without text is still a body
    [InlineData("<div><video src=\"x.mp4\"></video></div>")]
    [InlineData("<pre><code>dotnet build</code></pre>")]
    public void ContentfulHtmlIsNotBlank(string html) => Assert.False(HtmlText.IsBlank(html));
}
