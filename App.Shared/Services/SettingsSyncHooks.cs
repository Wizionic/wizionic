using App.Core.Storage;
using App.Core.Sync;

namespace App.Shared.Services;

/// <summary>
/// Call after a local settings save so timestamps update and auto-sync can push.
/// </summary>
public static class SettingsSyncHooks
{
    public static async Task AfterLocalSaveAsync(
        ISettingsSyncStore? store,
        ISyncService? sync,
        string category,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(category))
            return;

        if (store != null)
            await store.TouchCategoryAsync(category, ct);

        sync?.ScheduleAutoSyncSettingsAfterLocalSave(category);
    }
}
