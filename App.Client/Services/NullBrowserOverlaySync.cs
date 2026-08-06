using App.Core.Browser;

namespace App.Client.Services;

public sealed class NullBrowserOverlaySync : IBrowserOverlaySync
{
    public void ReportMainBounds(double x, double y, double width, double height) { }
    public void ReportSideBounds(double x, double y, double width, double height) { }
    public void SetMainOverlayVisible(bool visible) { }
    public void SetSideOverlayVisible(bool visible) { }
    public void RestoreCachedOverlay() { }
}