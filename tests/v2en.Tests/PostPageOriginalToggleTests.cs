using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Pages;
using v2en.Services;
using Xunit;

namespace v2en.Tests;

/// <summary>
/// The post detail page renders both languages and lets a button flip between them. Two bugs live at
/// the edges of that decision, and these pin both:
///
///   #22 — the toggle showed a blank page for a post with no original body.
///   #25 — the fix for #22 then hid the toggle on title-only posts, which on V2EX are ordinary posts
///         whose title IS the content, so their original title became unreachable.
///
/// The rule these lock in: offer the original whenever it says something the English view doesn't
/// (body OR title), and always render something on both sides so no flip can go blank.
/// </summary>
public class PostPageOriginalToggleTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;

    public PostPageOriginalToggleTests()
    {
        // Real SQLite, in memory: same provider as production, so the query in OnGetAsync is exercised
        // for real. The connection must stay open — closing it drops the database.
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<PostModel> RenderAsync(Post post)
    {
        _db.Posts.Add(post);
        await _db.SaveChangesAsync();

        var page = new PostModel(_db, new HtmlSanitizerService());
        await page.OnGetAsync(post.V2exId);
        return page;
    }

    private static Post Translated(long id, string titleZh, string contentZh, string? titleEn, string? contentEn) => new()
    {
        V2exId = id,
        TitleZh = titleZh,
        ContentZhHtml = contentZh,
        TitleEn = titleEn,
        ContentEnHtml = contentEn,
        Status = TranslationStatus.Translated,
        SourceUrl = $"https://www.v2ex.com/t/{id}",
        Published = DateTimeOffset.UnixEpoch,
        Updated = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task TitleOnlyPostStillOffersTheOriginal()
    {
        // #25: https://v2en.aify.nz/t/1233422 — a title, no body anywhere. The original title is the
        // only thing the post has, so hiding the toggle hides the whole original.
        var page = await RenderAsync(Translated(1233422, "求推荐一个好用的记账 App", "", "Looking for a good expense-tracking app", ""));

        Assert.True(page.HasOriginal);            // the button renders
        Assert.True(page.HasOriginalTitle);
        Assert.False(page.HasOriginalBody);       // ...but there is no body to show
        Assert.True(page.IsTitleOnly);
        Assert.Equal("求推荐一个好用的记账 App", page.OriginalTitle);
        Assert.Equal("Looking for a good expense-tracking app", page.TitleEnDisplay);
    }

    [Fact]
    public async Task PostWithBodyKeepsWorking()
    {
        var page = await RenderAsync(Translated(
            1233436, "标题", "<p>正文</p>", "Title", "<p>Body</p>"));

        Assert.True(page.HasOriginal);
        Assert.True(page.HasOriginalBody);
        Assert.Contains("正文", page.OriginalHtml);
        Assert.False(page.IsTitleOnly);
    }

    [Fact]
    public async Task IdenticalTitleAndNoBodyOffersNoToggle()
    {
        // Translation passed the title through unchanged (TranslationService falls back to TitleZh when
        // the model returns no title). Flipping would show the exact same page, so don't offer it.
        var page = await RenderAsync(Translated(1233423, "SwiftUI 4.0", "", "SwiftUI 4.0", ""));

        Assert.False(page.HasOriginal);
        Assert.False(page.HasOriginalTitle);
        Assert.True(page.IsTitleOnly);
    }

    [Fact]
    public async Task UntranslatedTitleFallsBackWithoutClaimingAnOriginal()
    {
        // Older rows can carry a null TitleEn: the heading falls back to the Chinese title, which is
        // then already on screen — so there is still nothing extra to flip to.
        var page = await RenderAsync(Translated(1233424, "只有中文标题", "", null, null));

        Assert.Equal("只有中文标题", page.TitleEnDisplay);
        Assert.False(page.HasOriginal);
        Assert.True(page.IsTitleOnly);
    }

    [Fact]
    public async Task MarkupOnlyBodyCountsAsNoBody()
    {
        // A wrapper with nothing in it is a non-empty string but an empty post — rendering it is the
        // blank original page from #22. The title still justifies the toggle.
        var page = await RenderAsync(Translated(
            1233425, "标题", "<div class=\"topic_content\"></div>", "Title", "<p>&nbsp;</p>"));

        Assert.False(page.HasOriginalBody);
        Assert.False(page.HasTranslatedBody);
        Assert.True(page.IsTitleOnly);
        Assert.True(page.HasOriginal);            // via the title, not the body
    }

    [Fact]
    public async Task ImageOnlyBodyIsARealBody()
    {
        // No text at all, but the post is the image. It must not be mistaken for a title-only post.
        var page = await RenderAsync(Translated(
            1233427, "看图", "<img src=\"https://i.v2ex.co/x.png\" class=\"embedded_image\">", "Look", null));

        Assert.True(page.HasOriginalBody);
        Assert.Contains("embedded_image", page.OriginalHtml);
        Assert.False(page.IsTitleOnly);
    }

    [Fact]
    public async Task MissingTranslationOfARealBodyIsNotTitleOnly()
    {
        // There IS an original body, the English side just doesn't have it yet — a translation gap, not
        // a bodyless post, and the page tells the reader so.
        var page = await RenderAsync(Translated(1233426, "标题", "<p>正文</p>", "Title", null));

        Assert.True(page.HasOriginalBody);
        Assert.False(page.IsTitleOnly);
        Assert.True(page.HasOriginal);
    }
}
