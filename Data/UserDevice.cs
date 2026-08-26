namespace App.Data;

/// <summary>
/// A client device this account has signed in on. Used for new-device email and
/// "remember this device" 2FA skip. Never stores encryption keys.
/// </summary>
public class UserDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string DeviceId { get; set; } = "";
    public string? Name { get; set; }
    public string? UserAgent { get; set; }

    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when a full sign-in completed on this device (code, password, or 2FA).</summary>
    public DateTime? TrustedAt { get; set; }

    public DateTime? TwoFactorTrustedUntil { get; set; }
}
