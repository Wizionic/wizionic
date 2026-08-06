namespace App.Core.Homeserver;

/// <summary>
/// Stable filesystem layout for a Windows / Linux Wizionic Home Server install.
/// Data never lives under the replaceable app binary directory.
/// </summary>
public static class HomeserverPaths
{
    public const string ServiceName = "WizionicHomeServer";
    public const string ServiceDisplayName = "Wizionic Home Server";
    /// <summary>systemd unit name (without .service).</summary>
    public const string SystemdUnitName = "wizionic-homeserver";
    public const string DefaultPort = "5150";
    public const string DefaultBaseUrl = "http://localhost:5150";

    public const string LatestManifestFile = "latest.json";

    /// <summary>
    /// Relative feed path under the public site root (platform-specific package).
    /// </summary>
    public static string ReleasesFeedPath =>
        OperatingSystem.IsLinux()
            ? "releases/homeserver/linux"
            : "releases/homeserver/windows";

    /// <summary>
    /// Writable root that survives app updates and uninstall of binaries.
    /// Windows: %ProgramData%\Wizionic\Homeserver
    /// Linux:   ~/.local/share/Wizionic/Homeserver  (user-local; works without root)
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            if (OperatingSystem.IsLinux())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local))
                    local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                return Path.Combine(local, "Wizionic", "Homeserver");
            }

            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common))
                common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wizionic");
            return Path.Combine(common, "Wizionic", "Homeserver");
        }
    }

    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string DatabasePath => Path.Combine(DataDirectory, "homeserver.db");
    public static string StateFilePath => Path.Combine(RootDirectory, "state.json");
    public static string AppsettingsPath => Path.Combine(RootDirectory, "appsettings.Homeserver.json");
    public static string PendingUpdateFlagPath => Path.Combine(RootDirectory, "pending-update.flag");

    /// <summary>Replaceable published host binaries (self-contained).</summary>
    public static string AppDirectory => Path.Combine(RootDirectory, "app");

    /// <summary>Published host entrypoint (App.exe on Windows, App on Linux).</summary>
    public static string HostExecutablePath =>
        Path.Combine(AppDirectory, OperatingSystem.IsWindows() ? "App.exe" : "App");

    public static string SqliteConnectionString =>
        "Data Source=" + DatabasePath.Replace('\\', '/');

    /// <summary>Linux user autostart desktop entry path.</summary>
    public static string LinuxAutostartDesktopPath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "wizionic-homeserver.desktop");
        }
    }

    /// <summary>Temp path used when writing a systemd unit before elevated install.</summary>
    public static string SystemdUnitFileName => $"{SystemdUnitName}.service";
}
