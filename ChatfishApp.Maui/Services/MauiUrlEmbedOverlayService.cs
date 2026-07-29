using ChatfishApp.Core.UI;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Positions a dedicated native WebView over Blazor content for sites that block iframes
/// (Home Assistant X-Frame-Options, etc.).
/// </summary>
public sealed class MauiUrlEmbedOverlayService : IUrlEmbedOverlay
{
    private WebView? _webView;
    private AbsoluteLayout? _layout;
    private string? _currentUrl;
    private bool _visible;

    public bool IsNative => true;

    public void Attach(WebView webView, AbsoluteLayout layout)
    {
        _webView = webView;
        _layout = layout;
        HideCore();
        Console.WriteLine("[UrlEmbed] native overlay attached");
    }

    public void Show(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            Hide();
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null)
                return;

            var changed = !string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase);
            _currentUrl = url;
            _visible = true;
            _webView.IsVisible = true;
            _webView.IsEnabled = true;

            if (changed || _webView.Source is not UrlWebViewSource src
                || !string.Equals(src.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                _webView.Source = new UrlWebViewSource { Url = url };
                Console.WriteLine($"[UrlEmbed] navigate {url}");
            }
        });
    }

    public void Hide() => MainThread.BeginInvokeOnMainThread(HideCore);

    public void UpdateBounds(double x, double y, double width, double height)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null || _layout == null)
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
            _webView.IsVisible = true;
            _webView.IsEnabled = true;
        });
    }

    private void HideCore()
    {
        _visible = false;
        _currentUrl = null;
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
}
