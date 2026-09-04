using App.Core.Update;

namespace App.Shared.Services;

/// <summary>
/// No-op update service for non-MAUI targets (WASM / Host).
/// Keeps the shared DI container and SettingsPage the same across targets.
/// </summary>
public class NullUpdateService : IUpdateService
{
    public static readonly NullUpdateService Instance = new();

    private NullUpdateService() { }

    public string? GetInstalledVersion() => null;

    public bool IsVelopackInstalled => false;

    public bool UpdatesManagedByStore => false;

    public string? UpdateFeedUrl => null;

    public string? InstallerDownloadUrl => null;

    public Task<UpdateCheckResult> CheckForUpdateAsync() =>
        Task.FromResult(new UpdateCheckResult
        {
            Status = UpdateCheckStatus.Unavailable,
            Message = "In-app updates are only available in the desktop app."
        });

    public Task DownloadAndInstallAsync(UpdateInfo update) => Task.CompletedTask;
}