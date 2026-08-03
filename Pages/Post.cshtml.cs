using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;

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

    /// <summary>Whether to offer the original at all — false for a post stored without a body.</summary>
    public bool HasOriginal => OriginalHtml.Length > 0;

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
        OriginalHtml = string.IsNullOrWhiteSpace(original) ? "" : original;

        return Page();
    }
}
