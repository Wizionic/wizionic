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
    /// <summary>Windows Firewall inbound rule (Private profile, TCP 5150).</summary>
    public const string FirewallRuleName = "Wizionic Home Server";

    public const string LatestManifestFile = "latest.json";

    /// <summary>
    /// Relative feed path under the public site root (platform-specific package).
    /// </summary>
    public static string ReleasesFeedPath =>
        OperatingSystem.IsLinux()
            ? "releases/homeserver/linux"
            : "releases/homeserver/windows";

    /// <summary>
    /// Writable root that survives app updates. Desktop uninstall removes this tree
    /// so a reinstall is a first run (best-effort; admin-owned files may need a delayed delete).
    /// Windows: %ProgramData%\Wizionic\Homeserver when this user can write it;
    /// otherwise %LocalAppData%\Wizionic\Homeserver (a new Windows user cannot replace
    /// another account's ProgramData install).
    /// Linux:   ~/.local/share/Wizionic/Homeserver  (user-local; works without root)
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return LinuxRootDirectory;

            return _windowsRoot ??= ResolveWindowsRoot();
        }
    }

    private static string? _windowsRoot;

    public static string LinuxRootDirectory
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(local, "Wizionic", "Homeserver");
        }
    }

    public static string WindowsProgramDataRoot
    {
        get
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common))
                common = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(common, "Wizionic", "Homeserver");
        }
    }

    public static string WindowsUserLocalRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Wizionic", "Homeserver");
        }
    }

    /// <summary>All known install roots to wipe on uninstall (ProgramData and per-user).</summary>
    public static IReadOnlyList<string> AllRootDirectories
    {
        get
        {
            if (OperatingSystem.IsLinux())
                return [LinuxRootDirectory];
            return [WindowsProgramDataRoot, WindowsUserLocalRoot];
        }
    }

    private static string ResolveWindowsRoot()
    {
        if (IsWritableDirectory(WindowsProgramDataRoot))
            return WindowsProgramDataRoot;
        return WindowsUserLocalRoot;
    }

    private static bool IsWritableDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".wizionic-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            // Folder Write is not enough when another Windows user owns state/binaries.
            var state = Path.Combine(dir, "state.json");
            if (File.Exists(state))
            {
                using var fs = File.Open(state, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }

            var exe = Path.Combine(dir, "app", OperatingSystem.IsWindows() ? "App.exe" : "App");
            if (File.Exists(exe))
            {
                using var fs = File.Open(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return true;
        }
        catch
        {
            return false;
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
