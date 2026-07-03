using ChatfishApp.Core.Browser;
using Microsoft.Maui.Controls;

namespace ChatfishApp.Maui.Services;

/// <summary>Platform hooks for the embedded browser WebView (new windows, downloads, clear data).</summary>
public sealed class BrowserWebViewPlatformService
{
    private readonly IBrowserStore _store;
    private readonly MauiBrowserAgentService _agent;
    private WebView? _webView;
    private bool _configured;

#if WINDOWS
    private Microsoft.Web.WebView2.Core.CoreWebView2? _core;
#endif

    public BrowserWebViewPlatformService(IBrowserStore store, MauiBrowserAgentService agent)
    {
        _store = store;
        _agent = agent;
    }

    public void Attach(WebView webView)
    {
        _webView = webView;
        _configured = false;
        webView.HandlerChanged += OnHandlerChanged;
        OnHandlerChanged(webView, EventArgs.Empty);
    }

    public async Task ApplyClearOnExitAsync(CancellationToken ct = default)
    {
        var settings = _store.GetSettings();

#if WINDOWS
        if (_core?.Profile != null && (settings.ClearCookiesOnExit || settings.ClearCacheOnExit))
        {
            try
            {
                Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds dataKinds = 0;
                if (settings.ClearCookiesOnExit)
                    dataKinds |= Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.Cookies;
                if (settings.ClearCacheOnExit)
                    dataKinds |= Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DiskCache
                        | Microsoft.Web.WebView2.Core.CoreWebView2BrowsingDataKinds.DownloadHistory;

                if (dataKinds != 0)
                    await _core.Profile.ClearBrowsingDataAsync(dataKinds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Browser] clear browsing data failed: {ex.Message}");
            }
        }
#endif

        if (settings.ClearHistoryOnExit)
            await _store.ClearHistoryAsync(ct);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        if (_configured || _webView?.Handler?.PlatformView == null)
            return;

#if WINDOWS
        if (_webView.Handler.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 wv2)
        {
            _ = ConfigureWindowsAsync(wv2);
            _configured = true;
        }
#endif
    }

#if WINDOWS
    private async Task ConfigureWindowsAsync(Microsoft.UI.Xaml.Controls.WebView2 wv2)
    {
        try
        {
            await wv2.EnsureCoreWebView2Async();
            _core = wv2.CoreWebView2;
            if (_core == null)
                return;

            _core.NewWindowRequested += (sender, args) =>
            {
                var target = args.Uri ?? "";
                args.Handled = true;

                var behavior = _store.GetSettings().NewWindowBehavior;
                if (behavior == BrowserNewWindowBehavior.ExternalBrowser && !string.IsNullOrWhiteSpace(target))
                {
                    _ = Launcher.Default.OpenAsync(new Uri(target));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(target))
                    _ = _agent.NavigateAsync(target);
            };

            _core.DownloadStarting += (sender, args) =>
            {
                if (_store.GetSettings().AskBeforeDownloading)
                {
                    args.Handled = true;
                    args.Cancel = true;
                    Console.WriteLine($"[Browser] download blocked (ask first): {args.DownloadOperation.Uri}");
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Browser] WebView2 configure failed: {ex.Message}");
        }
    }
#endif
}