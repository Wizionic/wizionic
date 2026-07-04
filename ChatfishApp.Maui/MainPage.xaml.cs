using ChatfishApp.Core.UI;
using ChatfishApp.Maui.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ChatfishApp.Maui;

public partial class MainPage : ContentPage
{
    private bool _wired;

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

        agent.AttachWebView(browserWebView);
        sideAgent.AttachWebView(browserSideWebView);
        overlay.Initialize(browserWebView, browserSideWebView, rootLayout);
        platform.Attach(browserWebView);

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