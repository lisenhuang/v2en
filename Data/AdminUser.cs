namespace v2en.Data;

/// <summary>
/// A dashboard admin account. Credentials live in the DB (you can manage rows directly).
/// The password is stored as a PBKDF2 hash — never plaintext. An initial account is seeded
/// on first startup; the password can be changed from the dashboard.
/// </summary>
public class AdminUser
{
    public int Id { get; set; }

    public string Username { get; set; } = "";

    /// <summary>PBKDF2 hash, format "iterations.saltBase64.hashBase64". Set via PasswordHasher.</summary>
    public string PasswordHash { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastLoginUtc { get; set; }
}
