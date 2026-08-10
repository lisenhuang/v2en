using System.Xml.Linq;
using v2en.Configuration;
using v2en.Data;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

public class FeedXmlWriterTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    [Fact]
    public void EntryLinksPointToTheConfiguredV2enPost()
    {
        var post = new Post
        {
            V2exId = 123456,
            SourceTagId = "tag:www.v2ex.com,2026-08-10:/t/123456",
            SourceUrl = "https://www.v2ex.com/t/123456",
            AuthorName = "tester",
            TitleZh = "测试标题",
            TitleEn = "Test title",
            ContentEnHtml = "<p>Translated content</p>",
            Published = DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
            Updated = DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
            Status = TranslationStatus.Translated,
        };

        var xml = FeedXmlWriter.Build(
            "https://v2en.aify.nz/",
            new SiteOptions(),
            new[] { post },
            DateTimeOffset.Parse("2026-08-10T00:00:00Z"));

        var document = XDocument.Parse(xml);
        var entry = document.Root!.Element(Atom + "entry")!;
        var link = entry.Elements(Atom + "link")
            .Single(element => (string?)element.Attribute("rel") == "alternate");

        Assert.Equal("https://v2en.aify.nz/t/123456", (string?)link.Attribute("href"));
    }
}
