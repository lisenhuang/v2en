using System.Text.Json;
using v2en.Configuration;
using v2en.Data;

namespace v2en.Utilities;

public static class SiteMetadata
{
    public const string Description = "Read V2EX technology and developer discussions in English. Browse machine-translated posts with original sources, search the archive, or ask questions about the feed.";
    public static string Origin(SiteOptions options) =>
        Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == "https" || uri.Scheme == "http")
            ? uri.GetLeftPart(UriPartial.Authority) : "https://v2en.9l.nz";

    public static string PostUrl(string origin, long id) => $"{origin}/t/{id}";
    public static DateTimeOffset Modified(Post post) =>
        new[] { post.Published, post.Updated, post.TranslatedAt ?? post.Published }.Max();

    // The default JSON encoder escapes HTML delimiters, including closing script tags.
    public static string ArticleJson(Post post, string origin) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["@context"] = "https://schema.org", ["@type"] = "Article",
        ["@id"] = PostUrl(origin, post.V2exId) + "#article",
        ["url"] = PostUrl(origin, post.V2exId), ["mainEntityOfPage"] = PostUrl(origin, post.V2exId),
        ["headline"] = string.IsNullOrWhiteSpace(post.TitleEn) ? post.TitleZh : post.TitleEn,
        ["description"] = HtmlText.Preview(post.ContentEnHtml, 160),
        ["inLanguage"] = "en", ["datePublished"] = post.Published.ToString("O"),
        ["dateModified"] = Modified(post).ToString("O"),
        ["author"] = new Dictionary<string, object?> { ["@type"] = "Person", ["name"] = post.AuthorName },
        ["isBasedOn"] = post.SourceUrl,
        ["translationOfWork"] = new Dictionary<string, object?> {
            ["@type"] = "CreativeWork", ["url"] = post.SourceUrl, ["name"] = post.TitleZh },
        ["publisher"] = new Dictionary<string, object?> { ["@type"] = "Organization", ["name"] = "V2EN", ["url"] = origin }
    });
}
