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
    /// {BaseUrl}/releases/windows on Windows and {BaseUrl}/releases/linux on Linux.
    /// </summary>
    public string? UpdateFeedUrl { get; set; }

    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/");

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();

    /// <summary>
    /// Platform-specific Velopack channel directory under the site root.
    /// </summary>
    public static string DefaultUpdateFeedPath =>
        OperatingSystem.IsLinux() ? "releases/linux" : "releases/windows";

    /// <summary>
    /// Velopack releases index file for the current platform (e.g. releases.win.json / releases.linux.json).
    /// </summary>
    public static string VelopackReleasesIndexFile =>
        OperatingSystem.IsLinux() ? "releases.linux.json" : "releases.win.json";

    public string GetUpdateFeedUrl() =>
        string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? BaseUrl.TrimEnd('/') + "/" + DefaultUpdateFeedPath
            : UpdateFeedUrl.TrimEnd('/');
}