using App.Core.Homeserver;

namespace App.Shared.Services;

/// <summary>No-op homeserver installer for WASM / host / non-Windows targets.</summary>
public sealed class NullHomeserverInstallService : IHomeserverInstallService
{
    public static readonly NullHomeserverInstallService Instance = new();

    private NullHomeserverInstallService() { }

    public bool IsSupported => false;
    public bool ShouldPromptOnStartup { get; set; }
    public bool PendingUpdateCheck { get; set; }

    public HomeserverState GetState() => HomeserverState.Load();

    public void Decline() { }

    public Task<HomeserverInstallResult> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Fail("Home Server install is only available on Windows desktop."));

    public Task<HomeserverInstallResult> UpdateIfNeededAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Ok("No homeserver on this platform.", HomeserverInstallMode.Unknown));

    public Task<HomeserverInstallResult> StartAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Fail("Home Server is only available on Windows and Linux desktop."));

    public Task<HomeserverInstallResult> StopAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Fail("Home Server is only available on Windows and Linux desktop."));

    public Task<HomeserverInstallResult> UninstallAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Fail("Home Server is only available on Windows and Linux desktop."));

    public Task<HomeserverInstallResult> EnsureLanAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HomeserverInstallResult.Ok("No homeserver on this platform.", HomeserverInstallMode.Unknown));

    public bool IsRunning() => false;

    public HomeserverListenAddresses GetListenAddresses() => HomeserverListenAddresses.LocalOnly();

    public bool IsLoginServerThisHomeserver(string? baseUrl) => false;

    public string? GetServiceStatusText() => null;
}
