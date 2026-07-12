#if LINUX_DESKTOP
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;

namespace ChatfishApp.Maui.Services.Linux;

/// <summary>
/// Embedded browser agent backed by a native WebKit.WebView (Linux desktop).
/// Single WebView with multi-tab placeholders (same model as MauiBrowserAgentService).
/// </summary>
public sealed class LinuxBrowserAgentService : IBrowserAgentService, IBrowserTabManager
{
	private readonly IBrowserPanelState _panel;
	private readonly IBrowserStore _store;
	private WebKit.WebView? _webView;
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

	// Root GirCore signal handlers so GC cannot collect them.
	private GObject.SignalHandler<WebKit.WebView, WebKit.WebView.LoadChangedSignalArgs>? _loadChangedHandler;
	private GObject.SignalHandler<WebKit.WebView, WebKit.WebView.CreateSignalArgs>? _createHandler;

	public LinuxBrowserAgentService(IBrowserPanelState panel, IBrowserStore store)
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

	public void AttachWebView(WebKit.WebView webView)
	{
		if (_webView != null)
			DetachWebView();

		_webView = webView ?? throw new ArgumentNullException(nameof(webView));
		_loadChangedHandler = OnLoadChanged;
		_webView.OnLoadChanged += _loadChangedHandler;

		// Intercept window.open / target=_blank — open a placeholder tab instead of a new WebView.
		try
		{
			_createHandler = OnCreateWebView;
			_webView.OnCreate += _createHandler;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux create-signal attach failed: {ex.Message}");
		}
	}

	public void DetachWebView()
	{
		if (_webView == null)
			return;

		if (_loadChangedHandler != null)
			_webView.OnLoadChanged -= _loadChangedHandler;
		_loadChangedHandler = null;

		if (_createHandler != null)
		{
			try { _webView.OnCreate -= _createHandler; } catch { /* ignore */ }
		}
		_createHandler = null;
		_webView = null;
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

		return LoadUrlAsync(_currentUrl, pushHistory: false, ct, force: true);
	}

	public Task OpenExternallyAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (string.IsNullOrWhiteSpace(_currentUrl))
			return Task.CompletedTask;

		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = _currentUrl,
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] open external failed: {ex.Message}");
		}

		return Task.CompletedTask;
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
			var result = await _webView.EvaluateJavascriptAsync(js);
			return result?.ToString() ?? "";
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
		await LoadUrlAsync(target.Url, pushHistory: false, ct, force: true);
		RaiseTabsChanged();
	}

	/// <summary>
	/// WebKit create signal: decide policy for new windows. Returning null cancels the new view;
	/// we open the target URL in a Chatfish tab instead when available.
	/// </summary>
	private WebKit.WebView? OnCreateWebView(WebKit.WebView sender, WebKit.WebView.CreateSignalArgs args)
	{
		try
		{
			var behavior = _store.GetSettings().NewWindowBehavior;
			// Navigation action may carry the URI for target=_blank; GirCore surface varies by version.
			var uri = TryGetCreateUri(args);

			if (behavior == BrowserNewWindowBehavior.ExternalBrowser && !string.IsNullOrWhiteSpace(uri))
			{
				try
				{
					Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Browser] external open failed: {ex.Message}");
				}
				return null;
			}

			if (!string.IsNullOrWhiteSpace(uri))
				_ = OpenInNewTabAsync(uri);
			else
				Console.WriteLine("[Browser] Linux create without URI — tab open skipped");

			return null; // Do not create an extra WebView.
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser] Linux OnCreate failed: {ex.Message}");
			return null;
		}
	}

	private static string? TryGetCreateUri(WebKit.WebView.CreateSignalArgs args)
	{
		try
		{
			// Reflection keeps us resilient if GirCore renames navigation-action APIs.
			var navProp = args.GetType().GetProperty("NavigationAction")
				?? args.GetType().GetProperty("navigation_action");
			var nav = navProp?.GetValue(args);
			if (nav == null)
				return null;

			var reqProp = nav.GetType().GetProperty("Request")
				?? nav.GetType().GetMethod("GetRequest");
			object? request = null;
			if (reqProp is System.Reflection.PropertyInfo pi)
				request = pi.GetValue(nav);
			else if (reqProp is System.Reflection.MethodInfo mi)
				request = mi.Invoke(nav, null);

			if (request == null)
				return null;

			var uriProp = request.GetType().GetProperty("Uri")
				?? request.GetType().GetMethod("GetUri");
			if (uriProp is System.Reflection.PropertyInfo up)
				return up.GetValue(request)?.ToString();
			if (uriProp is System.Reflection.MethodInfo um)
				return um.Invoke(request, null)?.ToString();
		}
		catch
		{
			// Best-effort.
		}

		return null;
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

		_currentUrl = "";
		_pageTitle = "";
		UpdateNavStateFromTab(tab);
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
			catch { /* best-effort */ }
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
		{
			Console.WriteLine("[Browser] LoadUrlAsync: WebView not attached");
			return Task.CompletedTask;
		}

		var tab = RequireActiveTab();
		tab.IsStartPage = false;

		if (!force && _isLoading && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase))
			return Task.CompletedTask;

		if (!force
			&& string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase)
			&& !_isLoading)
			return Task.CompletedTask;

		if (pushHistory)
			PushHistory(tab, url);

		tab.Url = url;
		_pendingUrl = url;

		if (!string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase))
		{
			_currentUrl = url;
			UrlChanged?.Invoke(_currentUrl);
		}

		Console.WriteLine($"[Browser] navigating to {url}");
		try
		{
			SetLoading(true);
			_webView.LoadUri(url);
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

	private void OnLoadChanged(WebKit.WebView sender, WebKit.WebView.LoadChangedSignalArgs args)
	{
		switch (args.LoadEvent)
		{
			case WebKit.LoadEvent.Started:
			case WebKit.LoadEvent.Redirected:
				SetLoading(true);
				break;

			case WebKit.LoadEvent.Committed:
				UpdateUrlFromWebView(sender);
				break;

			case WebKit.LoadEvent.Finished:
				_pendingUrl = null;
				UpdateUrlFromWebView(sender);
				SetLoading(false);
				// Prefer managed per-tab history for back/forward UI consistency with Windows.
				if (ActiveTab is { } tab)
					UpdateNavStateFromTab(tab);
				_ = RecordVisitAsync();

				if (_pendingScrollRestore && ActiveTab is { } active)
				{
					_pendingScrollRestore = false;
					var gen = _switchGeneration;
					_ = RestoreScrollAsync(gen, active.ScrollX, active.ScrollY);
				}

				RaiseTabsChanged();
				break;
		}
	}

	private void UpdateUrlFromWebView(WebKit.WebView webView)
	{
		var url = webView.GetUri() ?? "";
		if (string.IsNullOrWhiteSpace(url) || string.Equals(url, _currentUrl, StringComparison.Ordinal))
			return;

		var tab = ActiveTab;
		if (tab == null || tab.IsStartPage)
			return;

		_currentUrl = url;
		tab.Url = url;
		Console.WriteLine($"[Browser] navigated to {url}");
		UrlChanged?.Invoke(_currentUrl);
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
			catch { /* detached */ }
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
				// WebView may have been detached.
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
#endif
