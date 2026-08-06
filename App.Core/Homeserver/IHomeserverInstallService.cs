namespace App.Core.Homeserver;

public interface IHomeserverInstallService
{
    /// <summary>True on platforms that can host a local homeserver (Windows / Linux desktop).</summary>
    bool IsSupported { get; }

    /// <summary>Set when Velopack reports first run after install; cleared after the prompt is handled.</summary>
    bool ShouldPromptOnStartup { get; set; }

    /// <summary>Set by Velopack after-update hook so a normal launch can refresh the homeserver binaries.</summary>
    bool PendingUpdateCheck { get; set; }

    HomeserverState GetState();

    /// <summary>User declined installing a homeserver; never prompt again.</summary>
    void Decline();

    /// <summary>
    /// Download, extract, and start the homeserver. Prefer Windows Service; fall back to user-session.
    /// </summary>
    Task<HomeserverInstallResult> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// If a homeserver is installed and a newer package is available, update binaries only (never touch data/).
    /// </summary>
    Task<HomeserverInstallResult> UpdateIfNeededAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stop service / remove binaries. Leaves ProgramData data/ intact.</summary>
    Task UninstallBinariesAsync(CancellationToken cancellationToken = default);

    string? GetServiceStatusText();
}

public sealed class HomeserverInstallResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public HomeserverInstallMode Mode { get; init; }
    public string? BaseUrl { get; init; }
    public string? Version { get; init; }

    public static HomeserverInstallResult Ok(string message, HomeserverInstallMode mode, string? baseUrl = null, string? version = null) =>
        new() { Success = true, Message = message, Mode = mode, BaseUrl = baseUrl, Version = version };

    public static HomeserverInstallResult Fail(string message) =>
        new() { Success = false, Message = message, Mode = HomeserverInstallMode.Unknown };
}
