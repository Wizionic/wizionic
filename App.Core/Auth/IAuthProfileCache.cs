namespace App.Core.Auth;

/// <summary>
/// Optional platform store for <see cref="AuthProfileSnapshot"/>.
/// MAUI uses SQLite (DPAPI on Windows). WASM uses localStorage. The Blazor Server host does not register one.
/// </summary>
public interface IAuthProfileCache
{
    Task SaveAsync(AuthProfileSnapshot snapshot);
    Task<AuthProfileSnapshot?> LoadAsync();
    Task ClearAsync();
}
