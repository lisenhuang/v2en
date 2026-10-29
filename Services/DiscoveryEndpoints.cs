using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Data;
using v2en.Utilities;

namespace v2en.Services;

public static class DiscoveryEndpoints
{
    public const int SitemapPageSize = 1000;
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
    private static IResult Xml(XElement root) => Results.Content(new XDocument(root).ToString(), "application/xml; charset=utf-8");
    private static XElement Url(string url, DateTimeOffset? modified = null) => new(Ns + "url",
        new XElement(Ns + "loc", url), modified is null ? null : new XElement(Ns + "lastmod", modified.Value.ToString("O")));

    public static void MapDiscovery(this WebApplication app)
    {
        app.MapGet("/robots.txt", (IOptions<SiteOptions> options) => Results.Text(
            $"User-agent: *\nAllow: /\nDisallow: /admin\nDisallow: /api/\nDisallow: /healthz\n\nSitemap: {SiteMetadata.Origin(options.Value)}/sitemap.xml\n"));

        app.MapGet("/sitemap.xml", async (AppDbContext db, IOptions<SiteOptions> options, HttpContext ctx, CancellationToken ct) =>
        {
            var origin = SiteMetadata.Origin(options.Value);
            var count = await db.Posts.CountAsync(p => p.Status == TranslationStatus.Translated, ct);
            var paths = new List<string> { "/sitemaps/pages.xml" };
            for (var i = 1; i <= (count + SitemapPageSize - 1) / SitemapPageSize; i++) paths.Add($"/sitemaps/posts/{i}.xml");
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            return Xml(new XElement(Ns + "sitemapindex", paths.Select(path => new XElement(Ns + "sitemap", new XElement(Ns + "loc", origin + path)))));
        });
        app.MapGet("/sitemaps/pages.xml", (IOptions<SiteOptions> options) => Xml(new XElement(Ns + "urlset",
            new[] { "/", "/about", "/ask", "/live" }.Select(path => Url(SiteMetadata.Origin(options.Value) + path)))));
        app.MapGet("/sitemaps/posts/{page:int}.xml", async (int page, AppDbContext db, IOptions<SiteOptions> options, HttpContext ctx, CancellationToken ct) =>
        {
            if (page < 1 || page > int.MaxValue / SitemapPageSize) return Results.NotFound();
            var posts = await db.Posts.AsNoTracking().Where(p => p.Status == TranslationStatus.Translated)
                .OrderBy(p => p.V2exId).Skip((page - 1) * SitemapPageSize).Take(SitemapPageSize)
                .Select(p => new { p.V2exId, p.Published, p.Updated, p.TranslatedAt }).ToListAsync(ct);
            if (posts.Count == 0) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=300";
            var origin = SiteMetadata.Origin(options.Value);
            return Xml(new XElement(Ns + "urlset", posts.Select(p => Url(SiteMetadata.PostUrl(origin, p.V2exId),
                new[] { p.Published, p.Updated, p.TranslatedAt ?? p.Published }.Max()))));
        });
        app.MapGet("/api/public/posts", async (string? q, int? limit, long? before, AppDbContext db, IOptions<SiteOptions> options, HttpContext ctx, CancellationToken ct) =>
        {
            q = (q ?? "").Trim();
            if (q.Length > 100 || limit is < 1 or > 20 || before is <= 0)
                return Results.BadRequest(new { error = "Use a query of up to 100 characters, limit 1–20, and a positive before ID." });
            var query = db.Posts.AsNoTracking().Where(p => p.Status == TranslationStatus.Translated);
            if (before.HasValue) query = query.Where(p => p.V2exId < before.Value);
            if (q.Length > 0)
            {
                var term = q.ToLowerInvariant();
                query = query.Where(p => (p.TitleEn != null && p.TitleEn.ToLower().Contains(term)) || p.TitleZh.ToLower().Contains(term) || (p.ContentEnHtml != null && p.ContentEnHtml.ToLower().Contains(term)));
            }
            var take = limit ?? 10;
            var posts = await query.OrderByDescending(p => p.V2exId).Take(take + 1).ToListAsync(ct);
            var origin = SiteMetadata.Origin(options.Value);
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok(new { posts = posts.Take(take).Select(p => PublicPost(p, origin, false)), nextBefore = posts.Count > take ? (long?)posts[take - 1].V2exId : null });
        }).RequireRateLimiting("discovery");
        app.MapGet("/api/public/posts/{id:long}", async (long id, AppDbContext db, IOptions<SiteOptions> options, HttpContext ctx, CancellationToken ct) =>
        {
            var post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.V2exId == id && p.Status == TranslationStatus.Translated, ct);
            if (post is null) return Results.NotFound(new { error = "Translated post not found." });
            ctx.Response.Headers.CacheControl = "public, max-age=60";
            return Results.Ok(PublicPost(post, SiteMetadata.Origin(options.Value), true));
        }).RequireRateLimiting("discovery");
    }

    // Explicit projection: never return EF entities, provider errors, credentials or original private state.
    internal static object PublicPost(Post post, string origin, bool full) => new
    {
        id = post.V2exId, title = string.IsNullOrWhiteSpace(post.TitleEn) ? post.TitleZh : post.TitleEn,
        url = SiteMetadata.PostUrl(origin, post.V2exId), sourceUrl = post.SourceUrl, author = post.AuthorName,
        publishedAt = post.Published, modifiedAt = SiteMetadata.Modified(post), language = "en", machineTranslated = true,
        text = full ? HtmlText.Plain(post.ContentEnHtml, 30000) : HtmlText.Preview(post.ContentEnHtml, 240),
        textTruncated = full && HtmlText.Plain(post.ContentEnHtml, 30001).Length > 30000
    };
}
