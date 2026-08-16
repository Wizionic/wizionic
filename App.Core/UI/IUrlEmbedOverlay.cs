namespace App.Core.UI;

/// <summary>
/// Optional native WebView host for embedding sites that refuse iframes
/// (e.g. Home Assistant with default X-Frame-Options: SAMEORIGIN).
/// When <see cref="IsNative"/> is false, UI should use an iframe instead.
/// </summary>
public interface IUrlEmbedOverlay
{
    /// <summary>True when a platform native WebView is available (MAUI desktop).</summary>
    bool IsNative { get; }

    /// <summary>Show the overlay and navigate to <paramref name="url"/>.</summary>
    /// <param name="owner">
    /// Caller identity. A later <see cref="Hide"/> or <see cref="UpdateBounds"/> from a
    /// different owner is ignored so disposing Lemonade cannot blank Home Assistant.
    /// </param>
    void Show(string url, object? owner = null);

    /// <summary>Hide and clear the overlay WebView.</summary>
    void Hide(object? owner = null);

    /// <summary>
    /// Position the overlay in device-independent pixels relative to the app AbsoluteLayout
    /// (same coordinate space as browser overlays).
    /// </summary>
    void UpdateBounds(double x, double y, double width, double height, object? owner = null);

    /// <summary>
    /// Temporarily hide the native WebView without unloading it (e.g. while a modal is open).
    /// Default is a no-op for iframe / null hosts.
    /// </summary>
    void SetSuppressed(bool suppressed) { }
}
