#if LINUX_DESKTOP
using App.Core.Browser;
using App.Core.UI;

namespace App.Maui.Services.Linux;

/// <summary>
/// Side-panel WebKit browser agent for Linux desktop.
/// </summary>
public sealed class LinuxSideBrowserService : IBrowserSideAgentService
{
	private readonly IBrowserPanelState _panel;
	private readonly IBrowserStore _store;
	private WebKit.WebView? _webView;
	private string _currentUrl = "";
	private bool _isLoading;

	private GObject.SignalHandler<WebKit.WebView, WebKit.WebView.LoadChangedSignalArgs>? _loadChangedHandler;

	public LinuxSideBrowserService(IBrowserPanelState panel, IBrowserStore store)
	{
		_panel = panel;
		_store = store;
	}

	public bool IsAvailable => _webView != null && _panel.IsOpen;
	public string CurrentUrl => _currentUrl;
	public bool IsLoading => _isLoading;

	public event Action<string>? UrlChanged;
	public event Action<bool>? LoadingChanged;

	public void AttachWebView(WebKit.WebView webView)
	{
		if (_webView != null)
			DetachWebView();

		_webView = webView;
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
		if (_webView == null)
			return Task.CompletedTask;

		var normalized = BrowserUrlNormalizer.Normalize(url, _store.GetSettings());
		if (string.IsNullOrEmpty(normalized))
			return Task.CompletedTask;

		try
		{
			SetLoading(true);
			_webView.LoadUri(normalized);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Browser/Side] navigation error: {ex.Message}");
			SetLoading(false);
		}

		return Task.CompletedTask;
	}

	public Task RefreshAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		_webView?.Reload();
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
			case WebKit.LoadEvent.Finished:
				var uri = sender.GetUri() ?? "";
				if (!string.IsNullOrWhiteSpace(uri) && !string.Equals(uri, _currentUrl, StringComparison.Ordinal))
				{
					_currentUrl = uri;
					UrlChanged?.Invoke(_currentUrl);
				}

				if (args.LoadEvent == WebKit.LoadEvent.Finished)
					SetLoading(false);
				break;
		}
	}

	private void SetLoading(bool loading)
	{
		if (_isLoading == loading)
			return;
		_isLoading = loading;
		LoadingChanged?.Invoke(_isLoading);
	}
}
#endif
