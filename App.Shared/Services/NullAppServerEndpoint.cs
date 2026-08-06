using App.Core.Configuration;

namespace App.Shared.Services;

/// <summary>Fixed endpoint for WASM/host (same-origin). SetBaseUrl is a no-op.</summary>
public sealed class NullAppServerEndpoint : IAppServerEndpoint
{
    public static NullAppServerEndpoint Instance { get; } = new();

    private NullAppServerEndpoint() { }

    public string BaseUrl => "";
    public string UpdateFeedUrl => "";
    public string SyncHubUrl => "/sync-hub";
    public Uri BaseUri => new("/", UriKind.Relative);

    public event Action? OnChanged
    {
        add { }
        remove { }
    }

    public Task SetBaseUrlAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
