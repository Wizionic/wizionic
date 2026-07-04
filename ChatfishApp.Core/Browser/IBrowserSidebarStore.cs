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

    event Action? Changed;
}