using App.Core.Storage;

namespace App.Shared.Services;

public sealed class NullSettingsSyncStore : ISettingsSyncStore
{
    public static readonly NullSettingsSyncStore Instance = new();

    public event Action? OnSettingsChanged;

    public Task<IReadOnlyList<SyncManifestEntry>> LoadManifestEntriesAsync(
        IEnumerable<string>? categories = null,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SyncManifestEntry>>(Array.Empty<SyncManifestEntry>());

    public Task<SettingsSyncPayload?> ExportAsync(string category, CancellationToken ct = default) =>
        Task.FromResult<SettingsSyncPayload?>(null);

    public Task<bool> ShouldAcceptIncomingAsync(SettingsSyncPayload payload, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task ApplyAsync(SettingsSyncPayload payload, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task TouchCategoryAsync(string category, CancellationToken ct = default) =>
        Task.CompletedTask;
}
