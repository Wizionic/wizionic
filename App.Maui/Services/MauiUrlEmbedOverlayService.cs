using App.Core.UI;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace App.Maui.Services;

/// <summary>
/// Positions a dedicated native WebView over Blazor content for sites that block iframes
/// (Home Assistant X-Frame-Options, etc.).
/// </summary>
public sealed class MauiUrlEmbedOverlayService : IUrlEmbedOverlay
{
    private WebView? _webView;
    private AbsoluteLayout? _layout;
    private string? _currentUrl;
    private string? _pendingUrl;
    private object? _pendingOwner;
    private object? _owner;
    private bool _visible;
    private bool _suppressed;
    private bool _hasLayout;

    public bool IsNative => true;

    public void Attach(WebView webView, AbsoluteLayout layout)
    {
        var pending = _pendingUrl;
        var pendingOwner = _pendingOwner;
        _webView = webView;
        _layout = layout;
        HideCore();
        Console.WriteLine("[UrlEmbed] native overlay attached");

        // Replay a Show() that raced ahead of MainPage.Loaded wiring.
        if (!string.IsNullOrWhiteSpace(pending))
            Show(pending, pendingOwner);
    }

    public void Show(string url, object? owner = null)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Hide(owner);
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null)
            {
                _pendingUrl = url;
                _pendingOwner = owner;
                Console.WriteLine($"[UrlEmbed] Show deferred until overlay attach: {url}");
                return;
            }

            _owner = owner;
            var changed = !string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase);
            _currentUrl = url;
            _visible = true;
            _webView.IsVisible = !_suppressed;
            _webView.IsEnabled = !_suppressed;

            if (changed || _webView.Source is not UrlWebViewSource src
                || !string.Equals(src.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                _webView.Source = new UrlWebViewSource { Url = url };
                Console.WriteLine($"[UrlEmbed] navigate {url}");
            }
        });
    }

    public void Hide(object? owner = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsCurrentOwner(owner))
            {
                Console.WriteLine("[UrlEmbed] Hide ignored — overlay owned by another embed");
                return;
            }

            HideCore();
        });
    }

    public void UpdateBounds(double x, double y, double width, double height, object? owner = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null || _layout == null)
                return;
            if (!IsCurrentOwner(owner))
                return;

            if (!_visible || width < 2 || height < 2)
            {
                if (_visible)
                {
                    // Keep navigation state but collapse until layout is ready.
                    AbsoluteLayout.SetLayoutBounds(_webView, new Rect(0, 0, 0, 0));
                    AbsoluteLayout.SetLayoutFlags(_webView, AbsoluteLayoutFlags.None);
                }
                return;
            }

            var bounds = new Rect(
                Math.Round(x),
                Math.Round(y),
                Math.Round(width),
                Math.Round(height));

            AbsoluteLayout.SetLayoutFlags(_webView, AbsoluteLayoutFlags.None);
            AbsoluteLayout.SetLayoutBounds(_webView, bounds);
            _webView.IsVisible = !_suppressed;
            _webView.IsEnabled = !_suppressed;

            // WebView2 can swallow a navigation that happened while the view was 0×0.
            if (!_hasLayout && !string.IsNullOrWhiteSpace(_currentUrl))
            {
                _webView.Source = new UrlWebViewSource { Url = _currentUrl };
                Console.WriteLine($"[UrlEmbed] re-navigate after first layout {_currentUrl}");
            }
            _hasLayout = true;
        });
    }

    private bool IsCurrentOwner(object? owner) =>
        owner == null || _owner == null || ReferenceEquals(_owner, owner);

    private void HideCore()
    {
        _visible = false;
        _currentUrl = null;
        _pendingUrl = null;
        _pendingOwner = null;
        _owner = null;
        _hasLayout = false;
        if (_webView == null)
            return;

        _webView.IsVisible = false;
        _webView.IsEnabled = false;
        try
        {
            _webView.Source = new UrlWebViewSource { Url = "about:blank" };
        }
        catch
        {
            // ignore
        }

        AbsoluteLayout.SetLayoutBounds(_webView, new Rect(0, 0, 0, 0));
        AbsoluteLayout.SetLayoutFlags(_webView, AbsoluteLayoutFlags.None);
    }

    public void SetSuppressed(bool suppressed)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _suppressed = suppressed;
            if (_webView == null)
                return;
            var show = _visible && !suppressed;
            _webView.IsVisible = show;
            _webView.IsEnabled = show;
        });
    }
}
