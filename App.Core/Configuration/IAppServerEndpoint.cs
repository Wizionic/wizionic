namespace App.Core.Configuration;

/// <summary>
/// Mutable login-server endpoint for MAUI. WASM/host use a fixed same-origin implementation.
/// </summary>
public interface IAppServerEndpoint
{
    string BaseUrl { get; }
    string UpdateFeedUrl { get; }
    string SyncHubUrl { get; }
    Uri BaseUri { get; }

    event Action? OnChanged;

    /// <summary>
    /// Normalize, persist (MAUI), try to apply live, raise <see cref="OnChanged"/>.
    /// </summary>
    /// <returns>
    /// True when the new URL is saved but this process must restart before
    /// HTTP/sync use it (HttpClient.BaseAddress cannot change after the first request).
    /// </returns>
    Task<bool> SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default);
}
