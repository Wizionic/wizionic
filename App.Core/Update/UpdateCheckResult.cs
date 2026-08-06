namespace App.Core.Update;

public enum UpdateCheckStatus
{
    /// <summary>A newer version is available on the update feed.</summary>
    UpdateAvailable,

    /// <summary>Installed app is already on the latest release.</summary>
    UpToDate,

    /// <summary>App was not installed via Velopack (e.g. Debug / dotnet run).</summary>
    NotInstalled,

    /// <summary>Could not reach or parse the update feed.</summary>
    CheckFailed,

    /// <summary>Updates are not supported on this platform (WASM / host).</summary>
    Unavailable
}

/// <summary>
/// Outcome of an update check, including status and optional release info.
/// </summary>
public class UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; }

    /// <summary>Velopack release info when <see cref="Status"/> is <see cref="UpdateCheckStatus.UpdateAvailable"/>.</summary>
    public UpdateInfo? Update { get; init; }

    /// <summary>Currently installed version, when known.</summary>
    public string? CurrentVersion { get; init; }

    /// <summary>Human-readable detail for errors or unsupported states.</summary>
    public string? Message { get; init; }

    /// <summary>Velopack feed URL used for the check.</summary>
    public string? FeedUrl { get; init; }

    /// <summary>Highest full-release version found on the remote feed.</summary>
    public string? LatestFeedVersion { get; init; }

    /// <summary>When true, show the installer download link instead of in-app install.</summary>
    public bool RequiresManualInstall { get; init; }
}