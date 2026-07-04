namespace ChatfishApp.Core.Configuration;

public class ChatfishServerOptions
{
    public const string SectionName = "ChatfishServer";

    /// <summary>
    /// Base URL of the Chatfish backend (auth APIs, SignalR hub, tool proxies).
    /// Dev: http://localhost:5136 — Prod: https://chatfish.me
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5136";

    /// <summary>
    /// Optional override for the Velopack feed URL. When unset, defaults to
    /// {BaseUrl}/releases/windows/
    /// </summary>
    public string? UpdateFeedUrl { get; set; }

    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/");

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();

    public string GetUpdateFeedUrl() =>
        string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? BaseUrl.TrimEnd('/') + "/releases/windows"
            : UpdateFeedUrl.TrimEnd('/');
}