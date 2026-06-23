using System.Globalization;
using System.Text;
using System.Xml;
using v2en.Configuration;
using v2en.Data;

namespace v2en.Services;

/// <summary>
/// Builds our English Atom 1.0 feed, reproducing V2EX's exact structure:
/// same XML declaration, default Atom namespace, element set, ordering, attributes,
/// and CDATA-wrapped HTML content. Per-entry &lt;link&gt; points to the original v2ex URL;
/// feed-level self/alternate point to our own domain.
/// </summary>
public static class FeedXmlWriter
{
    private const string AtomNs = "http://www.w3.org/2005/Atom";
    private const string XmlNs = "http://www.w3.org/XML/1998/namespace";
    private const string ContentBase = "https://www.v2ex.com/";

    public static string Build(string baseUrl, SiteOptions site, IReadOnlyList<Post> posts, DateTimeOffset feedUpdated)
    {
        baseUrl = baseUrl.TrimEnd('/');

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
            NewLineChars = "\n",
        };

        var sw = new Utf8StringWriter();
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument(); // <?xml version="1.0" encoding="utf-8"?>
            w.WriteStartElement("feed", AtomNs);

            WriteSimple(w, "title", site.FeedTitle);
            WriteSimple(w, "subtitle", site.FeedSubtitle);
            WriteLink(w, "alternate", "text/html", $"{baseUrl}/");
            WriteLink(w, "self", "application/atom+xml", $"{baseUrl}/index.xml");
            WriteSimple(w, "id", $"{baseUrl}/");
            WriteSimple(w, "updated", FormatDate(feedUpdated));
            WriteSimple(w, "rights", site.FeedRights);

            foreach (var p in posts)
            {
                w.WriteStartElement("entry", AtomNs);

                WriteSimple(w, "title", p.TitleEn ?? p.TitleZh);
                WriteLink(w, "alternate", "text/html", p.SourceUrl);     // original v2ex URL
                WriteSimple(w, "id", p.SourceTagId);                     // original tag: id
                WriteSimple(w, "published", FormatDate(p.Published));
                WriteSimple(w, "updated", FormatDate(p.Updated));

                w.WriteStartElement("author", AtomNs);
                WriteSimple(w, "name", p.AuthorName);
                if (!string.IsNullOrEmpty(p.AuthorUri))
                    WriteSimple(w, "uri", p.AuthorUri);
                w.WriteEndElement(); // author

                w.WriteStartElement("content", AtomNs);
                w.WriteAttributeString("type", "html");
                w.WriteAttributeString("xml", "base", XmlNs, ContentBase);
                w.WriteAttributeString("xml", "lang", XmlNs, "en");
                w.WriteCData(p.ContentEnHtml ?? string.Empty);
                w.WriteEndElement(); // content

                w.WriteEndElement(); // entry
            }

            w.WriteEndElement(); // feed
            w.WriteEndDocument();
        }

        return sw.ToString();
    }

    private static void WriteSimple(XmlWriter w, string name, string value)
    {
        w.WriteStartElement(name, AtomNs);
        w.WriteString(value);
        w.WriteEndElement();
    }

    private static void WriteLink(XmlWriter w, string rel, string type, string href)
    {
        w.WriteStartElement("link", AtomNs);
        w.WriteAttributeString("rel", rel);
        w.WriteAttributeString("type", type);
        w.WriteAttributeString("href", href);
        w.WriteEndElement();
    }

    private static string FormatDate(DateTimeOffset dto) =>
        dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>Forces the XML declaration to say encoding="utf-8" (a plain StringWriter reports utf-16).</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
