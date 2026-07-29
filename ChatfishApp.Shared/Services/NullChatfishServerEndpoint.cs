using ChatfishApp.Core.Configuration;

namespace ChatfishApp.Shared.Services;

/// <summary>Fixed endpoint for WASM/host (same-origin). SetBaseUrl is a no-op.</summary>
public sealed class NullChatfishServerEndpoint : IChatfishServerEndpoint
{
    public static NullChatfishServerEndpoint Instance { get; } = new();

    private NullChatfishServerEndpoint() { }

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
