namespace App.Core.Browser;

/// <summary>
/// Multi-tab chrome for the embedded browser. Only the active tab is live in the WebView;
/// other tabs are placeholders restored on activate.
/// </summary>
public interface IBrowserTabManager
{
    IReadOnlyList<BrowserTabSession> Tabs { get; }

    string ActiveTabId { get; }

    BrowserTabSession? ActiveTab { get; }

    /// <summary>Raised when the tab list, active tab, or active tab chrome metadata changes.</summary>
    event Action? Changed;

    /// <summary>
    /// Open a new tab. Null/empty <paramref name="url"/> uses homepage settings (or bookmarks start page).
    /// </summary>
    Task OpenTabAsync(string? url = null, bool activate = true, CancellationToken ct = default);

    /// <summary>Open <paramref name="url"/> in a new tab and activate it (target=_blank / window.open).</summary>
    Task OpenInNewTabAsync(string url, CancellationToken ct = default);

    Task CloseTabAsync(string tabId, CancellationToken ct = default);

    Task ActivateTabAsync(string tabId, CancellationToken ct = default);

    /// <summary>Reorder open tabs to match <paramref name="orderedTabIds"/> (subset or full list of existing ids).</summary>
    Task ReorderTabsAsync(IReadOnlyList<string> orderedTabIds, CancellationToken ct = default);
}
