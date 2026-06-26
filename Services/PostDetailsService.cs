using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Utilities;

namespace v2en.Services;

public record PostDetails(
    long V2exId,
    string Title,
    string LocalUrl,
    DateTimeOffset Published,
    string Body,
    IReadOnlyList<V2exReply> Replies,
    bool Found,
    bool RepliesAvailable);

/// <summary>
/// Assembles the full content of a single post for the AI to read on demand: the complete
/// (translated-when-available) body from OUR database plus the discussion replies fetched live from
/// V2EX. The shareable link is always our own mirror page (/t/{V2exId}) — never a v2ex.com URL.
/// Replies are returned in their original Chinese; callers instruct the model to answer in English.
/// </summary>
public class PostDetailsService
{
    private const int MaxBodyChars = 8000;

    private readonly AppDbContext _db;
    private readonly V2exTopicClient _topics;

    public PostDetailsService(AppDbContext db, V2exTopicClient topics)
    {
        _db = db;
        _topics = topics;
    }

    public async Task<PostDetails> GetAsync(long v2exId, CancellationToken ct)
    {
        var post = await _db.Posts.AsNoTracking()
            .Where(p => p.V2exId == v2exId)
            .Select(p => new { p.V2exId, p.TitleEn, p.TitleZh, p.ContentEnHtml, p.ContentZhHtml, p.Published })
            .FirstOrDefaultAsync(ct);

        if (post is null)
            return new PostDetails(v2exId, "", $"/t/{v2exId}", default, "", Array.Empty<V2exReply>(), false, false);

        var title = string.IsNullOrWhiteSpace(post.TitleEn) ? post.TitleZh : post.TitleEn!;
        var bodyHtml = string.IsNullOrWhiteSpace(post.ContentEnHtml) ? post.ContentZhHtml : post.ContentEnHtml!;
        var body = HtmlText.Plain(bodyHtml, MaxBodyChars);

        var replies = await _topics.GetRepliesAsync(v2exId, ct);
        return new PostDetails(post.V2exId, title, $"/t/{post.V2exId}", post.Published, body, replies, true, replies.Count > 0);
    }
}
