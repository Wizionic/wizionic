namespace ChatfishApp.Core.Browser;

/// <summary>
/// Reports Blazor-measured browser content bounds to native WebView overlays (MAUI target).
/// </summary>
public interface IBrowserOverlaySync
{
    void ReportMainBounds(double x, double y, double width, double height);
    void ReportSideBounds(double x, double y, double width, double height);
    void SetMainOverlayVisible(bool visible);
    void SetSideOverlayVisible(bool visible);
    void RestoreCachedOverlay();
}