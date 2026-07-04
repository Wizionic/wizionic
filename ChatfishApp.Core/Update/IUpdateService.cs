namespace ChatfishApp.Core.Update;

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
    Task<UpdateCheckResult> CheckForUpdateAsync();

    Task DownloadAndInstallAsync(UpdateInfo update);
}
