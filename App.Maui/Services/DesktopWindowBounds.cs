using System.Text.Json;

namespace App.Maui.Services;

/// <summary>Device-local last size of the primary desktop window (not user-namespaced).</summary>
internal sealed class DesktopWindowBounds
{
    public const string SettingsKey = "app-window-bounds";
    public const int MinWidth = 640;
    public const int MinHeight = 480;

    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public bool Maximized { get; set; }

    public bool HasRestoredSize => W >= MinWidth && H >= MinHeight;

    public static DesktopWindowBounds? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<DesktopWindowBounds>(json);
        }
        catch
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}
