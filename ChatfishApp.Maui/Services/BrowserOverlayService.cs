using ChatfishApp.Core.Browser;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace ChatfishApp.Maui.Services;

public sealed class BrowserOverlayService : IBrowserOverlaySync
{
    private WebView? _mainWebView;
    private WebView? _sideWebView;
    private AbsoluteLayout? _layout;
    private Rect _lastMainBounds = Rect.Zero;
    private Rect _lastSideBounds = Rect.Zero;
    private bool _mainVisible;
    private bool _sideVisible;

    public void Initialize(WebView mainWebView, WebView sideWebView, AbsoluteLayout layout)
    {
        _mainWebView = mainWebView;
        _sideWebView = sideWebView;
        _layout = layout;
        _mainWebView.IsVisible = false;
        _mainWebView.IsEnabled = false;
        _sideWebView.IsVisible = false;
        _sideWebView.IsEnabled = false;
        Console.WriteLine("[Browser] dual overlay service initialized");
    }

    public void ReportMainBounds(double x, double y, double width, double height) =>
        ReportBounds(isMain: true, x, y, width, height);

    public void ReportSideBounds(double x, double y, double width, double height) =>
        ReportBounds(isMain: false, x, y, width, height);

    public void SetMainOverlayVisible(bool visible) => SetOverlayVisible(isMain: true, visible);

    public void SetSideOverlayVisible(bool visible) => SetOverlayVisible(isMain: false, visible);

    public void RestoreCachedOverlay()
    {
        RestoreCached(isMain: true);
        RestoreCached(isMain: false);
    }

    private void ReportBounds(bool isMain, double x, double y, double width, double height)
    {
        var webView = isMain ? _mainWebView : _sideWebView;
        if (webView == null || _layout == null)
            return;

        var rounded = new Rect(
            Math.Round(x),
            Math.Round(y),
            Math.Round(width),
            Math.Round(height));

        if (rounded.Width <= 1 || rounded.Height <= 1)
        {
            if (isMain ? _mainVisible : _sideVisible)
                SetOverlayVisible(isMain, false);
            return;
        }

        var lastBounds = isMain ? _lastMainBounds : _lastSideBounds;
        var visible = isMain ? _mainVisible : _sideVisible;
        if (rounded == lastBounds && visible)
            return;

        if (isMain)
            _lastMainBounds = rounded;
        else
            _lastSideBounds = rounded;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (webView == null)
                return;

            webView.IsVisible = true;
            webView.IsEnabled = true;
            if (isMain)
                _mainVisible = true;
            else
                _sideVisible = true;

            AbsoluteLayout.SetLayoutBounds(webView, rounded);
            AbsoluteLayout.SetLayoutFlags(webView, AbsoluteLayoutFlags.None);
        });
    }

    private void SetOverlayVisible(bool isMain, bool show)
    {
        var webView = isMain ? _mainWebView : _sideWebView;
        if (webView == null)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (isMain)
                _mainVisible = show;
            else
                _sideVisible = show;

            webView.IsVisible = show;
            webView.IsEnabled = show;
            if (!show)
                AbsoluteLayout.SetLayoutBounds(webView, new Rect(0, 0, 0, 0));
        });
    }

    private void RestoreCached(bool isMain)
    {
        var webView = isMain ? _mainWebView : _sideWebView;
        var bounds = isMain ? _lastMainBounds : _lastSideBounds;
        if (webView == null || bounds.Width <= 1 || bounds.Height <= 1)
            return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (webView == null)
                return;

            if (isMain)
                _mainVisible = true;
            else
                _sideVisible = true;

            webView.IsVisible = true;
            webView.IsEnabled = true;
            AbsoluteLayout.SetLayoutBounds(webView, bounds);
            AbsoluteLayout.SetLayoutFlags(webView, AbsoluteLayoutFlags.None);
        });
    }
}