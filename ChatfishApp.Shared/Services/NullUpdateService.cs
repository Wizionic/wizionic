using ChatfishApp.Core.Update;

namespace ChatfishApp.Shared.Services;

/// <summary>
/// No-op update service for non-MAUI targets (WASM / Host).
/// Keeps the shared DI container and SettingsPage the same across targets.
/// </summary>
public class NullUpdateService : IUpdateService
{
    public static readonly NullUpdateService Instance = new();

    private NullUpdateService() { }

    public Task<UpdateInfo?> CheckForUpdateAsync() => Task.FromResult<UpdateInfo?>(null);

    public Task DownloadAndInstallAsync(UpdateInfo update) => Task.CompletedTask;
}
