using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using v2en.Data;

namespace v2en.Services;

/// <summary>Started device-code login: show the user the code + URL, then poll.</summary>
public record DeviceAuthStart(string DeviceAuthId, string UserCode, string VerificationUri, int IntervalSeconds);

/// <summary>One poll result. <see cref="Status"/> is "pending", "done", or "error".</summary>
public record DevicePollResult(string Status, string? Error = null);

/// <summary>Connected-account summary for the dashboard (never exposes the tokens).</summary>
public record ChatGptStatus(bool Connected, string? AccountId, string? Plan, string? Label, DateTimeOffset? ExpiresUtc);

/// <summary>
/// "Sign in with ChatGPT" for the admin: implements the same OAuth the Codex CLI uses so the admin can
/// translate with their ChatGPT plan's models. Because the deployed admin has no localhost callback,
/// we use OpenAI's <b>device-code</b> flow (the CLI's <c>--device-auth</c> mode): the admin opens a URL
/// on any device, enters a short code, and we poll for the tokens. The admin can also paste a Codex
/// <c>auth.json</c> directly. Tokens live in <see cref="RuntimeSettings"/> and are refreshed on demand.
///
/// Client id / endpoints match the public Codex CLI so the same "device code" ChatGPT setting applies.
/// </summary>
public class ChatGptAuthService
{
    // Public Codex CLI OAuth client — the ChatGPT backend recognises it for subscription-model access.
    private const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string DeviceUserCodeUrl = "api/accounts/deviceauth/usercode";
    private const string DeviceTokenUrl = "api/accounts/deviceauth/token";
    private const string OAuthTokenUrl = "oauth/token";
    private const string DeviceRedirectUri = "https://auth.openai.com/deviceauth/callback";
    private const string VerificationUri = "https://auth.openai.com/codex/device";

    private readonly HttpClient _http;
    private readonly RuntimeSettingsService _settings;
    private readonly AppDbContext _db;
    private readonly ILogger<ChatGptAuthService> _logger;

    public ChatGptAuthService(HttpClient http, RuntimeSettingsService settings, AppDbContext db, ILogger<ChatGptAuthService> logger)
    {
        _http = http;
        _settings = settings;
        _db = db;
        _logger = logger;
    }

    public async Task<ChatGptStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cfg = await _settings.GetAsync(ct);
        var connected = !string.IsNullOrWhiteSpace(cfg.ChatGptAccessToken) || !string.IsNullOrWhiteSpace(cfg.ChatGptRefreshToken);
        return new ChatGptStatus(
            connected,
            NullIfEmpty(cfg.ChatGptAccountId),
            NullIfEmpty(cfg.ChatGptPlanType),
            NullIfEmpty(cfg.ChatGptAccountLabel),
            cfg.ChatGptAccessTokenExpiresUtc);
    }

    // ── Device-code flow ─────────────────────────────────────────────────────────────────────────

    /// <summary>Request a user code. Throws with a helpful message when device-code login is disabled.</summary>
    public async Task<DeviceAuthStart> StartDeviceAuthAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceUserCodeUrl)
        {
            Content = JsonContent.Create(new { client_id = ClientId }),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeBodyAsync(resp, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "Device-code login isn't enabled for this ChatGPT account. Turn on " +
                    "\"Enable device code authentication for Codex\" in ChatGPT → Settings → Security " +
                    "(chatgpt.com/codex/settings), or use \"Paste Codex auth.json\" below.");
            throw new InvalidOperationException($"Couldn't start ChatGPT sign-in (HTTP {(int)resp.StatusCode}). {Clip(body)}");
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var deviceAuthId = GetString(root, "device_auth_id") ?? "";
        var userCode = GetString(root, "user_code") ?? GetString(root, "usercode") ?? "";
        var interval = 5;
        if (root.TryGetProperty("interval", out var iv))
        {
            if (iv.ValueKind == JsonValueKind.Number) interval = iv.GetInt32();
            else if (iv.ValueKind == JsonValueKind.String && int.TryParse(iv.GetString(), out var pi)) interval = pi;
        }
        if (string.IsNullOrEmpty(deviceAuthId) || string.IsNullOrEmpty(userCode))
            throw new InvalidOperationException("ChatGPT sign-in response was missing the device code.");

        return new DeviceAuthStart(deviceAuthId, userCode, VerificationUri, Math.Clamp(interval, 1, 30));
    }

    /// <summary>
    /// Poll once for the device-code authorization. Returns "pending" until the user approves, then
    /// exchanges the returned authorization code for tokens, persists them, and returns "done".
    /// </summary>
    public async Task<DevicePollResult> PollDeviceAuthAsync(string deviceAuthId, string userCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceAuthId) || string.IsNullOrWhiteSpace(userCode))
            return new DevicePollResult("error", "Missing device code — start the sign-in again.");

        using var req = new HttpRequestMessage(HttpMethod.Post, DeviceTokenUrl)
        {
            Content = JsonContent.Create(new { device_auth_id = deviceAuthId, user_code = userCode }),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, ct);

        // Still waiting for the user to approve on the other device.
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return new DevicePollResult("pending");

        if (!resp.IsSuccessStatusCode)
            return new DevicePollResult("error", $"Sign-in failed (HTTP {(int)resp.StatusCode}). {Clip(await SafeBodyAsync(resp, ct))}");

        string authorizationCode, codeVerifier;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            authorizationCode = GetString(doc.RootElement, "authorization_code") ?? "";
            codeVerifier = GetString(doc.RootElement, "code_verifier") ?? "";
        }
        catch (Exception ex)
        {
            return new DevicePollResult("error", $"Couldn't read the sign-in response: {ex.Message}");
        }
        if (string.IsNullOrEmpty(authorizationCode) || string.IsNullOrEmpty(codeVerifier))
            return new DevicePollResult("pending"); // some backends return 200 with an empty payload while pending

        try
        {
            var tokens = await ExchangeCodeAsync(authorizationCode, codeVerifier, ct);
            await PersistTokensAsync(tokens, ct);
            return new DevicePollResult("done");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT token exchange failed.");
            return new DevicePollResult("error", $"Token exchange failed: {ex.Message}");
        }
    }

    // ── Paste auth.json (fallback when device-code is disabled) ────────────────────────────────────

    /// <summary>
    /// Import a Codex <c>auth.json</c> (produced by <c>codex login</c>). Accepts either the file's
    /// <c>{ "tokens": { access_token, refresh_token, id_token, account_id } }</c> shape or a flat object.
    /// </summary>
    public async Task ImportAuthJsonAsync(string json, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Paste the contents of your Codex auth.json.");

        string access, refresh, id, accountId;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // The real file nests under "tokens"; also accept a flat object for convenience.
            var t = root.TryGetProperty("tokens", out var tokensEl) && tokensEl.ValueKind == JsonValueKind.Object ? tokensEl : root;
            access = GetString(t, "access_token") ?? "";
            refresh = GetString(t, "refresh_token") ?? "";
            id = GetString(t, "id_token") ?? "";
            accountId = GetString(t, "account_id") ?? GetString(root, "account_id") ?? "";
        }
        catch (JsonException ex) { throw new InvalidOperationException($"That isn't valid JSON: {ex.Message}"); }

        if (string.IsNullOrEmpty(access) && string.IsNullOrEmpty(refresh))
            throw new InvalidOperationException("Couldn't find access_token/refresh_token in that JSON.");

        var tokenSet = new TokenSet(access, refresh, id, ExpiryFromAccessToken(access));
        await PersistTokensAsync(tokenSet, ct, fallbackAccountId: accountId);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var cfg = await _settings.GetAsync(ct);
        cfg.ChatGptAccessToken = "";
        cfg.ChatGptRefreshToken = "";
        cfg.ChatGptIdToken = "";
        cfg.ChatGptAccountId = "";
        cfg.ChatGptPlanType = "";
        cfg.ChatGptAccountLabel = "";
        cfg.ChatGptAccessTokenExpiresUtc = null;
        cfg.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Token access (used by the translator) ──────────────────────────────────────────────────────

    /// <summary>
    /// Return a currently-valid access token + account id, refreshing via the refresh token when the
    /// current one is expired (or about to). Returns null when no account is connected or refresh fails.
    /// </summary>
    public async Task<(string AccessToken, string AccountId)?> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var cfg = await _settings.GetAsync(ct);
        var hasAccess = !string.IsNullOrWhiteSpace(cfg.ChatGptAccessToken);
        var fresh = cfg.ChatGptAccessTokenExpiresUtc is { } exp && exp > DateTimeOffset.UtcNow.AddMinutes(5);
        if (hasAccess && fresh)
            return (cfg.ChatGptAccessToken, cfg.ChatGptAccountId);

        // Need a refresh.
        if (string.IsNullOrWhiteSpace(cfg.ChatGptRefreshToken))
            return hasAccess ? (cfg.ChatGptAccessToken, cfg.ChatGptAccountId) : null; // no refresh token: use what we have

        try
        {
            var tokens = await RefreshAsync(cfg.ChatGptRefreshToken, ct);
            await PersistTokensAsync(tokens, ct, fallbackAccountId: cfg.ChatGptAccountId);
            return (tokens.AccessToken, string.IsNullOrEmpty(cfg.ChatGptAccountId) ? ExtractAccountId(tokens.AccessToken) ?? "" : cfg.ChatGptAccountId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ChatGPT token refresh failed.");
            return hasAccess ? (cfg.ChatGptAccessToken, cfg.ChatGptAccountId) : null;
        }
    }

    // ── OAuth token endpoints ──────────────────────────────────────────────────────────────────────

    private sealed record TokenSet(string AccessToken, string RefreshToken, string IdToken, DateTimeOffset? ExpiresUtc);

    private async Task<TokenSet> ExchangeCodeAsync(string code, string codeVerifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["code_verifier"] = codeVerifier,
            ["redirect_uri"] = DeviceRedirectUri,
        };
        return await PostTokenAsync(form, ct);
    }

    private async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = ClientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = "openid profile email",
        };
        return await PostTokenAsync(form, ct);
    }

    private async Task<TokenSet> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, OAuthTokenUrl) { Content = new FormUrlEncodedContent(form) };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Clip(body)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = GetString(root, "access_token") ?? "";
        var refresh = GetString(root, "refresh_token") ?? form.GetValueOrDefault("refresh_token") ?? "";
        var id = GetString(root, "id_token") ?? "";
        DateTimeOffset? expires = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UtcNow.AddSeconds(ei.GetInt64())
            : ExpiryFromAccessToken(access);
        if (string.IsNullOrEmpty(access))
            throw new InvalidOperationException("Token response had no access_token.");
        return new TokenSet(access, refresh, id, expires);
    }

    private async Task PersistTokensAsync(TokenSet tokens, CancellationToken ct, string? fallbackAccountId = null)
    {
        var cfg = await _settings.GetAsync(ct);
        cfg.ChatGptAccessToken = tokens.AccessToken;
        if (!string.IsNullOrEmpty(tokens.RefreshToken)) cfg.ChatGptRefreshToken = tokens.RefreshToken;
        if (!string.IsNullOrEmpty(tokens.IdToken)) cfg.ChatGptIdToken = tokens.IdToken;
        cfg.ChatGptAccessTokenExpiresUtc = tokens.ExpiresUtc;

        var accountId = ExtractAccountId(tokens.AccessToken) ?? fallbackAccountId;
        if (!string.IsNullOrWhiteSpace(accountId)) cfg.ChatGptAccountId = accountId!;

        var plan = ExtractClaimString(tokens.AccessToken, "https://api.openai.com/auth", "chatgpt_plan_type");
        if (!string.IsNullOrWhiteSpace(plan)) cfg.ChatGptPlanType = plan!;

        var label = ExtractTopClaim(tokens.IdToken, "email") ?? ExtractTopClaim(tokens.AccessToken, "email");
        if (!string.IsNullOrWhiteSpace(label)) cfg.ChatGptAccountLabel = label!;

        cfg.UpdatedUtc = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── JWT helpers (decode without verifying — we already trust tokens from the OAuth flow) ────────

    public static string? ExtractAccountId(string? jwt) =>
        ExtractClaimString(jwt, "https://api.openai.com/auth", "chatgpt_account_id");

    private static DateTimeOffset? ExpiryFromAccessToken(string? jwt)
    {
        var payload = DecodePayload(jwt);
        if (payload is { } p && p.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
        return null;
    }

    private static string? ExtractClaimString(string? jwt, string containerClaim, string field)
    {
        var payload = DecodePayload(jwt);
        if (payload is { } p && p.TryGetProperty(containerClaim, out var container) && container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static string? ExtractTopClaim(string? jwt, string field)
    {
        var payload = DecodePayload(jwt);
        if (payload is { } p && p.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    private static JsonElement? DecodePayload(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length <= 500 ? s : s[..500]);

    private static async Task<string> SafeBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }
}
