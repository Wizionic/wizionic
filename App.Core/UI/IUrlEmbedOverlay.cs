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
    void Show(string url);

    /// <summary>Hide and clear the overlay WebView.</summary>
    void Hide();

    /// <summary>
    /// Position the overlay in device-independent pixels relative to the app AbsoluteLayout
    /// (same coordinate space as browser overlays).
    /// </summary>
    void UpdateBounds(double x, double y, double width, double height);
}
