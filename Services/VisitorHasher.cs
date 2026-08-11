using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace v2en.Services;

/// <summary>
/// Turns a visitor's IP address into the only per-visitor identifier this app ever stores.
///
/// HOW IT WORKS (and why it is safe to keep):
///   hash = HMAC-SHA256(key: serverSalt, message: "yyyy-MM-dd|ip|user-agent")  → first 16 bytes, hex
///
///   • One-way. SHA-256 cannot be inverted, so a stored hash cannot be turned back into an IP.
///   • Keyed. The salt is a 32-byte random secret generated on first startup and kept only in the
///     server's own database, never rendered in the UI or returned by any API. Without it, an
///     attacker who obtained the analytics table could not brute-force the (small) IPv4 space.
///   • Date-scoped. The UTC date is mixed in, so the same person gets a DIFFERENT hash tomorrow.
///     That is what makes "unique visitors" a same-day metric and makes long-term tracking of an
///     individual across days impossible by construction.
///
/// This is the standard "rotating salted hash" approach used by privacy-first analytics; the raw IP
/// exists only for the microseconds it takes to compute the hash and is never written anywhere.
/// </summary>
public static class VisitorHasher
{
    /// <summary>Bytes of HMAC output kept — 128 bits, ample against collisions at any realistic traffic.</summary>
    private const int HashBytes = 16;

    /// <summary>Generates a fresh random salt (base64). Called once, when the settings row has none.</summary>
    public static string NewSalt() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The visitor id for this request. Returns an empty string when there is no usable client
    /// address (then the row is still recorded as a page view — it just has no visitor identity).
    /// </summary>
    /// <param name="ip">The client address. Never stored; used only as HMAC input.</param>
    /// <param name="userAgent">Mixed in so two devices behind one NAT are more likely to count separately.</param>
    /// <param name="salt">The server secret from <c>RuntimeSettings.AnalyticsSalt</c>.</param>
    /// <param name="utcNow">Supplies the UTC date the hash is scoped to.</param>
    public static string Compute(IPAddress? ip, string? userAgent, string salt, DateTimeOffset utcNow)
    {
        if (ip is null || string.IsNullOrEmpty(salt)) return "";

        var message = string.Create(null, stackalloc char[256],
            $"{utcNow.UtcDateTime:yyyy-MM-dd}|{ip}|{userAgent}");

        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(KeyBytes(salt), Encoding.UTF8.GetBytes(message), hash);
        return Convert.ToHexStringLower(hash[..HashBytes]);
    }

    /// <summary>
    /// The salt is stored base64 but must keep working if it was ever set to a plain string by hand,
    /// so a non-base64 value is used as raw UTF-8 rather than rejected.
    /// </summary>
    private static byte[] KeyBytes(string salt)
    {
        Span<byte> buffer = stackalloc byte[64];
        return Convert.TryFromBase64String(salt, buffer, out var written)
            ? buffer[..written].ToArray()
            : Encoding.UTF8.GetBytes(salt);
    }
}
