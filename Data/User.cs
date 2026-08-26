namespace App.Data;

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
    /// When true, password login requires a second factor (email code and/or SMS)
    /// before a session cookie is issued. Does not apply to notebook/chat/gallery unlock.
    /// </summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// Verified E.164 phone for Twilio Verify SMS. Null when SMS is not enrolled.
    /// </summary>
    public string? TwoFactorPhoneE164 { get; set; }

    /// <summary>
    /// SHA-256 (hex) of the one-shot password-verified 2FA challenge id. Cleared after sign-in or expiry.
    /// </summary>
    public string? TwoFactorChallengeHash { get; set; }

    public DateTime? TwoFactorChallengeExpiresAt { get; set; }

    /// <summary>
    /// Future admin flag. Defaults to false for all existing and new users.
    /// </summary>
    public bool IsAdmin { get; set; }

    /// <summary>UTC until which password/code attempts are rejected. Null when not locked.</summary>
    public DateTime? LockoutUntil { get; set; }

    public int FailedAuthCount { get; set; }

    /// <summary>JSON array of SHA-256 hex hashes of remaining 2FA recovery codes.</summary>
    public string? RecoveryCodesJson { get; set; }

    public ICollection<AuthSession> Sessions { get; set; } = new List<AuthSession>();
    public ICollection<UserDevice> Devices { get; set; } = new List<UserDevice>();
}
