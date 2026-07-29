using ChatfishApp.Core.Homeserver;
using ChatfishApp.Core.UI;
using ChatfishApp.Maui.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ChatfishApp.Maui;

public partial class MainPage : ContentPage
{
    private bool _wired;
    private bool _homeserverLifecycleStarted;

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object? sender, EventArgs e)
    {
        if (_wired)
            return;

        var services = Handler?.MauiContext?.Services;
        if (services == null)
            return;

        var agent = services.GetRequiredService<MauiBrowserAgentService>();
        var sideAgent = services.GetRequiredService<MauiSideBrowserService>();
        var overlay = services.GetRequiredService<BrowserOverlayService>();
        var panel = services.GetRequiredService<IBrowserPanelState>();
        var platform = services.GetRequiredService<BrowserWebViewPlatformService>();
        var urlEmbed = services.GetService<MauiUrlEmbedOverlayService>();

        agent.AttachWebView(browserWebView);
        sideAgent.AttachWebView(browserSideWebView);
        overlay.Initialize(browserWebView, browserSideWebView, rootLayout);
        platform.Attach(browserWebView);
        urlEmbed?.Attach(urlEmbedWebView, rootLayout);

        panel.OnChanged += () =>
        {
            if (panel.IsOpen)
                overlay.RestoreCachedOverlay();
            else
            {
                Console.WriteLine("[Browser] panel closed — hiding overlay");
                overlay.SetMainOverlayVisible(false);
                overlay.SetSideOverlayVisible(false);
            }
        };

        _wired = true;
        Loaded -= OnPageLoaded;
        Console.WriteLine("[Browser] MainPage wiring complete");

        if (!_homeserverLifecycleStarted)
        {
            _homeserverLifecycleStarted = true;
            _ = RunPostUpdateHomeserverRefreshAsync(services);
        }
    }

    /// <summary>
    /// After Velopack update only: refresh homeserver binaries if installed.
    /// First-run onboarding is the full-screen SetupWizard (no alerts).
    /// </summary>
    private static async Task RunPostUpdateHomeserverRefreshAsync(IServiceProvider services)
    {
        try
        {
            var homeserver = services.GetService<IHomeserverInstallService>();
            if (homeserver is null || !homeserver.IsSupported)
                return;

            if (!(homeserver.PendingUpdateCheck || File.Exists(HomeserverPaths.PendingUpdateFlagPath)))
                return;

            var state = homeserver.GetState();
            if (state.IsInstalled)
            {
                Console.WriteLine("[Homeserver] Checking for Home Server update after app update…");
                var updateResult = await homeserver.UpdateIfNeededAsync();
                Console.WriteLine($"[Homeserver] {updateResult.Message}");
            }
            else
            {
                homeserver.PendingUpdateCheck = false;
                try
                {
                    if (File.Exists(HomeserverPaths.PendingUpdateFlagPath))
                        File.Delete(HomeserverPaths.PendingUpdateFlagPath);
                }
                catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Homeserver] Lifecycle error: {ex}");
        }
    }

    private async void OnPageUnloaded(object? sender, EventArgs e)
    {
        var services = Handler?.MauiContext?.Services;
        if (services == null)
            return;

        var platform = services.GetRequiredService<BrowserWebViewPlatformService>();
        await platform.ApplyClearOnExitAsync();
    }
}
