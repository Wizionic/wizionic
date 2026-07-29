namespace ChatfishApp.Core.Configuration;

/// <summary>
/// Mutable login-server endpoint for MAUI. WASM/host use a fixed same-origin implementation.
/// </summary>
public interface IChatfishServerEndpoint
{
    string BaseUrl { get; }
    string UpdateFeedUrl { get; }
    string SyncHubUrl { get; }
    Uri BaseUri { get; }

    event Action? OnChanged;

    /// <summary>Normalize, persist (MAUI), apply to HttpClient/cookies, raise <see cref="OnChanged"/>.</summary>
    Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default);
}
