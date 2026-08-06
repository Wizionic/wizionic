using App.Core.UI;

namespace App.Shared.Services;

public sealed class NullUrlEmbedOverlay : IUrlEmbedOverlay
{
    public static readonly NullUrlEmbedOverlay Instance = new();

    private NullUrlEmbedOverlay() { }

    public bool IsNative => false;

    public void Show(string url) { }

    public void Hide() { }

    public void UpdateBounds(double x, double y, double width, double height) { }
}
