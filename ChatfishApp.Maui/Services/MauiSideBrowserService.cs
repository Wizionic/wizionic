using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;
using Microsoft.Maui.Controls;

namespace ChatfishApp.Maui.Services;

public sealed class MauiSideBrowserService : IBrowserSideAgentService
{
    private readonly IBrowserPanelState _panel;
    private readonly IBrowserStore _store;
    private WebView? _webView;

    private string _currentUrl = "";
    private bool _isLoading;

    public MauiSideBrowserService(IBrowserPanelState panel, IBrowserStore store)
    {
        _panel = panel;
        _store = store;
    }

    public bool IsAvailable => _webView != null && _panel.IsOpen;

    public string CurrentUrl => _currentUrl;
    public bool IsLoading => _isLoading;

    public event Action<string>? UrlChanged;
    public event Action<bool>? LoadingChanged;

    public void AttachWebView(WebView webView)
    {
        if (_webView != null)
            DetachWebView();

        _webView = webView;
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

    public Task NavigateAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_webView == null)
            return Task.CompletedTask;

        var normalized = BrowserUrlNormalizer.Normalize(url, _store.GetSettings());
        if (string.IsNullOrEmpty(normalized))
            return Task.CompletedTask;

        if (string.Equals(_currentUrl, normalized, StringComparison.OrdinalIgnoreCase) && !_isLoading)
            return Task.CompletedTask;

        try
        {
            _webView.Source = new UrlWebViewSource { Url = normalized };
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
        if (_webView == null || string.IsNullOrWhiteSpace(_currentUrl))
            return Task.CompletedTask;

        return NavigateAsync(_currentUrl, ct);
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e) => SetLoading(true);

    private void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        SetLoading(false);
        if (e.Result != WebNavigationResult.Success)
            return;

        var url = e.Url ?? "";
        if (string.IsNullOrWhiteSpace(url))
            return;

        _currentUrl = url;
        UrlChanged?.Invoke(_currentUrl);
    }

    private void SetLoading(bool loading)
    {
        if (_isLoading == loading)
            return;

        _isLoading = loading;
        LoadingChanged?.Invoke(_isLoading);
    }
}