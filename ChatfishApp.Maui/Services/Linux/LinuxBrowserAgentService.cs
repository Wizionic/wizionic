#if LINUX_DESKTOP
using System.Diagnostics;
using System.Text.Json;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;

namespace ChatfishApp.Maui.Services.Linux;

/// <summary>
/// Embedded browser agent backed by a native WebKit.WebView (Linux desktop).
/// </summary>
public sealed class LinuxBrowserAgentService : IBrowserAgentService
{
	private readonly IBrowserPanelState _panel;
	private readonly IBrowserStore _store;
	private WebKit.WebView? _webView;
	private readonly List<string> _history = [];
	private int _historyIndex = -1;
	private string? _pendingUrl;

	private string _currentUrl = "";
	private string _pageTitle = "";
	private bool _canGoBack;
	private bool _canGoForward;
	private bool _isLoading;

	// Root GirCore signal handlers so GC cannot collect them.
	private GObject.SignalHandler<WebKit.WebView, WebKit.WebView.LoadChangedSignalArgs>? _loadChangedHandler;

	public LinuxBrowserAgentService(IBrowserPanelState panel, IBrowserStore store)
	{
		_panel = panel ?? throw new ArgumentNullException(nameof(panel));
		_store = store ?? throw new ArgumentNullException(nameof(store));
	}

	// WebView is attached at host startup; availability for tools requires the browser panel open.
	public bool IsAvailable => _webView != null && _panel.IsOpen;

	public string CurrentUrl => _currentUrl;
	public string PageTitle => _pageTitle;
	public bool CanGoBack => _canGoBack;
	public bool CanGoForward => _canGoForward;
	public bool IsLoading => _isLoading;

	public event Action<string>? UrlChanged;
	public event Action<string>? TitleChanged;
	public event Action<bool>? LoadingChanged;

	public void AttachWebView(WebKit.WebView webView)
	{
		if (_webView != null)
			DetachWebView();

		_webView = webView ?? throw new ArgumentNullException(nameof(webView));
		_loadChangedHandler = OnLoadChanged;
		_webView.OnLoadChanged += _loadChangedHandler;
	}

	public void DetachWebView()
	{
		if (_webView == null)
			return;

		if (_loadChangedHandler != null)
			_webView.OnLoadChanged -= _loadChangedHandler;
		_loadChangedHandler = null;
		_webView = null;
	}

	public Task NavigateAsync(string url, CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		var normalized = NormalizeUrl(url);
		if (string.IsNullOrEmpty(normalized))
			return Task.CompletedTask;

		return LoadUrlAsync(normalized, pushHistory: true, ct);
	}

	public async Task<bool> NavigateHomeAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		var target = BrowserUrlNormalizer.ResolveHomeTarget(_store.GetSettings());
		if (target == null)
			return false;

		await NavigateAsync(target, ct);
		return true;
	}

	public Task GoBackAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (_webView == null || !_webView.CanGoBack())
			return Task.CompletedTask;

		_webView.GoBack();
		return Task.CompletedTask;
	}

	public Task GoForwardAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		if (_webView == null || !_webView.CanGoForward())
			return Task.CompletedTask;

		_webView.GoForward();
		return Task.CompletedTask;
	}

	public Task RefreshAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		_webView?.Reload();
		return Task.CompletedTask;
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

	private Task LoadUrlAsync(string url, bool pushHistory, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		if (_webView == null)
		{
			Console.WriteLine("[Browser] LoadUrlAsync: WebView not attached");
			return Task.CompletedTask;
		}

		if (_isLoading && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase))
			return Task.CompletedTask;

		if (pushHistory)
			PushHistory(url);

		_pendingUrl = url;
		// Optimistic update so the Blazor URL bar and tool results don't wait for LoadFinished.
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
				UpdateNavStateFromWebView(sender);
				_ = RecordVisitAsync();
				break;
		}
	}

	private void UpdateUrlFromWebView(WebKit.WebView webView)
	{
		var url = webView.GetUri() ?? "";
		if (string.IsNullOrWhiteSpace(url) || string.Equals(url, _currentUrl, StringComparison.Ordinal))
			return;

		_currentUrl = url;
		Console.WriteLine($"[Browser] navigated to {url}");
		UrlChanged?.Invoke(_currentUrl);
	}

	private void UpdateNavStateFromWebView(WebKit.WebView webView)
	{
		_canGoBack = webView.CanGoBack();
		_canGoForward = webView.CanGoForward();
	}

	private async Task RecordVisitAsync()
	{
		await _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);
		await UpdateTitleAsync();
	}

	private async Task UpdateTitleAsync()
	{
		var title = await EvaluateScriptAsync("document.title");
		_pageTitle = UnquoteJsString(title);
		TitleChanged?.Invoke(_pageTitle);

		if (!string.IsNullOrWhiteSpace(_currentUrl))
			await _store.AddHistoryEntryAsync(_currentUrl, _pageTitle);
	}

	private static string UnquoteJsString(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return "";

		var trimmed = value.Trim();
		if (trimmed.Length >= 2 && trimmed.StartsWith('"') && trimmed.EndsWith('"'))
			return trimmed[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n");

		return trimmed;
	}

	private void PushHistory(string url)
	{
		if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
			_history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

		if (_history.Count == 0 || !string.Equals(_history[^1], url, StringComparison.OrdinalIgnoreCase))
		{
			_history.Add(url);
			_historyIndex = _history.Count - 1;
		}
		else
		{
			_historyIndex = _history.Count - 1;
		}

		UpdateNavState();
	}

	private void UpdateNavState()
	{
		_canGoBack = _historyIndex > 0;
		_canGoForward = _historyIndex >= 0 && _historyIndex < _history.Count - 1;
	}

	private void SetLoading(bool loading)
	{
		if (_isLoading == loading)
			return;

		_isLoading = loading;
		LoadingChanged?.Invoke(_isLoading);
	}

	private string NormalizeUrl(string input) =>
		BrowserUrlNormalizer.Normalize(input, _store.GetSettings());
}
#endif
