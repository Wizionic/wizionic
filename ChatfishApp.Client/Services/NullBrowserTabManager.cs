using ChatfishApp.Core.Browser;

namespace ChatfishApp.Client.Services;

/// <summary>No-op tab manager for host/WASM (embedded multi-tab browser is MAUI-only).</summary>
public sealed class NullBrowserTabManager : IBrowserTabManager
{
    private readonly BrowserTabSession _tab = new("null-tab") { Title = "New tab" };

    public IReadOnlyList<BrowserTabSession> Tabs => [_tab];
    public string ActiveTabId => _tab.Id;
    public BrowserTabSession? ActiveTab => _tab;

    public event Action? Changed;

    public Task OpenTabAsync(string? url = null, bool activate = true, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task OpenInNewTabAsync(string url, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task CloseTabAsync(string tabId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ActivateTabAsync(string tabId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task ReorderTabsAsync(IReadOnlyList<string> orderedTabIds, CancellationToken ct = default) =>
        Task.CompletedTask;
}
