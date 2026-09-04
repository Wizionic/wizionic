namespace App.Maui;

/// <summary>
/// Detects a packaged (MSIX) Windows install. Unpackaged / Velopack throws from Package.Current.
/// </summary>
internal static class WindowsPackageInfo
{
    public static bool IsPackaged
    {
        get
        {
            try
            {
                var id = global::Windows.ApplicationModel.Package.Current?.Id;
                return id is not null && !string.IsNullOrEmpty(id.FamilyName);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string? VersionString
    {
        get
        {
            try
            {
                var v = global::Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                return null;
            }
        }
    }
}
