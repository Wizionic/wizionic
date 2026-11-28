using System.Globalization;

namespace App.Core.SmartHome;

/// <summary>
/// Resolves light color names and hex strings to Home Assistant <c>rgb_color</c>
/// values. HA integrations are inconsistent with <c>color_name</c> (LIFX turned
/// "purple" into magenta), so the app always sends explicit RGB.
/// </summary>
public static class HomeAssistantColor
{
    private static readonly Dictionary<string, int[]> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = [255, 255, 255],
        ["warm white"] = [255, 214, 170],
        ["cool white"] = [235, 245, 255],
        ["red"] = [255, 0, 0],
        ["green"] = [0, 255, 0],
        ["lime"] = [0, 255, 0],
        ["blue"] = [0, 0, 255],
        ["navy"] = [0, 0, 128],
        ["yellow"] = [255, 255, 0],
        ["orange"] = [255, 165, 0],
        ["purple"] = [128, 0, 255],
        ["violet"] = [128, 0, 255],
        ["magenta"] = [255, 0, 255],
        ["pink"] = [255, 192, 203],
        ["cyan"] = [0, 255, 255],
        ["teal"] = [0, 128, 128],
        ["turquoise"] = [64, 224, 208],
        ["gold"] = [255, 215, 0]
    };

    public static IReadOnlyCollection<string> KnownNames => NamedColors.Keys;

    public static bool TryParse(string? value, out int[] rgb)
    {
        rgb = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.StartsWith('#'))
            return TryParseHex(text, out rgb);

        var key = text.Replace('_', ' ').Replace('-', ' ').Trim();
        while (key.Contains("  ", StringComparison.Ordinal))
            key = key.Replace("  ", " ", StringComparison.Ordinal);

        if (!NamedColors.TryGetValue(key, out var found))
            return false;

        rgb = [found[0], found[1], found[2]];
        return true;
    }

    public static string FormatRgb(IReadOnlyList<int> rgb) =>
        rgb.Count >= 3 ? $"[{rgb[0]},{rgb[1]},{rgb[2]}]" : "[]";

    private static bool TryParseHex(string text, out int[] rgb)
    {
        rgb = [];
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6)
            return false;

        if (!byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return false;

        rgb = [r, g, b];
        return true;
    }
}
