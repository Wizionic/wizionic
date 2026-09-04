namespace App.Core.Update;

/// <summary>
/// Lightweight wrapper around a platform update provider's release info.
/// Keeps the shared project free of platform-specific SDK types (e.g. Velopack).
/// </summary>
public class UpdateInfo
{
    public Release TargetRelease { get; init; } = new();

    /// <summary>
    /// Full version string (e.g. "1.2.3").
    /// </summary>
    public string Version => TargetRelease.Version;

    public class Release
    {
        public string Version { get; init; } = "";
    }
}

/// <summary>
/// Contract for the app's auto-update service.
/// MAUI implements with Velopack; WASM/host provide a no-op.
/// </summary>
public interface IUpdateService
{
    /// <summary>Velopack-installed version, when available.</summary>
    string? GetInstalledVersion();

    /// <summary>True when running from a Velopack Setup.exe install.</summary>
    bool IsVelopackInstalled { get; }

    /// <summary>True when this install is updated by the Microsoft Store (not GitHub/Velopack).</summary>
    bool UpdatesManagedByStore { get; }

    /// <summary>Update feed URL (empty when updates are unavailable on this platform).</summary>
    string? UpdateFeedUrl { get; }

    /// <summary>Direct installer download for the current OS (GitHub Releases).</summary>
    string? InstallerDownloadUrl { get; }

    Task<UpdateCheckResult> CheckForUpdateAsync();

    Task DownloadAndInstallAsync(UpdateInfo update);
}
