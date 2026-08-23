namespace App.Core.Configuration;

public class AppServerOptions
{
    public const string SectionName = "AppServer";

    /// <summary>
    /// Base URL of the Wizionic backend (auth APIs, SignalR hub, tool proxies).
    /// Dev: http://localhost:5136 — Prod: https://wizionic.com
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:5136";

    public const string GitHubRepoUrl = "https://github.com/Wizionic/wizionic";
    public const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/Wizionic/wizionic/releases/latest";

    public static string GitHubLatestDownloadUrl(string assetFileName) =>
        $"{GitHubRepoUrl}/releases/latest/download/{assetFileName}";

    public static string LatestWindowsSetupUrl => GitHubLatestDownloadUrl("Wizionic-win-Setup.exe");
    public static string LatestWindowsInstallScriptUrl => GitHubLatestDownloadUrl("install.ps1");
    public const string HostedWindowsInstallScriptUrl = "https://wizionic.com/install.ps1";
    public static string LatestSha256SumsUrl => GitHubLatestDownloadUrl("SHA256SUMS");
    public static string LatestLinuxAppImageUrl => GitHubLatestDownloadUrl("Wizionic.AppImage");
    public static string LatestLinuxInstallScriptUrl => GitHubLatestDownloadUrl("install.sh");
    public static string HomeserverWinManifestUrl => GitHubLatestDownloadUrl("homeserver-win-latest.json");
    public static string HomeserverLinuxManifestUrl => GitHubLatestDownloadUrl("homeserver-linux-latest.json");

    /// <summary>
    /// Optional override for the Velopack source. When unset, desktop updates
    /// use GitHub Releases at <see cref="GitHubRepoUrl"/> (not the login server).
    /// </summary>
    public string? UpdateFeedUrl { get; set; }

    public Uri BaseUri => new(BaseUrl.TrimEnd('/') + "/");

    public string SyncHubUrl => new Uri(BaseUri, "sync-hub").ToString();

    /// <summary>
    /// Platform-specific Velopack channel directory under the site root.
    /// Uses RuntimeInformation (not OperatingSystem) so the Core assembly stays safe if ever
    /// loaded into a browser WASM context where OperatingSystem may be unavailable.
    /// </summary>
    public static string DefaultUpdateFeedPath =>
        IsLinuxDesktop() ? "releases/linux" : "releases/windows";

    /// <summary>
    /// Velopack releases index file for the current platform (e.g. releases.win.json / releases.linux.json).
    /// </summary>
    public static string VelopackReleasesIndexFile =>
        IsLinuxDesktop() ? "releases.linux.json" : "releases.win.json";

    public string GetUpdateFeedUrl() =>
        string.IsNullOrWhiteSpace(UpdateFeedUrl)
            ? GitHubRepoUrl
            : UpdateFeedUrl.TrimEnd('/');

    private static bool IsLinuxDesktop()
    {
        // Prefer RuntimeInformation — more reliable across runtimes than OperatingSystem.IsLinux()
        // when assemblies are shared with Blazor WebAssembly.
        return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Linux);
    }
}