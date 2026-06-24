using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using v2en.Data;
using v2en.Services;

namespace v2en.Pages.Admin;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly AppDbContext _db;

    public LoginModel(AppDbContext db) => _db = db;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; set; }

    public IActionResult OnGet()
        => User.Identity?.IsAuthenticated == true ? RedirectToPage("/Admin/Index") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var user = await _db.AdminUsers.FirstOrDefaultAsync(u => u.Username == Username, ct);
        if (user is null || !PasswordHasher.Verify(Password, user.PasswordHash))
        {
            Error = "Invalid username or password.";
            return Page();
        }

        user.LastLoginUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return Redirect(ReturnUrl);
        return RedirectToPage("/Admin/Index");
    }
}
