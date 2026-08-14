using App.Core.UI;

namespace App.Shared.Services;

public sealed class NullUrlEmbedOverlay : IUrlEmbedOverlay
{
    public static readonly NullUrlEmbedOverlay Instance = new();

    private NullUrlEmbedOverlay() { }

    public bool IsNative => false;

    public void Show(string url, object? owner = null) { }

    public void Hide(object? owner = null) { }

    public void UpdateBounds(double x, double y, double width, double height, object? owner = null) { }
}
