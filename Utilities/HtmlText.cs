using System.Net;
using System.Text.RegularExpressions;

namespace v2en.Utilities;

public static partial class HtmlText
{
    [GeneratedRegex("<.*?>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

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
