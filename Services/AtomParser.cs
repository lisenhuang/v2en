using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace v2en.Services;

public record ParsedEntry(
    long V2exId,
    string SourceTagId,
    string SourceUrl,
    string Title,
    string ContentHtml,
    string AuthorName,
    string AuthorUri,
    DateTimeOffset Published,
    DateTimeOffset Updated);

public record ParsedFeed(DateTimeOffset? Updated, IReadOnlyList<ParsedEntry> Entries);

/// <summary>Parses the V2EX Atom 1.0 feed into strongly-typed entries.</summary>
public static partial class AtomParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    [GeneratedRegex(@"/t/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PostIdRegex();

    public static ParsedFeed Parse(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var feed = doc.Root ?? throw new FormatException("Feed has no root element.");

        var feedUpdated = ParseDate(feed.Element(Atom + "updated")?.Value);

        var entries = new List<ParsedEntry>();
        foreach (var e in feed.Elements(Atom + "entry"))
        {
            var sourceTagId = e.Element(Atom + "id")?.Value?.Trim() ?? "";
            var alternate = AlternateHref(e);

            // Numeric post id: prefer the <id> (tag:...:/t/NNNN), fall back to the link href.
            var v2exId = ExtractId(sourceTagId) ?? ExtractId(alternate);
            if (v2exId is null)
                continue; // not a post entry we can key on; skip

            var author = e.Element(Atom + "author");
            entries.Add(new ParsedEntry(
                V2exId: v2exId.Value,
                SourceTagId: sourceTagId,
                SourceUrl: alternate,
                Title: (e.Element(Atom + "title")?.Value ?? "").Trim(),
                ContentHtml: (e.Element(Atom + "content")?.Value ?? "").Trim(),
                AuthorName: (author?.Element(Atom + "name")?.Value ?? "").Trim(),
                AuthorUri: (author?.Element(Atom + "uri")?.Value ?? "").Trim(),
                Published: ParseDate(e.Element(Atom + "published")?.Value) ?? default,
                Updated: ParseDate(e.Element(Atom + "updated")?.Value) ?? default));
        }

        return new ParsedFeed(feedUpdated, entries);
    }

    private static string AlternateHref(XElement entry)
    {
        foreach (var link in entry.Elements(Atom + "link"))
        {
            var rel = (string?)link.Attribute("rel");
            if (rel is null || rel == "alternate")
                return ((string?)link.Attribute("href") ?? "").Trim();
        }
        return "";
    }

    private static long? ExtractId(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = PostIdRegex().Match(s);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    private static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(
            s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto)
            ? dto
            : null;
    }
}
