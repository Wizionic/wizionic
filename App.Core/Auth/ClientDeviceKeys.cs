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
}
