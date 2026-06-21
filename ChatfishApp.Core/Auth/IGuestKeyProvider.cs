namespace ChatfishApp.Core.Auth;

/// <summary>
/// Supplies a device-local guest encryption key when the user is not authenticated.
/// </summary>
public interface IGuestKeyProvider
{
    Task<string> GetOrCreateGuestKeyAsync();
}