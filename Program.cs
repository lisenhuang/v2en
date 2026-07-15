using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text;
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
builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection(GeminiOptions.Section));

// API keys (OpenRouter translation key + Gemini embedding key pool) are MANAGED IN THE ADMIN
// DASHBOARD and stored in the DB; they are seeded from config/env on first run. The app starts
// without them — translation and embedding simply pause (logged) until the admin sets them.

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

// ── V2exTopicClient: best-effort, no-token fetch of a topic's replies (legacy public JSON API). ──
builder.Services.AddHttpClient<V2exTopicClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<FeedOptions>>().Value;
    client.BaseAddress = new Uri("https://www.v2ex.com/");
    client.DefaultRequestHeaders.UserAgent.TryParseAdd(opts.UserAgent);
}).AddStandardResilienceHandler();

// ── OpenRouter clients: base address only — the API key is read per-request from the DB ──
// (No AddResilienceHandler for the translator — it has its own model-fallback + 429 handling.)
static void ConfigureOpenRouterClient(IServiceProvider sp, HttpClient client)
{
    var opts = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + '/');
}
builder.Services.AddHttpClient<OpenRouterTranslator>(ConfigureOpenRouterClient);
builder.Services.AddHttpClient<OpenRouterModelsService>(ConfigureOpenRouterClient);
builder.Services.AddHttpClient<OpenRouterAccountService>(ConfigureOpenRouterClient);

// ── ChatGPT (Codex) translation: OAuth (auth.openai.com) + the Codex Responses backend ──
// Tokens are obtained from /admin ("Sign in with ChatGPT" device-code flow, or a pasted auth.json)
// and stored in the DB; the translator sends them per-request. No key/secret in config.
builder.Services.AddHttpClient<ChatGptAuthService>(c => c.BaseAddress = new Uri("https://auth.openai.com/"));
builder.Services.AddHttpClient<ChatGptTranslator>(c => c.BaseAddress = new Uri("https://chatgpt.com/backend-api/codex/"));
builder.Services.AddSingleton<ChatGptModelsService>();

// ── Gemini clients (embeddings + chat + live model list). Keys are per-request. ──
const string GeminiBase = "https://generativelanguage.googleapis.com/";
builder.Services.AddHttpClient<GeminiModelsService>(c => c.BaseAddress = new Uri(GeminiBase));
builder.Services.AddHttpClient<GeminiEmbeddingService>(c => c.BaseAddress = new Uri(GeminiBase));
builder.Services.AddHttpClient<GeminiChatService>(c => c.BaseAddress = new Uri(GeminiBase));

// ── Application services ──────────────────────────────────────────────────────────
builder.Services.AddSingleton<HtmlSanitizerService>();
builder.Services.AddSingleton<GeminiKeyCursor>();
builder.Services.AddSingleton<VectorCache>();
builder.Services.AddScoped<RuntimeSettingsService>();
builder.Services.AddScoped<TranslationService>();
builder.Services.AddScoped<RetrievalService>();
builder.Services.AddScoped<PostDetailsService>();
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

// Let the admin's fetch() calls (ChatGPT device-code sign-in) send the antiforgery token as a header.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

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
    var rss = scope.ServiceProvider.GetRequiredService<RuntimeSettingsService>();
    var cfgRow = await rss.GetAsync();

    // Back-compat upgrade: an already-deployed site keeps its OpenRouter key (and any Gemini
    // seed) in config/env. On the first run after this release the DB row already EXISTS, so the
    // first-run seed above doesn't fire — copy the config keys into the row once so translation
    // (and embedding) keep working without the admin re-entering anything.
    var orOpts = scope.ServiceProvider.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    var gOpts = scope.ServiceProvider.GetRequiredService<IOptions<GeminiOptions>>().Value;
    var migrated = false;
    if (string.IsNullOrWhiteSpace(cfgRow.OpenRouterApiKey) && !string.IsNullOrWhiteSpace(orOpts.ApiKey))
    {
        cfgRow.OpenRouterApiKey = orOpts.ApiKey;
        migrated = true;
    }
    if (RuntimeSettingsService.ParseEmbedKeys(cfgRow).Count == 0 && gOpts.EmbedKeys.Count > 0)
    {
        cfgRow.GeminiEmbedKeysJson = RuntimeSettingsService.SerializeEmbedKeys(gOpts.EmbedKeys);
        migrated = true;
    }
    if (string.IsNullOrWhiteSpace(cfgRow.EmbeddingModel) && !string.IsNullOrWhiteSpace(gOpts.EmbeddingModel))
    {
        cfgRow.EmbeddingModel = gOpts.EmbeddingModel;
        migrated = true;
    }

    // The migration backfilled these NEW columns on the existing prod row with 0/"" (the SQL
    // default), not the intended C# defaults. Every one clamps to ≥1, so 0/empty unambiguously
    // means "never set" — normalize to safe defaults so embedding/chat work out of the box.
    if (cfgRow.EmbeddingDim <= 0) { cfgRow.EmbeddingDim = 768; migrated = true; }
    if (cfgRow.EmbedMaxPerTick <= 0) { cfgRow.EmbedMaxPerTick = 16; migrated = true; }
    if (cfgRow.EmbedMaxAttempts <= 0) { cfgRow.EmbedMaxAttempts = 5; migrated = true; }
    if (cfgRow.RetrievalTopK <= 0) { cfgRow.RetrievalTopK = 8; migrated = true; }
    if (cfgRow.ChatMaxContextPosts <= 0) { cfgRow.ChatMaxContextPosts = 8; migrated = true; }
    if (cfgRow.ChatRateLimitPerMinutePerIp <= 0) { cfgRow.ChatRateLimitPerMinutePerIp = 6; migrated = true; }
    if (string.IsNullOrWhiteSpace(cfgRow.ChatModel)) { cfgRow.ChatModel = "gemini-2.5-flash"; migrated = true; }

    if (migrated)
    {
        cfgRow.UpdatedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

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
    IOptions<FeedOptions> feedOpts,
    HttpContext ctx) =>
{
    var query = db.Posts
        .Where(p => p.Status == TranslationStatus.Translated);

    // Show every post published within the recent window (default 24h), with NO count cap.
    // A non-positive window disables the cutoff and emits all translated posts (legacy behavior).
    var windowHours = feedOpts.Value.RecentWindowHours;
    if (windowHours > 0)
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-windowHours);
        query = query.Where(p => p.Published >= cutoff);
    }

    var posts = await query
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

// ── Public "ask the feed" chat: embed the query, retrieve, and answer — all with the VISITOR's key ──
app.MapPost("/api/chat", async (
    ChatRequest req,
    RuntimeSettingsService settingsSvc,
    RetrievalService retrieval,
    GeminiChatService chat,
    AppDbContext db,
    IMemoryCache cache,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var cfg = await settingsSvc.GetAsync(ct);
    if (!cfg.EnableChat) return Results.NotFound();

    var question = (req.Question ?? "").Trim();
    if (question.Length == 0) return Results.BadRequest(new { error = "Please enter a question." });
    if (question.Length > 1000) question = question[..1000];
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "Add your Google AI Studio key to ask." });

    // English-only: short-circuit obvious non-English (CJK) questions before spending any quota.
    // (Other non-English scripts are caught by the model's system instruction.)
    if (LooksPredominantlyCjk(question))
        return Results.Ok(new { answer = "Please ask your question in English.", sources = Array.Empty<object>() });

    // Per-IP fixed-window rate limit (IP is trustworthy: UseForwardedHeaders is configured).
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var bucket = $"chat_rl:{ip}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
    var used = cache.Get<int>(bucket);
    if (used >= Math.Max(1, cfg.ChatRateLimitPerMinutePerIp))
        return Results.Json(new { error = "You're asking too fast — wait a minute and try again." }, statusCode: 429);
    cache.Set(bucket, used + 1, TimeSpan.FromMinutes(1));

    if (string.IsNullOrWhiteSpace(cfg.EmbeddingModel))
        return Results.Json(new { error = "Search isn't available right now." }, statusCode: 503);

    // Prior turns the browser replayed, normalized to a valid alternating transcript. Lets the model
    // (and retrieval) understand follow-ups like "is there more?" instead of treating them in isolation.
    var history = SanitizeChatHistory(req.History);

    var window = RetrievalService.ParseWindow(req.Window);
    // Embed the visitor's query with the VISITOR's own key — same model/dim as the stored post
    // vectors, so the query still lands in the same vector space (the key only authenticates/bills
    // the call, it doesn't change the embedding). The server-side key pool remains in use by the
    // ingestion job to embed posts; only this per-question query embedding is charged to the visitor.
    // For follow-ups, blend the recent user turns into the embedded query so a bare "is there more?"
    // still retrieves the posts the conversation is about.
    var retrievalQuery = BuildRetrievalQuery(history, question);
    var queryKeys = new[] { req.ApiKey! };
    var search = await retrieval.SearchAsync(retrievalQuery, cfg.EmbeddingModel, cfg.EmbeddingDim, window, cfg.RetrievalTopK, queryKeys, ct);
    if (search.Error is not null)
        return Results.Json(new { error = search.Error }, statusCode: 503);
    if (search.Hits.Count == 0)
        return Results.Ok(new { answer = "I couldn't find any matching posts in that time window yet.", sources = Array.Empty<object>() });

    var contextPosts = search.Hits.Take(Math.Max(1, cfg.ChatMaxContextPosts)).ToList();
    // Visitor-chosen chat model when supplied & well-formed, else the dashboard default.
    var chatModel = SanitizeModelId(req.Model) ?? cfg.ChatModel;
    var result = await chat.AnswerAsync(question, req.ApiKey!, chatModel, contextPosts, history, ct);

    // Dashboard log — NEVER the visitor's key.
    db.TranslationLogs.Add(new TranslationLog
    {
        Utc = DateTimeOffset.UtcNow,
        Level = result.Success ? LogSeverity.Info : LogSeverity.Warning,
        Event = result.Success ? "chat" : "chat_failed",
        Model = chatModel,
        HttpStatus = result.HttpStatus,
        Message = result.Success
            ? $"Answered a question ({contextPosts.Count} source(s), {window})."
            : $"Chat failed: {result.Error}",
        Detail = result.Success ? null : result.Detail,   // raw Google reason (never the visitor's key)
    });
    await db.SaveChangesAsync(ct);

    if (!result.Success)
    {
        var code = result.HttpStatus is int s && s is >= 400 and < 600 ? s : 502;
        return Results.Json(new { error = result.Error ?? "The AI request failed." }, statusCode: code);
    }

    return Results.Ok(new
    {
        answer = result.Answer,
        sources = result.Sources.Select(x => new { id = x.V2exId, title = x.Title, url = x.Url, published = x.Published }),
    });
});

// ── Chat model picker: list the visitor's OWN generation-capable Gemini models (never hardcoded) ──
app.MapPost("/api/chat/models", async (
    ChatModelsRequest req,
    RuntimeSettingsService settingsSvc,
    GeminiModelsService models,
    IMemoryCache cache,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var cfg = await settingsSvc.GetAsync(ct);
    if (!cfg.EnableChat) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "Add your Google AI Studio key to load models." });

    // Light per-IP fixed-window limit — the lookup hits Google and is cached, but guard abuse.
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var bucket = $"models_rl:{ip}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
    var used = cache.Get<int>(bucket);
    if (used >= 20)
        return Results.Json(new { error = "Too many requests — wait a minute and try again." }, statusCode: 429);
    cache.Set(bucket, used + 1, TimeSpan.FromMinutes(1));

    var lists = await models.GetModelsAsync(req.ApiKey, ct);
    if (lists.Chat.Count == 0)
        return Results.Json(new { error = "Couldn't load models — check your key." }, statusCode: 502);

    return Results.Ok(new
    {
        models = lists.Chat.Select(m => new { id = m.Id, displayName = m.DisplayName }),
        live = lists.Live.Select(m => new { id = m.Id, displayName = m.DisplayName }),
        @default = cfg.ChatModel,
    });
});

// ── Live voice grounding: retrieve posts for a tool-call from the /live session ──
// The live audio session runs entirely in the browser; when Gemini calls the search_v2ex_posts
// function it hits this endpoint, which runs the SAME embedding + vector search as /api/chat and
// returns matching posts as text for the model to answer from. Query embedding uses the visitor's
// own key (same as the text chat).
app.MapPost("/api/live/search", async (
    LiveSearchRequest req,
    RuntimeSettingsService settingsSvc,
    RetrievalService retrieval,
    IMemoryCache cache,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var cfg = await settingsSvc.GetAsync(ct);
    if (!cfg.EnableChat) return Results.NotFound();

    var question = (req.Question ?? "").Trim();
    if (question.Length == 0) return Results.Ok(new { posts = Array.Empty<object>() });
    if (question.Length > 1000) question = question[..1000];
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "Missing key." });

    // Per-IP fixed-window limit — a voice turn may search a few times, so allow more than text chat.
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var bucket = $"live_search_rl:{ip}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
    var used = cache.Get<int>(bucket);
    if (used >= Math.Max(10, cfg.ChatRateLimitPerMinutePerIp * 5))
        return Results.Json(new { error = "Too many requests — slow down." }, statusCode: 429);
    cache.Set(bucket, used + 1, TimeSpan.FromMinutes(1));

    if (string.IsNullOrWhiteSpace(cfg.EmbeddingModel))
        return Results.Ok(new { posts = Array.Empty<object>() });

    var window = RetrievalService.ParseWindow(req.Window);
    var search = await retrieval.SearchAsync(
        question, cfg.EmbeddingModel, cfg.EmbeddingDim, window, cfg.RetrievalTopK, new[] { req.ApiKey! }, ct);
    if (search.Error is not null)
        return Results.Ok(new { posts = Array.Empty<object>() });

    var posts = search.Hits.Take(Math.Max(1, cfg.ChatMaxContextPosts))
        .Select(h => new { id = h.V2exId, title = h.Title, url = h.SourceUrl, published = h.Published, snippet = h.Snippet });
    return Results.Ok(new { posts });
});

// ── /live voice tool: fetch ONE post's full body (from our mirror) + its replies (live from V2EX). ──
// Used by the get_post_details function call so the audio assistant can read the original post and the
// discussion. The shareable link returned is always our own /t/{id} page, never a v2ex.com URL.
app.MapPost("/api/live/post", async (
    LivePostRequest req,
    RuntimeSettingsService settingsSvc,
    PostDetailsService details,
    IMemoryCache cache,
    HttpContext ctx,
    CancellationToken ct) =>
{
    var cfg = await settingsSvc.GetAsync(ct);
    if (!cfg.EnableChat) return Results.NotFound();
    if (req.V2exId <= 0) return Results.BadRequest(new { error = "Missing post id." });

    // Per-IP fixed-window limit — a voice turn may open a couple of posts (mirrors the search limit).
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var bucket = $"live_post_rl:{ip}:{DateTimeOffset.UtcNow:yyyyMMddHHmm}";
    var used = cache.Get<int>(bucket);
    if (used >= Math.Max(10, cfg.ChatRateLimitPerMinutePerIp * 5))
        return Results.Json(new { error = "Too many requests — slow down." }, statusCode: 429);
    cache.Set(bucket, used + 1, TimeSpan.FromMinutes(1));

    var d = await details.GetAsync(req.V2exId, ct);
    if (!d.Found) return Results.Ok(new { found = false });
    return Results.Ok(new
    {
        found = true,
        id = d.V2exId,
        title = d.Title,
        localUrl = d.LocalUrl,
        published = d.Published,
        body = d.Body,
        repliesAvailable = d.RepliesAvailable,
        replies = d.Replies.Select(r => new { author = r.Author, text = r.Text, created = r.Created }),
    });
});

await app.RunAsync();
return 0;

// Heuristic: a question is "predominantly CJK" (Chinese/Japanese/Korean) when CJK letters are at
// least half of its letters — a strong signal it isn't English. Conservative on purpose so an
// English question that merely quotes a Chinese term still passes through to the model.
static bool LooksPredominantlyCjk(string s)
{
    int cjk = 0, letters = 0;
    foreach (var ch in s)
    {
        if (char.IsLetter(ch)) letters++;
        if (ch is (>= '一' and <= '鿿')   // CJK ideographs
              or (>= '぀' and <= 'ヿ')     // Hiragana + Katakana
              or (>= '가' and <= '힯'))    // Hangul
            cjk++;
    }
    return letters > 0 && cjk * 2 >= letters;
}

// The browser replays prior turns so follow-ups ("is there more?") keep context. Gemini requires the
// conversation to start with a "user" turn and to alternate user/model, so we normalize the untrusted
// client input into that exact shape: map roles, drop blanks, clip each message, keep only the most
// recent turns, and force strict alternation ending on a "model" turn (the live question — always a
// "user" turn — is appended after this). Bounds token cost and can't break the API contract.
static IReadOnlyList<ChatTurn> SanitizeChatHistory(IReadOnlyList<ChatMessage>? history)
{
    const int maxTurns = 12;        // ~6 Q&A pairs of carried context
    const int maxLenPerTurn = 4000; // clip any single replayed message

    if (history is null || history.Count == 0) return Array.Empty<ChatTurn>();

    // Normalize roles + text first, dropping anything empty.
    var mapped = new List<ChatTurn>(history.Count);
    foreach (var m in history)
    {
        var text = (m.Text ?? "").Trim();
        if (text.Length == 0) continue;
        if (text.Length > maxLenPerTurn) text = text[..maxLenPerTurn];
        var role = (m.Role ?? "").Trim().ToLowerInvariant() is "model" or "assistant" or "bot" ? "model" : "user";
        mapped.Add(new ChatTurn(role, text));
    }
    if (mapped.Count == 0) return Array.Empty<ChatTurn>();

    // Keep only the most recent turns.
    if (mapped.Count > maxTurns) mapped = mapped.GetRange(mapped.Count - maxTurns, maxTurns);

    // Force a valid, alternating transcript that starts with "user".
    var cleaned = new List<ChatTurn>(mapped.Count);
    var expect = "user";
    foreach (var t in mapped)
    {
        if (t.Role != expect) continue;                 // skip out-of-order turns
        cleaned.Add(t);
        expect = expect == "user" ? "model" : "user";
    }
    // The next thing appended is the live "user" question, so history must end on a "model" turn.
    if (cleaned.Count > 0 && cleaned[^1].Role == "user") cleaned.RemoveAt(cleaned.Count - 1);
    return cleaned;
}

// A bare follow-up like "is there more?" embeds to nothing useful, so the vector search would miss the
// posts the conversation is actually about. Prepend the recent USER turns to the current question to
// give retrieval the topical signal it needs. Clipped so the embedding query stays bounded.
static string BuildRetrievalQuery(IReadOnlyList<ChatTurn> history, string question)
{
    const int maxLen = 1000;
    if (history.Count == 0) return question;

    var sb = new StringBuilder();
    foreach (var t in history)
        if (t.Role == "user") sb.Append(t.Text).Append(' ');
    sb.Append(question);
    var q = sb.ToString().Trim();
    return q.Length > maxLen ? q[..maxLen] : q;
}

// A visitor-supplied model id is interpolated into the Gemini request path, so accept only a safe
// model-id shape (letters, digits, dot, hyphen). Anything else (or empty) → null, and the caller
// falls back to the dashboard's configured chat model. Prevents path/URL injection.
static string? SanitizeModelId(string? model)
{
    var m = model?.Trim();
    if (string.IsNullOrEmpty(m)) return null;
    foreach (var ch in m)
        if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')) return null;
    return m;
}

/// <summary>One earlier message the browser replays so follow-ups keep context. Role is "user" or
/// "model" (Gemini's vocabulary); anything else is normalized server-side.</summary>
record ChatMessage(string? Role, string? Text);

/// <summary>Public chat request body. ApiKey is the visitor's own Gemini key — used once, never stored.
/// Model is the visitor-chosen chat model (optional; falls back to the dashboard default).
/// History is the prior turns of this same chat (optional; older clients omit it and still work).</summary>
record ChatRequest(string? Question, string? ApiKey, string? Window, string? Model, IReadOnlyList<ChatMessage>? History);

/// <summary>Body for the public chat model-picker lookup: the visitor's own Gemini key.</summary>
record ChatModelsRequest(string? ApiKey);

/// <summary>Body for a /live voice tool-call: a search query + the visitor's own Gemini key.</summary>
record LiveSearchRequest(string? Question, string? ApiKey, string? Window);

/// <summary>Body for the /live "read this post" tool-call: the numeric V2EX post id. ApiKey is
/// accepted for symmetry with the other live calls but is not needed (no Gemini call is made).</summary>
record LivePostRequest(long V2exId, string? ApiKey);
