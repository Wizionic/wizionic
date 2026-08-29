namespace App.Core.Auth;

/// <summary>
/// Shared localStorage / settings keys so auth and sync use the same device id.
/// </summary>
public static class ClientDeviceKeys
{
    public const string DeviceId = "app-device-id";
    public const string DeviceName = "app-device-name";

    public const string IdHeader = "X-Wizionic-Device-Id";
    public const string NameHeader = "X-Wizionic-Device-Name";
    public const string SessionClaimType = "sid";

    /// <summary>
    /// HTTP headers must be ASCII. MAUI default names include "•".
    /// </summary>
    public static string? EncodeNameHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > 80)
            trimmed = trimmed[..80];
        return Uri.EscapeDataString(trimmed);
    }
}
