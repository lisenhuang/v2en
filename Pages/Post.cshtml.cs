using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;
using v2en.Utilities;

namespace v2en.Pages;

public class PostModel : PageModel
{
    private readonly AppDbContext _db;
    private readonly HtmlSanitizerService _sanitizer;

    public PostModel(AppDbContext db, HtmlSanitizerService sanitizer)
    {
        _db = db;
        _sanitizer = sanitizer;
    }

    public Post Post { get; private set; } = default!;

    /// <summary>
    /// The pre-translation body, sanitized, or "" when there is nothing worth offering.
    ///
    /// Sanitized HERE rather than trusted from the DB: only <c>ContentEnHtml</c> is cleaned at store
    /// time (TranslationService), because until now the original was never rendered as HTML — the chat
    /// path flattens it with HtmlText.Plain and the feed only ever emits the translation. This page is
    /// the first place raw V2EX user HTML would reach a browser, and rows written by every earlier
    /// version are already in the database unsanitized, so cleaning at render time is what actually
    /// covers them. Cheap: one pass over one post body, only on the detail page.
    /// </summary>
    public string OriginalHtml { get; private set; } = "";

    /// <summary>The heading shown by default: the translation, falling back to the original title.</summary>
    public string TitleEnDisplay =>
        string.IsNullOrWhiteSpace(Post.TitleEn) ? Post.TitleZh : Post.TitleEn!;

    /// <summary>
    /// The heading shown in original mode. Falls back to the English one so the toggle can never leave
    /// the page without a title (the bug in #22).
    /// </summary>
    public string OriginalTitle =>
        string.IsNullOrWhiteSpace(Post.TitleZh) ? TitleEnDisplay : Post.TitleZh;

    /// <summary>Whether the original has a body to render — false for a post stored without one.</summary>
    public bool HasOriginalBody => OriginalHtml.Length > 0;

    /// <summary>Whether the translated body has anything to render.</summary>
    public bool HasTranslatedBody => !HtmlText.IsBlank(Post.ContentEnHtml);

    /// <summary>
    /// Whether the original title says something the English heading doesn't. An identical title means
    /// the translator passed it through unchanged (or fell back to it), so flipping shows nothing new.
    /// </summary>
    public bool HasOriginalTitle =>
        !string.IsNullOrWhiteSpace(Post.TitleZh) &&
        !string.Equals(Post.TitleZh.Trim(), TitleEnDisplay.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Whether to offer the original at all. A title-only post — very common on V2EX, where the title
    /// IS the post — still has an original title worth showing, so the body alone must not decide this
    /// (#25). The toggle disappears only when the original would be identical to what's already shown.
    /// </summary>
    public bool HasOriginal => HasOriginalBody || HasOriginalTitle;

    /// <summary>
    /// True when the post has no body in EITHER language: nothing is missing, the title is the post.
    /// Distinguishes that from a real translation gap, which is worth telling the reader about.
    /// </summary>
    public bool IsTitleOnly => !HasOriginalBody && !HasTranslatedBody;

    // The page always renders on the English translation; switching to the original is a purely
    // client-side toggle (see Post.cshtml / site.js). Any query string is ignored — there is no
    // server-side language mode, so a bookmarked/shared link always opens on English.
    public async Task<IActionResult> OnGetAsync(long id)
    {
        var post = await _db.Posts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.V2exId == id && p.Status == TranslationStatus.Translated);

        if (post is null)
            return NotFound();

        Post = post;

        var original = _sanitizer.Sanitize(post.ContentZhHtml);
        OriginalHtml = HtmlText.IsBlank(original) ? "" : original;

        return Page();
    }
}
