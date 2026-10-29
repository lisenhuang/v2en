using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using v2en.Configuration;
using v2en.Data;
using v2en.Services;
using v2en.Utilities;
using Xunit;

namespace v2en.Tests;

public class DiscoveryTests
{
    [Fact]
    public void ArticleJsonEscapesScriptContentAndKeepsOriginalAttribution()
    {
        var post = new Post { V2exId = 12, TitleEn = "</script><script>alert(1)</script>", AuthorName = "Author", SourceUrl = "https://www.v2ex.com/t/12", TranslatedAt = DateTimeOffset.UtcNow };
        var json = SiteMetadata.ArticleJson(post, "https://v2en.9l.nz");
        Assert.DoesNotContain("</script>", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("https://v2en.9l.nz/t/12", document.RootElement.GetProperty("url").GetString());
        Assert.Equal(post.SourceUrl, document.RootElement.GetProperty("isBasedOn").GetString());
        Assert.Equal(post.TitleEn, document.RootElement.GetProperty("headline").GetString());
        Assert.Equal(post.TranslatedAt, SiteMetadata.Modified(post));
    }

    [Fact]
    public async Task PublicDiscoveryPaginatesAndNeverExposesUntranslatedOrPrivateFields()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.Configure<SiteOptions>(o => o.BaseUrl = "https://v2en.9l.nz");
        builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
        builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("discovery", x => { x.PermitLimit = 100; x.Window = TimeSpan.FromMinutes(1); }));
        await using var app = builder.Build();
        app.UseRouting(); app.UseRateLimiter(); app.MapDiscovery();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Posts.AddRange(Enumerable.Range(1, 1001).Select(id => new Post {
                V2exId = id, TitleEn = "Topic " + id, ContentEnHtml = "<p>English &amp; content</p>",
                Status = TranslationStatus.Translated, SourceUrl = $"https://www.v2ex.com/t/{id}",
                LastError = "PRIVATE ERROR", TranslationModel = "PRIVATE PROVIDER" }));
            db.Posts.Add(new Post { V2exId = 9999, Status = TranslationStatus.Failed, TitleZh = "Unpublished" });
            await db.SaveChangesAsync();
        }
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var index = XDocument.Parse(await client.GetStringAsync("/sitemap.xml"));
            XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
            Assert.Equal(3, index.Root!.Elements(ns + "sitemap").Count());
            var page = XDocument.Parse(await client.GetStringAsync("/sitemaps/posts/2.xml"));
            Assert.Single(page.Root!.Elements(ns + "url"));
            Assert.Contains("https://v2en.9l.nz/t/1001", page.ToString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sitemaps/posts/0.xml")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/sitemaps/posts/3.xml")).StatusCode);
            var body = await client.GetStringAsync("/api/public/posts?limit=2");
            Assert.DoesNotContain("PRIVATE", body);
            using var result = JsonDocument.Parse(body);
            Assert.Equal(1001, result.RootElement.GetProperty("posts")[0].GetProperty("id").GetInt64());
            Assert.Equal(1000, result.RootElement.GetProperty("nextBefore").GetInt64());
            using var next = JsonDocument.Parse(await client.GetStringAsync("/api/public/posts?limit=2&before=1000"));
            Assert.Equal(999, next.RootElement.GetProperty("posts")[0].GetProperty("id").GetInt64());
            using var detail = JsonDocument.Parse(await client.GetStringAsync("/api/public/posts/1"));
            Assert.Equal("English & content", detail.RootElement.GetProperty("text").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/posts/9999")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/public/posts?limit=1000")).StatusCode);
            using var search = JsonDocument.Parse(await client.GetStringAsync("/api/public/posts?q=Topic%201001"));
            Assert.Single(search.RootElement.GetProperty("posts").EnumerateArray());
            var robots = await client.GetStringAsync("/robots.txt");
            Assert.Contains("Sitemap: https://v2en.9l.nz/sitemap.xml", robots);
        }
        finally { await app.StopAsync(); }
    }
}
