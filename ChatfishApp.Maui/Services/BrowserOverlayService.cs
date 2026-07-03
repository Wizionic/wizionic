using ChatfishApp.Core.Browser;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace ChatfishApp.Maui.Services;

public sealed class BrowserOverlayService : IBrowserOverlaySync
{
    private WebView? _webView;
    private AbsoluteLayout? _layout;
    private Rect _lastBounds = Rect.Zero;
    private bool _visible;

    public void Initialize(WebView webView, AbsoluteLayout layout)
    {
        _webView = webView;
        _layout = layout;
        _webView.IsVisible = false;
        _webView.IsEnabled = false;
        Console.WriteLine("[Browser] overlay service initialized");
    }

    public void ReportBounds(double x, double y, double width, double height)
    {
        if (_webView == null || _layout == null)
            return;

        var rounded = new Rect(
            Math.Round(x),
            Math.Round(y),
            Math.Round(width),
            Math.Round(height));

        if (rounded.Width <= 1 || rounded.Height <= 1)
        {
            if (_visible)
            {
                Console.WriteLine("[Browser] overlay hidden — invalid bounds");
                SetOverlayVisible(false);
            }
            return;
        }

        if (rounded == _lastBounds && _visible)
            return;

        _lastBounds = rounded;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null)
                return;

            _webView.IsVisible = true;
            _webView.IsEnabled = true;
            _visible = true;
            AbsoluteLayout.SetLayoutBounds(_webView, rounded);
            AbsoluteLayout.SetLayoutFlags(_webView, AbsoluteLayoutFlags.None);
        });
    }

    public void SetOverlayVisible(bool visible)
    {
        if (_webView == null)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            _visible = visible;
            _webView.IsVisible = visible;
            _webView.IsEnabled = visible;
            if (!visible)
                AbsoluteLayout.SetLayoutBounds(_webView, new Rect(0, 0, 0, 0));
        });
    }

    public void RestoreCachedOverlay()
    {
        if (_webView == null || _layout == null || _lastBounds.Width <= 1 || _lastBounds.Height <= 1)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_webView == null)
                return;

            _visible = true;
            _webView.IsVisible = true;
            _webView.IsEnabled = true;
            AbsoluteLayout.SetLayoutBounds(_webView, _lastBounds);
            AbsoluteLayout.SetLayoutFlags(_webView, AbsoluteLayoutFlags.None);
            Console.WriteLine($"[Browser] overlay restored from cache at {_lastBounds.Width}x{_lastBounds.Height}");
        });
    }
}