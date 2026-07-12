using ChatfishApp.Core.Storage;

namespace ChatfishApp.Core.Browser;

public interface IBrowserSidebarStore
{
    Task LoadAsync(CancellationToken ct = default);

    IReadOnlyList<SidebarApp> GetPinnedApps();
    SidebarApp? FindPinnedByUrl(string url);
    Task<SidebarApp> PinAppAsync(SidebarApp app, CancellationToken ct = default);
    Task UpdateAppAsync(SidebarApp app, CancellationToken ct = default);
    Task UnpinAppAsync(string id, CancellationToken ct = default);
    Task ReorderAppsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default);

    double GetSidePanelWidthPx();
    Task SetSidePanelWidthPxAsync(double widthPx, CancellationToken ct = default);

    OpenTarget GetLastOpenTarget(string appId);
    Task SetLastOpenTargetAsync(string appId, OpenTarget target, CancellationToken ct = default);

    // --- Cross-device sync (MAUI) ---
    Task<List<SyncManifestEntry>> LoadSidebarAppManifestEntriesAsync(bool backfillMissingFingerprints = false, CancellationToken ct = default);
    Task<SidebarApp?> GetAppByIdAsync(string id, CancellationToken ct = default);
    Task ApplySidebarAppPayloadAsync(SidebarApp app, CancellationToken ct = default);
    Task<bool> ShouldAcceptIncomingSidebarAppAsync(SidebarApp app, CancellationToken ct = default);
    Task<DateTime> TombstoneSidebarAppAsync(string id, CancellationToken ct = default);
    Task<bool> TryApplyRemoteSidebarAppDeleteAsync(string id, long deletedAtTicks, CancellationToken ct = default);

    event Action? Changed;
}
