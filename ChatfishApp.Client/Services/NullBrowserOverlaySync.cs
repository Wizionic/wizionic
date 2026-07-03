using ChatfishApp.Core.Browser;

namespace ChatfishApp.Client.Services;

public sealed class NullBrowserOverlaySync : IBrowserOverlaySync
{
    public void ReportBounds(double x, double y, double width, double height) { }
    public void SetOverlayVisible(bool visible) { }
    public void RestoreCachedOverlay() { }
}