using App.Core.Browser;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace App.Maui.Services;

public sealed class BrowserOverlayService : IBrowserOverlaySync
{
    private WebView? _mainWebView;
    private WebView? _sideWebView;
    private AbsoluteLayout? _layout;
    private Rect _lastMainBounds = Rect.Zero;
    private Rect _lastSideBounds = Rect.Zero;
    private bool _mainVisible;
    private bool _sideVisible;
    private bool _htmlFullscreen;
    private bool _sideWasVisibleBeforeFullscreen;

    public bool IsHtmlFullscreen => _htmlFullscreen;

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

    /// <summary>
    /// Expand the main WebView over the entire AbsoluteLayout (and typically pair with OS fullscreen)
    /// so HTML5 video/fullscreen elements cover the whole app, not just the browser content host.
    /// </summary>
    public void EnterHtmlFullscreen()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_mainWebView == null || _layout == null)
                return;

            _htmlFullscreen = true;
            _sideWasVisibleBeforeFullscreen = _sideVisible;
            if (_sideWebView != null)
            {
                _sideVisible = false;
                _sideWebView.IsVisible = false;
                _sideWebView.IsEnabled = false;
            }

            _mainVisible = true;
            _mainWebView.IsVisible = true;
            _mainWebView.IsEnabled = true;

            // Fill the host AbsoluteLayout (window client area under Blazor).
            AbsoluteLayout.SetLayoutBounds(_mainWebView, new Rect(0, 0, 1, 1));
            AbsoluteLayout.SetLayoutFlags(_mainWebView, AbsoluteLayoutFlags.All);
            // Z-order: main WebView is already declared after BlazorWebView in MainPage.xaml.

            Console.WriteLine("[Browser] HTML fullscreen: main WebView expanded to fill layout");
        });
    }

    public void ExitHtmlFullscreen()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_htmlFullscreen)
                return;

            _htmlFullscreen = false;
            if (_mainWebView == null)
                return;

            // Restore last known browser-content bounds.
            if (_lastMainBounds.Width > 1 && _lastMainBounds.Height > 1)
            {
                AbsoluteLayout.SetLayoutBounds(_mainWebView, _lastMainBounds);
                AbsoluteLayout.SetLayoutFlags(_mainWebView, AbsoluteLayoutFlags.None);
                _mainWebView.IsVisible = true;
                _mainWebView.IsEnabled = true;
                _mainVisible = true;
            }
            else
            {
                _mainVisible = false;
                _mainWebView.IsVisible = false;
                _mainWebView.IsEnabled = false;
                AbsoluteLayout.SetLayoutBounds(_mainWebView, new Rect(0, 0, 0, 0));
            }

            if (_sideWasVisibleBeforeFullscreen && _sideWebView != null
                && _lastSideBounds.Width > 1 && _lastSideBounds.Height > 1)
            {
                _sideVisible = true;
                _sideWebView.IsVisible = true;
                _sideWebView.IsEnabled = true;
                AbsoluteLayout.SetLayoutBounds(_sideWebView, _lastSideBounds);
                AbsoluteLayout.SetLayoutFlags(_sideWebView, AbsoluteLayoutFlags.None);
            }

            Console.WriteLine("[Browser] HTML fullscreen exited — restored overlay bounds");
        });
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

        // Always remember intended chrome bounds so we can restore after HTML fullscreen.
        if (rounded.Width > 1 && rounded.Height > 1)
        {
            if (isMain)
            {
                if (_lastMainBounds == rounded && _mainVisible && !_htmlFullscreen)
                    return;
                _lastMainBounds = rounded;
            }
            else
            {
                if (_lastSideBounds == rounded && _sideVisible)
                    return;
                _lastSideBounds = rounded;
            }
        }

        // While HTML fullscreen is active, keep the main WebView covering the full layout.
        if (_htmlFullscreen && isMain)
            return;

        if (rounded.Width <= 1 || rounded.Height <= 1)
        {
            if (isMain ? _mainVisible : _sideVisible)
                SetOverlayVisible(isMain, false);
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (webView == null || (_htmlFullscreen && isMain))
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
            // Do not hide/shrink the main WebView while HTML fullscreen is active.
            if (_htmlFullscreen && isMain && !show)
                return;

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