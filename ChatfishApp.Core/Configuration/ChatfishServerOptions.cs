namespace ChatfishApp.Core.Configuration;

public class ChatfishServerOptions
{
    public const string SectionName = "ChatfishServer";

    /// <summary>
    /// Base URL of the Chatfish backend (auth APIs, SignalR hub, tool proxies).
    /// Dev: http://localhost:5136 — Prod: https://chatfish.me
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5136";

    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/");

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();
}