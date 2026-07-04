using System.Text.Json;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.UI;
using Microsoft.Maui.Controls;

namespace ChatfishApp.Maui.Services;

public sealed class MauiBrowserAgentService : IBrowserAgentService
{
    private readonly IBrowserPanelState _panel;
    private readonly IBrowserStore _store;
    private WebView? _webView;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string? _pendingUrl;

    private string _currentUrl = "";
    private string _pageTitle = "";
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;

    public MauiBrowserAgentService(IBrowserPanelState panel, IBrowserStore store)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public bool IsAvailable => _webView != null && _panel.IsOpen;

    public string CurrentUrl => _currentUrl;
    public string PageTitle => _pageTitle;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;
    public bool IsLoading => _isLoading;

    public event Action<string>? UrlChanged;
    public event Action<string>? TitleChanged;
    public event Action<bool>? LoadingChanged;

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
        if (!_canGoBack || _historyIndex <= 0)
            return Task.CompletedTask;

        _historyIndex--;
        UpdateNavState();
        return LoadUrlAsync(_history[_historyIndex], pushHistory: false, ct);
    }

    public Task GoForwardAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_canGoForward || _historyIndex >= _history.Count - 1)
            return Task.CompletedTask;

        _historyIndex++;
        UpdateNavState();
        return LoadUrlAsync(_history[_historyIndex], pushHistory: false, ct);
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_webView == null || string.IsNullOrWhiteSpace(_currentUrl))
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

    private Task LoadUrlAsync(string url, bool pushHistory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_webView == null)
            return Task.CompletedTask;

        if (_isLoading && string.Equals(_pendingUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[Browser] skip duplicate navigation to {url}");
            return Task.CompletedTask;
        }

        if (string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase) && !_isLoading)
        {
            Console.WriteLine($"[Browser] already at {url}");
            return Task.CompletedTask;
        }

        if (pushHistory)
            PushHistory(url);

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

        _currentUrl = url;
        Console.WriteLine($"[Browser] navigated to {url}");
        UrlChanged?.Invoke(_currentUrl);
        SetLoading(false);
        _ = RecordVisitAsync();
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