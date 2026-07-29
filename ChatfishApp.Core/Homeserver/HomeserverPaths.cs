namespace ChatfishApp.Core.Homeserver;

/// <summary>
/// Stable filesystem layout for a Windows (later Linux) Chatfish Home Server install.
/// Data never lives under the replaceable app binary directory.
/// </summary>
public static class HomeserverPaths
{
    public const string ServiceName = "ChatfishHomeServer";
    public const string ServiceDisplayName = "Chatfish Home Server";
    public const string DefaultPort = "5050";
    public const string DefaultBaseUrl = "http://localhost:5050";

    /// <summary>Relative feed path under the public site root.</summary>
    public const string ReleasesFeedPath = "releases/homeserver/windows";

    public const string LatestManifestFile = "latest.json";

    /// <summary>
    /// ProgramData root (Windows) / equivalent common data. Survives app updates and uninstall of binaries.
    /// </summary>
    public static string RootDirectory
    {
        get
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(common))
                common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chatfish");
            return Path.Combine(common, "Chatfish", "Homeserver");
        }
    }

    public static string DataDirectory => Path.Combine(RootDirectory, "data");
    public static string DatabasePath => Path.Combine(DataDirectory, "chatfish.db");
    public static string StateFilePath => Path.Combine(RootDirectory, "state.json");
    public static string AppsettingsPath => Path.Combine(RootDirectory, "appsettings.Homeserver.json");
    public static string PendingUpdateFlagPath => Path.Combine(RootDirectory, "pending-update.flag");

    /// <summary>Replaceable published host binaries (self-contained).</summary>
    public static string AppDirectory => Path.Combine(RootDirectory, "app");

    public static string HostExecutablePath => Path.Combine(AppDirectory, "ChatfishApp.exe");

    public static string SqliteConnectionString =>
        "Data Source=" + DatabasePath.Replace('\\', '/');
}
