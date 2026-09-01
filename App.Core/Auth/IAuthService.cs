namespace App.Core.Auth;

public interface IAuthService
{
    string? Email { get; }
    string? UserId { get; }
    string? LocalEncryptionKeyB64 { get; }
    bool IsAuthenticated { get; }
    /// <summary>True when the signed-in account has set a login password.</summary>
    bool HasPassword { get; }
    /// <summary>True when password login requires a second factor.</summary>
    bool TwoFactorEnabled { get; }
    bool HasTwoFactorPhone { get; }
    string? TwoFactorPhoneMasked { get; }
    /// <summary>True when the host can send Twilio SMS codes.</summary>
    bool SmsTwoFactorAvailable { get; }
    bool HasRecoveryCodes { get; }
    /// <summary>One-time recovery codes from the last 2FA enable. Shown once, then cleared.</summary>
    IReadOnlyList<string>? LastRecoveryCodes { get; }
    string ServerBaseUrl { get; }
    string SyncHubUrl { get; }

    event Action? OnChanged;

    Task LoadAsync();
    Task<(bool Success, string? Error)> RequestLoginCodeAsync(string email);
    Task<(bool Success, string? Error)> VerifyLoginCodeAsync(string email, string code);
    /// <summary>
    /// Forgot-password: consume an emailed login code, clear the password (and 2FA), then sign in.
    /// </summary>
    Task<(bool Success, string? Error)> ResetPasswordAsync(string email, string code);
    Task<AuthLoginResult> LoginWithPasswordAsync(string email, string password);
    Task<AuthLoginResult> VerifyTwoFactorAsync(string challengeId, string code, string method);
    Task<(bool Success, string? Error)> SendTwoFactorAsync(string challengeId, string method);
    Task<(bool Success, string? Error)> SetPasswordAsync(string password, string confirmPassword, string? currentPassword = null);
    Task<(bool Success, string? Error)> SetTwoFactorEnabledAsync(bool enabled, string? currentPassword = null);
    Task<(bool Success, string? Error)> EnrollTwoFactorPhoneAsync(string phone);
    Task<(bool Success, string? Error)> ConfirmTwoFactorPhoneAsync(string phone, string code);
    Task<(bool Success, string? Error)> RemoveTwoFactorPhoneAsync(string? currentPassword = null);
    Task<(bool Success, string? Error)> VerifyPasswordAsync(string password);
    Task<IReadOnlyList<AuthSessionInfo>> GetSessionsAsync();
    Task<(bool Success, string? Error)> RevokeSessionAsync(string sessionId);
    Task<(bool Success, string? Error)> RevokeOtherSessionsAsync();
    Task SignOutAsync();
    Task<string> GetOrCreateHistoryEncryptionKeyAsync();
    void SignOutLocal();
}