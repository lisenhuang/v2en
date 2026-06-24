using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using v2en.Configuration;
using v2en.Data;
using v2en.Services;
using v2en.Workers;

var builder = WebApplication.CreateBuilder(args);

// ── Options ──────────────────────────────────────────────────────────────────────
builder.Services.Configure<FeedOptions>(builder.Configuration.GetSection(FeedOptions.Section));
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.Section));
builder.Services.Configure<TranslationOptions>(builder.Configuration.GetSection(TranslationOptions.Section));
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));

// ── Required: OpenRouter API key (fail fast with clear setup instructions) ───────
var orKey = builder.Configuration[$"{OpenRouterOptions.Section}:ApiKey"];
if (string.IsNullOrWhiteSpace(orKey))
{
    using var logFactory = LoggerFactory.Create(b => b.AddConsole());
    logFactory.CreateLogger("Startup").LogCritical(
        "\n\n" +
        "  ╔══════════════════════════════════════════════════════════════════╗\n" +
        "  ║           OpenRouter API key is not configured                  ║\n" +
        "  ╠══════════════════════════════════════════════════════════════════╣\n" +
        "  ║  DEV — add it via user-secrets (never put it in source):        ║\n" +
        "  ║    dotnet user-secrets set \"OpenRouter:ApiKey\" \"sk-or-...\"       ║\n" +
        "  ║                                                                  ║\n" +
        "  ║  DOCKER / PROD — set the environment variable:                  ║\n" +
        "  ║    OpenRouter__ApiKey=sk-or-...                                  ║\n" +
        "  ║                                                                  ║\n" +
        "  ║  Get a free key at: https://openrouter.ai/keys                  ║\n" +
        "  ╚══════════════════════════════════════════════════════════════════╝\n");
    return 1;
}

// ── SQLite + EF Core (WAL set once at startup; busy_timeout per-connection) ──────
builder.Services.AddSingleton<BusyTimeoutInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, opts) =>
    opts.UseSqlite(
            builder.Configuration.GetConnectionString("Default")
            ?? "Data Source=/data/v2en.db")
        .AddInterceptors(sp.GetRequiredService<BusyTimeoutInterceptor>()));

// ── V2exFeedClient: standard resilience is fine (retrying a public feed is safe) ─
builder.Services.AddHttpClient<V2exFeedClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<FeedOptions>>().Value;
    client.DefaultRequestHeaders.UserAgent.TryParseAdd(opts.UserAgent);
}).AddStandardResilienceHandler();

// ── OpenRouterTranslator: custom pipeline — NEVER retry 429 (burns daily quota) ──
builder.Services.AddHttpClient<OpenRouterTranslator>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
    if (!string.IsNullOrWhiteSpace(opts.Referer))
        client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", opts.Referer);
    if (!string.IsNullOrWhiteSpace(opts.Title))
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", opts.Title);
});
// No AddResilienceHandler for OpenRouterTranslator — OpenRouterTranslator has its own
// model-fallback chain and 429 handling; HTTP-level retries would burn the daily quota.

// ── OpenRouter dashboard helpers: live :free model list + account/quota snapshot ──
static void ConfigureOpenRouterClient(IServiceProvider sp, HttpClient client)
{
    var opts = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
}
builder.Services.AddHttpClient<OpenRouterModelsService>(ConfigureOpenRouterClient);
builder.Services.AddHttpClient<OpenRouterAccountService>(ConfigureOpenRouterClient);

// ── Application services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<HtmlSanitizerService>();
builder.Services.AddScoped<RuntimeSettingsService>();
builder.Services.AddScoped<TranslationService>();
builder.Services.AddHostedService<FeedWorker>();
builder.Services.AddMemoryCache();

// ── Admin auth: cookie-based, credentials in the DB (AdminUser) ──────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.LogoutPath = "/admin/logout";
        options.AccessDeniedPath = "/admin/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Cookie.Name = "v2en_admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

// ── Infrastructure ────────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();
builder.Services.AddRazorPages(options =>
{
    // Everything under /Admin requires login, except the login page itself.
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
});

// ═════════════════════════════════════════════════════════════════════════════════
var app = builder.Build();
// ═════════════════════════════════════════════════════════════════════════════════

// Trust X-Forwarded-* from cloudflared (which connects from 127.0.0.1).
var fwdOpts = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
fwdOpts.KnownIPNetworks.Clear();
fwdOpts.KnownProxies.Clear();
app.UseForwardedHeaders(fwdOpts);

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// ── Run EF migrations, set WAL mode, seed runtime settings + initial admin ───────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    // journal_mode=WAL persists in the DB file — only needs to be set once ever,
    // but calling it on every startup is idempotent and harmless.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

    // Seed the runtime-settings row from appsettings on first run (DB is source of truth after).
    await scope.ServiceProvider.GetRequiredService<RuntimeSettingsService>().GetAsync();

    // ── Seed / enforce the admin account ─────────────────────────────────────────
    // Contract:
    //  • If Admin:Password is configured, that account is ENFORCED on every startup
    //    (created if missing, password re-synced if changed) so a known login always
    //    works. Leave it empty to manage the password only via the dashboard.
    //  • If Admin:Password is empty and no admin exists yet, one is seeded with a
    //    random password that is logged once.
    var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var adminUser = builder.Configuration["Admin:Username"];
    if (string.IsNullOrWhiteSpace(adminUser)) adminUser = "admin";
    var adminPass = builder.Configuration["Admin:Password"];

    if (!string.IsNullOrWhiteSpace(adminPass))
    {
        var user = await db.AdminUsers.FirstOrDefaultAsync(u => u.Username == adminUser);
        if (user is null)
        {
            db.AdminUsers.Add(new AdminUser
            {
                Username = adminUser,
                PasswordHash = PasswordHasher.Hash(adminPass),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            startupLog.LogInformation("Seeded admin account '{User}' from configuration.", adminUser);
        }
        else if (!PasswordHasher.Verify(adminPass, user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(adminPass);
            await db.SaveChangesAsync();
            startupLog.LogInformation("Admin '{User}' password re-synced from configuration.", adminUser);
        }
    }
    else if (!await db.AdminUsers.AnyAsync())
    {
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        db.AdminUsers.Add(new AdminUser
        {
            Username = adminUser,
            PasswordHash = PasswordHasher.Hash(password),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        startupLog.LogWarning(
            "\n  ┌──────────────────────────────────────────────────────────────┐\n" +
            "  │  Seeded admin account — CHANGE THE PASSWORD after first login │\n" +
            "  │    username: {User}\n" +
            "  │    password: {Password}\n" +
            "  │  Set Admin__Username / Admin__Password env vars to control it │\n" +
            "  └──────────────────────────────────────────────────────────────┘",
            adminUser, password);
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────────
app.MapHealthChecks("/healthz");
app.MapStaticAssets();
app.MapRazorPages();

// English Atom 1.0 feed — same structure as https://www.v2ex.com/index.xml
// Entry <link> points to original V2EX URLs; feed self/alternate points to our domain.
app.MapGet("/index.xml", async (
    AppDbContext db,
    IOptions<SiteOptions> siteOpts,
    HttpContext ctx) =>
{
    var posts = await db.Posts
        .Where(p => p.Status == TranslationStatus.Translated)
        .OrderByDescending(p => p.Published)
        .AsNoTracking()
        .ToListAsync();

    var feedUpdated = posts.Count > 0
        ? posts.Max(p => p.Updated)
        : DateTimeOffset.UtcNow;

    var site = siteOpts.Value;
    // Fall back to the request's own origin when Site:BaseUrl is not yet configured.
    var baseUrl = string.IsNullOrWhiteSpace(site.BaseUrl)
        ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
        : site.BaseUrl.TrimEnd('/');

    var xml = FeedXmlWriter.Build(baseUrl, site, posts, feedUpdated);

    ctx.Response.Headers.CacheControl = "public, max-age=300";
    return Results.Content(xml, "application/atom+xml;charset=UTF-8");
});

await app.RunAsync();
return 0;
