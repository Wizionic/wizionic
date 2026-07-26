namespace ChatfishApp.Core.Auth;

public interface IAuthService
{
    string? Email { get; }
    string? UserId { get; }
    string? LocalEncryptionKeyB64 { get; }
    bool IsAuthenticated { get; }
    /// <summary>True when the signed-in account has set a login password.</summary>
    bool HasPassword { get; }
    string ServerBaseUrl { get; }
    string SyncHubUrl { get; }

    event Action? OnChanged;

    Task LoadAsync();
    Task<(bool Success, string? Error)> RequestLoginCodeAsync(string email);
    Task<(bool Success, string? Error)> VerifyLoginCodeAsync(string email, string code);
    Task<(bool Success, string? Error)> LoginWithPasswordAsync(string email, string password);
    Task<(bool Success, string? Error)> SetPasswordAsync(string password, string confirmPassword, string? currentPassword = null);
    Task<(bool Success, string? Error)> VerifyPasswordAsync(string password);
    Task SignOutAsync();
    Task<string> GetOrCreateHistoryEncryptionKeyAsync();
    void SignOutLocal();
}