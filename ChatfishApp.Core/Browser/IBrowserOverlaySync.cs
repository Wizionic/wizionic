namespace ChatfishApp.Core.Browser;

/// <summary>
/// Reports Blazor-measured browser content bounds to the native WebView overlay (MAUI target).
/// </summary>
public interface IBrowserOverlaySync
{
    void ReportBounds(double x, double y, double width, double height);
    void SetOverlayVisible(bool visible);
    void RestoreCachedOverlay();
}