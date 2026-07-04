using ChatfishApp.Core.Browser;

namespace ChatfishApp.Client.Services;

public sealed class NullBrowserSidebarStore : IBrowserSidebarStore
{
    public event Action? Changed;

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<SidebarApp> GetPinnedApps() => [];
    public SidebarApp? FindPinnedByUrl(string url) => null;
    public Task<SidebarApp> PinAppAsync(SidebarApp app, CancellationToken ct = default) => Task.FromResult(app);
    public Task UpdateAppAsync(SidebarApp app, CancellationToken ct = default) => Task.CompletedTask;
    public Task UnpinAppAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReorderAppsAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public double GetSidePanelWidthPx() => 320;
    public Task SetSidePanelWidthPxAsync(double widthPx, CancellationToken ct = default) => Task.CompletedTask;
    public OpenTarget GetLastOpenTarget(string appId) => OpenTarget.SidePanel;
    public Task SetLastOpenTargetAsync(string appId, OpenTarget target, CancellationToken ct = default) => Task.CompletedTask;
}