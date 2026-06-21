namespace ChatfishApp.Core.Auth;

public interface IAuthService
{
    string? Email { get; }
    string? UserId { get; }
    string? LocalEncryptionKeyB64 { get; }
    bool IsAuthenticated { get; }
    string ServerBaseUrl { get; }
    string SyncHubUrl { get; }

    event Action? OnChanged;

    Task LoadAsync();
    Task<(bool Success, string? Error)> RequestLoginCodeAsync(string email);
    Task<(bool Success, string? Error)> VerifyLoginCodeAsync(string email, string code);
    Task SignOutAsync();
    Task<string> GetOrCreateHistoryEncryptionKeyAsync();
    void SignOutLocal();
}