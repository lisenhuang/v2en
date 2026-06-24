using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;

namespace v2en.Pages.Admin;

public class AccountModel : PageModel
{
    private readonly AppDbContext _db;

    public AccountModel(AppDbContext db) => _db = db;

    public AdminUser? CurrentUser { get; private set; }

    [BindProperty] public string Current { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string Confirm { get; set; } = "";
    public string? Error { get; set; }
    public bool Saved { get; set; }

    private Task<AdminUser?> LoadUserAsync(CancellationToken ct) =>
        _db.AdminUsers.FirstOrDefaultAsync(u => u.Username == User.Identity!.Name, ct);

    public async Task OnGetAsync(CancellationToken ct) => CurrentUser = await LoadUserAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        CurrentUser = await LoadUserAsync(ct);
        if (CurrentUser is null)
        {
            Error = "Signed-in user not found.";
            return Page();
        }
        if (!PasswordHasher.Verify(Current, CurrentUser.PasswordHash))
        {
            Error = "Current password is incorrect.";
            return Page();
        }
        if (NewPassword.Length < 8)
        {
            Error = "New password must be at least 8 characters.";
            return Page();
        }
        if (NewPassword != Confirm)
        {
            Error = "New passwords don't match.";
            return Page();
        }

        CurrentUser.PasswordHash = PasswordHasher.Hash(NewPassword);
        await _db.SaveChangesAsync(ct);

        Current = NewPassword = Confirm = "";
        Saved = true;
        return Page();
    }
}
