using App.Core.Homeserver;
using App.Core.UI;
using App.Maui.Services;
using Microsoft.Extensions.DependencyInjection;

namespace App.Maui;

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
            _ = RunHomeserverLifecycleAsync(services);
        }
    }

    /// <summary>
    /// Bind existing Home Server installs to the LAN, then after a Velopack update
    /// refresh homeserver binaries if installed. First-run onboarding is SetupWizard.
    /// </summary>
    private static async Task RunHomeserverLifecycleAsync(IServiceProvider services)
    {
        try
        {
            var homeserver = services.GetService<IHomeserverInstallService>();
            if (homeserver is null || !homeserver.IsSupported)
                return;

            var state = homeserver.GetState();
            if (state.IsInstalled)
            {
                Console.WriteLine("[Homeserver] Ensuring LAN bind and firewall…");
                var lan = await homeserver.EnsureLanAccessAsync();
                Console.WriteLine($"[Homeserver] {lan.Message}");
            }

            if (!(homeserver.PendingUpdateCheck || File.Exists(HomeserverPaths.PendingUpdateFlagPath)))
                return;

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

#if WINDOWS
        // Hide-to-tray must not look like Quit for the embedded browser.
        var desktop = services.GetService<WindowsDesktopHost>();
        if (desktop is not null && !desktop.IsQuitRequested)
        {
            Console.WriteLine("[Desktop] skipping browser clear-on-exit (not quitting)");
            return;
        }
#endif

        var platform = services.GetRequiredService<BrowserWebViewPlatformService>();
        await platform.ApplyClearOnExitAsync();
    }
}
