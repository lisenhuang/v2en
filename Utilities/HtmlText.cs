using System.Net;
using System.Text.RegularExpressions;

namespace v2en.Utilities;

public static partial class HtmlText
{
    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<\s*(img|picture|video|audio|source|embed|object|svg|iframe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediaTagRegex();

    /// <summary>
    /// True when this HTML would render as nothing — no visible text and no embedded media.
    ///
    /// A non-empty string is not the same as a non-empty body: V2EX ships wrapper markup such as
    /// <c>&lt;div&gt;&lt;/div&gt;</c> for topics that are only a title, and a plain length check calls
    /// that a body and then renders a blank page. An image-only body has no text but IS a body, so
    /// media tags short-circuit to "not blank".
    /// </summary>
    public static bool IsBlank(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        if (MediaTagRegex().IsMatch(html)) return false;
        return Plain(html).Length == 0;
    }

    /// <summary>Plain-text preview from HTML, for list cards / meta descriptions.</summary>
    public static string Preview(string? html, int max = 180)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = TagRegex().Replace(html, " ");
        text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return text.Length <= max ? text : string.Concat(text.AsSpan(0, max).TrimEnd(), "…");
    }

    /// <summary>
    /// Full plain text from HTML (tags stripped, entities decoded, whitespace collapsed), hard-capped
    /// at <paramref name="max"/> characters with no ellipsis suffix. For feeding a post body to the AI.
    /// </summary>
    public static string Plain(string? html, int max = 8000)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var text = TagRegex().Replace(html, " ");
        text = WhitespaceRegex().Replace(WebUtility.HtmlDecode(text), " ").Trim();
        return text.Length <= max ? text : text[..max];
    }
}
