namespace ChatfishApp.Data;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? MagicLinkToken { get; set; }
    public DateTime? MagicLinkExpiresAt { get; set; }

    /// <summary>
    /// Per-user random AES-256 key (base64, 32 raw bytes) used by WASM/MAUI clients for IndexedDB
    /// encryption and live sync payloads. Stored as plaintext in SQLite; created once per user.
    /// Fetched over HTTPS+cookie auth so all devices for the same email share one stable key.
    /// </summary>
    public string? LocalEncryptionKey { get; set; }

    /// <summary>
    /// Optional PBKDF2 password hash for email+password login (and notebook unlock).
    /// Null when the user has not set a password (magic-link / login code only).
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Future admin flag. Defaults to false for all existing and new users.
    /// </summary>
    public bool IsAdmin { get; set; }
}
