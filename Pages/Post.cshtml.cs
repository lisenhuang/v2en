using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;

namespace v2en.Pages;

public class PostModel : PageModel
{
    private readonly AppDbContext _db;

    public PostModel(AppDbContext db) => _db = db;

    public Post Post { get; private set; } = default!;

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var post = await _db.Posts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.V2exId == id && p.Status == TranslationStatus.Translated);

        if (post is null)
            return NotFound();

        Post = post;
        return Page();
    }
}
