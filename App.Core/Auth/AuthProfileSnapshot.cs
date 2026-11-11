namespace App.Core.Auth;

/// <summary>
/// Last successful sign-in for one login server, kept on this device so local
/// notes and history open when that server is unreachable.
/// </summary>
public sealed class AuthProfileSnapshot
{
    public string ServerBaseUrl { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public string LocalEncryptionKeyB64 { get; set; } = "";
    public bool HasPassword { get; set; }
    public bool TwoFactorEnabled { get; set; }
    public bool HasTwoFactorPhone { get; set; }
    public string? TwoFactorPhoneMasked { get; set; }
    public bool SmsTwoFactorAvailable { get; set; }
    public bool HasRecoveryCodes { get; set; }
}
