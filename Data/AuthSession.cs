namespace App.Data;

/// <summary>
/// Server-side session row. The cookie holds the raw session id; this table stores only a hash
/// so a leaked database cannot replay cookies. Revoking a row signs that device out without
/// rotating Data Protection keys or touching encryption keys.
/// </summary>
public class AuthSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 hex of the secret session id in the cookie.</summary>
    public string SessionHash { get; set; } = "";

    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? UserAgent { get; set; }
    public string? Ip { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public DateTime? TwoFactorTrustedUntil { get; set; }
}
