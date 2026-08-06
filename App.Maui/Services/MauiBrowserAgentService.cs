using System.Globalization;
using System.Text.Json;
using App.Core.Browser;
using App.Core.UI;
using Microsoft.Maui.Controls;

namespace App.Maui.Services;

/// <summary>
/// Single-WebView browser agent with multi-tab placeholders.
/// Only the active tab is loaded; background tabs store URL/history/scroll until activated.
/// </summary>
public sealed class MauiBrowserAgentService : IBrowserAgentService, IBrowserTabManager
{
    private readonly IBrowserPanelState _panel;
    private readonly IBrowserStore _store;
    private WebView? _webView;
    private string? _pendingUrl;
    private bool _pendingScrollRestore;
    private string? _switchGeneration;

    private readonly List<BrowserTabSession> _tabs = [];
    private string _activeTabId = "";

    private string _currentUrl = "";
    private string _pageTitle = "";
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;

    public MauiBrowserAgentService(IBrowserPanelState panel, IBrowserStore store)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        var initial = new BrowserTabSession();
        _tabs.Add(initial);
        _activeTabId = initial.Id;
    }

    public bool IsAvailable => _webView != null && _panel.IsOpen;

    public string CurrentUrl => _currentUrl;
    public string PageTitle => _pageTitle;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;
    public bool IsLoading => _isLoading;

    public IReadOnlyList<BrowserTabSession> Tabs => _tabs;
    public string ActiveTabId => _activeTabId;
    public BrowserTabSession? ActiveTab =>
        _tabs.FirstOrDefault(t => t.Id == _activeTabId);

    public event Action<string>? UrlChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool>? LoadingChanged;
    public event Action? Changed;

    public void AttachWebView(WebView webView)
    {
        if (_webView != null)
            DetachWebView();

        _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        _webView.Navigating += OnNavigating;
        _webView.Navigated += OnNavigated;
    }

    public void DetachWebView()
    {
        if (_webView == null)
            return;

        _webView.Navigating -= OnNavigating;
        _webView.Navigated -= OnNavigated;
        _webView = null;
    }

    /// <summary>
    /// Called when the live document changes URL without a full WebView navigation
    /// (History API / SPA routers). Updates the location bar and active tab so tab
    /// restore reloads the correct deep link.
    /// </summary>
    /// <param name="url">New location.href</param>
    /// <param name="pushHistory">
    /// True for pushState/user navigation (adds managed history entry).
    /// False for replaceState / source sync (updates current entry only).
    /// </param>
    public void NotifyClientLocationChanged(string url, bool pushHistory = true)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Ignore about:blank noise and non-http(s) internal frames.
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        var tab = ActiveTab;
        if (tab == null || tab.IsStartPage)
            return;

        // Always keep tab.Url aligned (critical for tab switch restore).
        tab.Url = url;
        tab.IsStartPage = false;

        if (string.Equals(_currentUrl, url, StringComparison.Ordinal))
            return;

        Console.WriteLine($"[Browser] client location → {url} (push={pushHistory})");

        var previous = _currentUrl;
        _currentUrl = url;

        // Mid full-navigation (LoadUrlAsync already pushed this URL) — sync bar only.
        var alreadyPending = !string.IsNullOrWhiteSpace(_pendingUrl)
            && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase);
        var alreadyAtHistoryTip = tab.HistoryIndex >= 0
            && tab.HistoryIndex < tab.History.Count
            && string.Equals(tab.History[tab.HistoryIndex], url, StringComparison.OrdinalIgnoreCase);

        if (!alreadyPending && !alreadyAtHistoryTip)
        {
            if (pushHistory)
            {
                PushHistory(tab, url);
            }
            else if (tab.HistoryIndex >= 0 && tab.HistoryIndex < tab.History.Count)
            {
                // replaceState / redirect settle: rewrite current history slot.
                tab.History[tab.HistoryIndex] = url;
                UpdateNavStateFromTab(tab);
            }
            else
            {
                PushHistory(tab, url);
            }
        }

        UrlChanged?.Invoke(_currentUrl);
        RaiseTabsChanged();

        // Record SPA routes in visit history (store upsert is fine to call often).
        if (!string.IsNullOrWhiteSpace(previous))
            _ = _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);
    }

    /// <summary>Sync page title from platform DocumentTitleChanged without re-querying the DOM.</summary>
    public void NotifyDocumentTitleChanged(string? title)
    {
        var tab = ActiveTab;
        if (tab == null || tab.IsStartPage)
            return;

        var resolved = BrowserPageMetadata.ResolveBookmarkTitle(title, _currentUrl);
        if (string.IsNullOrWhiteSpace(resolved))
            return;

        if (string.Equals(_pageTitle, resolved, StringComparison.Ordinal)
            && string.Equals(tab.Title, resolved, StringComparison.Ordinal))
            return;

        _pageTitle = resolved;
        tab.Title = resolved;
        TitleChanged?.Invoke(_pageTitle);
        RaiseTabsChanged();
    }

    public Task NavigateAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = NormalizeUrl(url);
        if (string.IsNullOrEmpty(normalized))
            return Task.CompletedTask;

        var tab = RequireActiveTab();
        tab.IsStartPage = false;
        return LoadUrlAsync(normalized, pushHistory: true, ct);
    }

    public async Task<bool> NavigateHomeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = BrowserUrlNormalizer.ResolveHomeTarget(_store.GetSettings());
        if (target == null)
        {
            await ShowStartPageOnActiveTabAsync(ct);
            return false;
        }

        await NavigateAsync(target, ct);
        return true;
    }

    public Task GoBackAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tab = RequireActiveTab();
        if (!_canGoBack || tab.HistoryIndex <= 0)
            return Task.CompletedTask;

        tab.HistoryIndex--;
        UpdateNavStateFromTab(tab);
        return LoadUrlAsync(tab.History[tab.HistoryIndex], pushHistory: false, ct);
    }

    public Task GoForwardAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tab = RequireActiveTab();
        if (!_canGoForward || tab.HistoryIndex >= tab.History.Count - 1)
            return Task.CompletedTask;

        tab.HistoryIndex++;
        UpdateNavStateFromTab(tab);
        return LoadUrlAsync(tab.History[tab.HistoryIndex], pushHistory: false, ct);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tab = ActiveTab;
        if (_webView == null || tab == null || tab.IsStartPage || string.IsNullOrWhiteSpace(_currentUrl))
            return Task.CompletedTask;

        return LoadUrlAsync(_currentUrl, pushHistory: false, ct);
    }

    public async Task OpenExternallyAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_currentUrl))
            return;

        await Launcher.Default.OpenAsync(new Uri(_currentUrl));
    }

    public Task<string> GetPageTextAsync(CancellationToken ct = default) =>
        EvaluateScriptAsync(
            """
            (() => {
              const clone = document.body.cloneNode(true);
              clone.querySelectorAll('script,style,noscript,svg').forEach(e => e.remove());
              return (clone.innerText || '').trim();
            })()
            """,
            ct);

    public Task<string> GetPageHtmlAsync(CancellationToken ct = default) =>
        EvaluateScriptAsync("document.documentElement.outerHTML", ct);

    public Task ClickElementAsync(string selector, CancellationToken ct = default)
    {
        var js = $"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(selector)}); if (!el) throw new Error('Element not found'); el.click(); return 'ok'; }})()";
        return EvaluateScriptAsync(js, ct);
    }

    public Task FillInputAsync(string selector, string value, CancellationToken ct = default)
    {
        var js =
            "(() => { const el = document.querySelector(" + JsonSerializer.Serialize(selector) +
            "); if (!el) throw new Error('Element not found'); el.value = " + JsonSerializer.Serialize(value) +
            "; el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); return 'ok'; })()";
        return EvaluateScriptAsync(js, ct);
    }

    public async Task<string> EvaluateScriptAsync(string js, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_webView == null)
            return "";

        try
        {
            var result = await _webView.EvaluateJavaScriptAsync(js);
            return result ?? "";
        }
        catch (Exception ex)
        {
            return $"Script error: {ex.Message}";
        }
    }

    public async Task OpenTabAsync(string? url = null, bool activate = true, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tab = new BrowserTabSession();
        _tabs.Add(tab);
        RaiseTabsChanged();

        if (!activate)
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var normalized = NormalizeUrl(url);
                if (!string.IsNullOrEmpty(normalized))
                {
                    tab.Url = normalized;
                    tab.History.Add(normalized);
                    tab.HistoryIndex = 0;
                    tab.Title = normalized;
                }
            }
            else
            {
                // Placeholder new tab; content applied when activated.
                tab.IsStartPage = BrowserUrlNormalizer.ResolveHomeTarget(_store.GetSettings()) == null;
            }

            RaiseTabsChanged();
            return;
        }

        await ActivateTabAsync(tab.Id, ct);

        if (!string.IsNullOrWhiteSpace(url))
            await NavigateAsync(url, ct);
        else
            await NavigateHomeAsync(ct);
    }

    public Task OpenInNewTabAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Task.CompletedTask;

        // Ensure browser chrome is visible when a page forces a new window/tab.
        // MauiBrowserPanelState.IsOpen setter raises OnChanged.
        if (!_panel.IsOpen)
            _panel.IsOpen = true;

        return OpenTabAsync(url, activate: true, ct);
    }

    public Task ReorderTabsAsync(IReadOnlyList<string> orderedTabIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (orderedTabIds == null || orderedTabIds.Count == 0)
            return Task.CompletedTask;

        var byId = _tabs.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var reordered = new List<BrowserTabSession>(byId.Count);
        foreach (var id in orderedTabIds)
        {
            if (byId.Remove(id, out var tab))
                reordered.Add(tab);
        }

        // Append any tabs not present in the ordered list (safety).
        foreach (var remaining in byId.Values)
            reordered.Add(remaining);

        if (reordered.Count == 0)
            return Task.CompletedTask;

        _tabs.Clear();
        _tabs.AddRange(reordered);
        RaiseTabsChanged();
        return Task.CompletedTask;
    }

    public async Task CloseTabAsync(string tabId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(tabId))
            return;

        var index = _tabs.FindIndex(t => t.Id == tabId);
        if (index < 0)
            return;

        // Last tab: reset instead of closing.
        if (_tabs.Count == 1)
        {
            await ResetTabToHomeAsync(_tabs[0], ct);
            return;
        }

        var wasActive = string.Equals(_activeTabId, tabId, StringComparison.Ordinal);
        _tabs.RemoveAt(index);

        if (!wasActive)
        {
            RaiseTabsChanged();
            return;
        }

        // Prefer tab to the right, else left (Chrome-style).
        var nextIndex = Math.Min(index, _tabs.Count - 1);
        await ActivateTabAsync(_tabs[nextIndex].Id, ct);
        RaiseTabsChanged();
    }

    public async Task ActivateTabAsync(string tabId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = _tabs.FirstOrDefault(t => t.Id == tabId);
        if (target == null)
            return;

        if (string.Equals(_activeTabId, tabId, StringComparison.Ordinal))
        {
            target.LastActivatedUtc = DateTimeOffset.UtcNow;
            return;
        }

        await SnapshotActiveTabAsync();

        _activeTabId = tabId;
        target.LastActivatedUtc = DateTimeOffset.UtcNow;
        _switchGeneration = Guid.NewGuid().ToString("N");
        _pendingScrollRestore = !target.IsStartPage && (target.ScrollX != 0 || target.ScrollY != 0);

        if (target.IsStartPage || string.IsNullOrWhiteSpace(target.Url))
        {
            ApplyActiveTabToSurface(target);
            RaiseTabsChanged();
            return;
        }

        ApplyActiveTabToSurface(target);
        // Always force load: WebView still holds the previous tab's document.
        await LoadUrlAsync(target.Url, pushHistory: false, ct, force: true);
        RaiseTabsChanged();
    }

    private async Task ResetTabToHomeAsync(BrowserTabSession tab, CancellationToken ct)
    {
        tab.History.Clear();
        tab.HistoryIndex = -1;
        tab.ScrollX = 0;
        tab.ScrollY = 0;
        tab.FaviconUrl = null;
        tab.Title = "";
        tab.Url = "";
        tab.IsStartPage = false;

        if (!string.Equals(_activeTabId, tab.Id, StringComparison.Ordinal))
            _activeTabId = tab.Id;

        await NavigateHomeAsync(ct);
        RaiseTabsChanged();
    }

    private async Task ShowStartPageOnActiveTabAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tab = RequireActiveTab();
        tab.IsStartPage = true;
        tab.Url = "";
        tab.Title = "Bookmarks";
        tab.FaviconUrl = null;
        tab.ScrollX = 0;
        tab.ScrollY = 0;
        // Keep history so back from a later navigation can return; do not push start page into history.

        _currentUrl = "";
        _pageTitle = "";
        _canGoBack = tab.HistoryIndex > 0;
        _canGoForward = tab.HistoryIndex >= 0 && tab.HistoryIndex < tab.History.Count - 1;
        SetLoading(false);
        _pendingUrl = null;

        UrlChanged?.Invoke(_currentUrl);
        TitleChanged?.Invoke(_pageTitle);
        RaiseTabsChanged();
        await Task.CompletedTask;
    }

    private async Task SnapshotActiveTabAsync()
    {
        var tab = ActiveTab;
        if (tab == null)
            return;

        tab.Url = _currentUrl;
        tab.Title = _pageTitle;
        if (!string.IsNullOrWhiteSpace(_currentUrl))
            tab.FaviconUrl = BrowserFaviconUrl.GetCached(_currentUrl) ?? tab.FaviconUrl;

        if (!tab.IsStartPage && _webView != null && !string.IsNullOrWhiteSpace(_currentUrl))
        {
            try
            {
                var raw = await EvaluateScriptAsync(
                    "JSON.stringify({x: window.scrollX || 0, y: window.scrollY || 0})");
                var cleaned = UnquoteJsString(raw);
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(cleaned);
                    if (doc.RootElement.TryGetProperty("x", out var xProp))
                        tab.ScrollX = xProp.GetDouble();
                    if (doc.RootElement.TryGetProperty("y", out var yProp))
                        tab.ScrollY = yProp.GetDouble();
                }
            }
            catch
            {
                // Non-fatal — scroll restore is best-effort.
            }
        }
    }

    private void ApplyActiveTabToSurface(BrowserTabSession tab)
    {
        _currentUrl = tab.IsStartPage ? "" : (tab.Url ?? "");
        _pageTitle = tab.Title ?? "";
        UpdateNavStateFromTab(tab);
        UrlChanged?.Invoke(_currentUrl);
        TitleChanged?.Invoke(_pageTitle);
        LoadingChanged?.Invoke(_isLoading);
    }

    private Task LoadUrlAsync(string url, bool pushHistory, CancellationToken ct, bool force = false)
    {
        ct.ThrowIfCancellationRequested();
        if (_webView == null)
            return Task.CompletedTask;

        var tab = RequireActiveTab();
        tab.IsStartPage = false;

        if (!force && _isLoading && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Browser] skip duplicate navigation to {url}");
            return Task.CompletedTask;
        }

        // Same-URL skip only when not forcing (tab switch always force-reloads the WebView).
        if (!force
            && string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase)
            && !_isLoading)
        {
            Console.WriteLine($"[Browser] already at {url}");
            return Task.CompletedTask;
        }

        if (pushHistory)
            PushHistory(tab, url);

        tab.Url = url;
        _pendingUrl = url;
        Console.WriteLine($"[Browser] navigating to {url}");

        try
        {
            _webView.Source = new UrlWebViewSource { Url = url };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] navigation error: {ex.Message}");
            SetLoading(false);
            _pendingUrl = null;
        }

        RaiseTabsChanged();
        return Task.CompletedTask;
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        SetLoading(true);
    }

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        _pendingUrl = null;

        if (e.Result != WebNavigationResult.Success)
        {
            SetLoading(false);
            Console.WriteLine($"[Browser] navigation failed: {e.Result} url={e.Url}");
            return;
        }

        var url = e.Url ?? "";
        if (string.IsNullOrWhiteSpace(url))
        {
            SetLoading(false);
            return;
        }

        var tab = ActiveTab;
        if (tab == null || tab.IsStartPage)
        {
            SetLoading(false);
            return;
        }

        _currentUrl = url;
        tab.Url = url;
        Console.WriteLine($"[Browser] navigated to {url}");
        UrlChanged?.Invoke(_currentUrl);
        SetLoading(false);
        _ = RecordVisitAsync();

        if (_pendingScrollRestore)
        {
            _pendingScrollRestore = false;
            var gen = _switchGeneration;
            var sx = tab.ScrollX;
            var sy = tab.ScrollY;
            _ = RestoreScrollAsync(gen, sx, sy);
        }

        RaiseTabsChanged();
    }

    private async Task RestoreScrollAsync(string? generation, double scrollX, double scrollY)
    {
        if (scrollX == 0 && scrollY == 0)
            return;

        foreach (var delayMs in new[] { 50, 300, 800 })
        {
            try
            {
                await Task.Delay(delayMs);
                if (generation != _switchGeneration)
                    return;

                var x = scrollX.ToString(CultureInfo.InvariantCulture);
                var y = scrollY.ToString(CultureInfo.InvariantCulture);
                await EvaluateScriptAsync($"window.scrollTo({x}, {y})");
            }
            catch
            {
                // Ignore if WebView was detached.
            }
        }
    }

    private async Task RecordVisitAsync()
    {
        await _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);
        await UpdateTitleAsync();
        _ = RefreshTitleAfterSpaSettleAsync();
    }

    private async Task RefreshTitleAfterSpaSettleAsync()
    {
        foreach (var delayMs in new[] { 400, 1200, 2500 })
        {
            try
            {
                await Task.Delay(delayMs);
                if (string.IsNullOrWhiteSpace(_currentUrl))
                    return;
                await UpdateTitleAsync();
            }
            catch
            {
                // Ignore if WebView was detached mid-delay.
            }
        }
    }

    private async Task UpdateTitleAsync()
    {
        var title = await EvaluateScriptAsync(BrowserPageMetadata.ReadTitleScript);
        var resolved = BrowserPageMetadata.ResolveBookmarkTitle(UnquoteJsString(title), _currentUrl);

        string? icon = null;
        try
        {
            var iconRaw = await EvaluateScriptAsync(BrowserPageMetadata.ReadFaviconHrefScript);
            icon = UnquoteJsString(iconRaw);
            if (!string.IsNullOrWhiteSpace(icon) && !string.IsNullOrWhiteSpace(_currentUrl))
                BrowserFaviconUrl.Remember(_currentUrl, icon);
        }
        catch { /* non-fatal */ }

        var tab = ActiveTab;
        if (tab != null && !tab.IsStartPage)
        {
            tab.Title = resolved;
            if (!string.IsNullOrWhiteSpace(icon))
                tab.FaviconUrl = icon;
            else if (!string.IsNullOrWhiteSpace(_currentUrl))
                tab.FaviconUrl = BrowserFaviconUrl.GetCached(_currentUrl) ?? tab.FaviconUrl;
        }

        if (string.Equals(_pageTitle, resolved, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(_currentUrl))
                await _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);
            RaiseTabsChanged();
            return;
        }

        _pageTitle = resolved;
        TitleChanged?.Invoke(_pageTitle);

        if (!string.IsNullOrWhiteSpace(_currentUrl))
            await _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);

        RaiseTabsChanged();
    }

    private static string UnquoteJsString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var trimmed = value.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase))
            return "";

        if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n");

        return trimmed;
    }

    private void PushHistory(BrowserTabSession tab, string url)
    {
        if (tab.HistoryIndex >= 0 && tab.HistoryIndex < tab.History.Count - 1)
            tab.History.RemoveRange(tab.HistoryIndex + 1, tab.History.Count - tab.HistoryIndex - 1);

        if (tab.History.Count == 0 || !string.Equals(tab.History[^1], url, StringComparison.OrdinalIgnoreCase))
        {
            tab.History.Add(url);
            tab.HistoryIndex = tab.History.Count - 1;
        }
        else
        {
            tab.HistoryIndex = tab.History.Count - 1;
        }

        UpdateNavStateFromTab(tab);
    }

    private void UpdateNavStateFromTab(BrowserTabSession tab)
    {
        _canGoBack = tab.HistoryIndex > 0;
        _canGoForward = tab.HistoryIndex >= 0 && tab.HistoryIndex < tab.History.Count - 1;
    }

    private void SetLoading(bool loading)
    {
        if (_isLoading == loading)
            return;

        _isLoading = loading;
        LoadingChanged?.Invoke(_isLoading);
    }

    private BrowserTabSession RequireActiveTab()
    {
        var tab = ActiveTab;
        if (tab != null)
            return tab;

        var created = new BrowserTabSession();
        _tabs.Add(created);
        _activeTabId = created.Id;
        return created;
    }

    private void RaiseTabsChanged() => Changed?.Invoke();

    private string NormalizeUrl(string input) =>
        BrowserUrlNormalizer.Normalize(input, _store.GetSettings());
}
