namespace App.Core.Auth;

/// <summary>
/// Stable per-install device id (browser localStorage or MAUI settings).
/// </summary>
public interface IClientDeviceId
{
    Task<string> GetOrCreateAsync();
    Task<string?> GetNameAsync();
}
