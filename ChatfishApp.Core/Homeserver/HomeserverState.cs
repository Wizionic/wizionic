using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatfishApp.Core.Homeserver;

public enum HomeserverInstallMode
{
    /// <summary>Not installed and user has not been asked (or first-run not yet handled).</summary>
    Unknown = 0,
    /// <summary>User declined the homeserver prompt.</summary>
    Declined = 1,
    /// <summary>Running as a Windows Service (auto-start).</summary>
    WindowsService = 2,
    /// <summary>User-session fallback (Startup folder / logon autostart).</summary>
    UserSession = 3,
    /// <summary>Running as a Linux systemd unit (auto-start).</summary>
    Systemd = 4
}

public sealed class HomeserverState
{
    [JsonPropertyName("installMode")]
    public HomeserverInstallMode InstallMode { get; set; } = HomeserverInstallMode.Unknown;

    [JsonPropertyName("installedVersion")]
    public string? InstalledVersion { get; set; }

    [JsonPropertyName("port")]
    public string Port { get; set; } = HomeserverPaths.DefaultPort;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = HomeserverPaths.DefaultBaseUrl;

    [JsonPropertyName("askedAt")]
    public DateTimeOffset? AskedAt { get; set; }

    [JsonPropertyName("installedAt")]
    public DateTimeOffset? InstalledAt { get; set; }

    [JsonPropertyName("declinedAt")]
    public DateTimeOffset? DeclinedAt { get; set; }

    /// <summary>When the full-screen setup wizard was completed or dismissed.</summary>
    [JsonPropertyName("onboardingCompletedAt")]
    public DateTimeOffset? OnboardingCompletedAt { get; set; }

    [JsonIgnore]
    public bool IsInstalled =>
        InstallMode is HomeserverInstallMode.WindowsService
            or HomeserverInstallMode.UserSession
            or HomeserverInstallMode.Systemd;

    [JsonIgnore]
    public bool HasDecided =>
        InstallMode is HomeserverInstallMode.Declined
            or HomeserverInstallMode.WindowsService
            or HomeserverInstallMode.UserSession
            or HomeserverInstallMode.Systemd;

    [JsonIgnore]
    public bool OnboardingCompleted => OnboardingCompletedAt.HasValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static HomeserverState Load()
    {
        try
        {
            var path = HomeserverPaths.StateFilePath;
            if (!File.Exists(path))
                return new HomeserverState();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<HomeserverState>(json, JsonOptions) ?? new HomeserverState();
        }
        catch
        {
            return new HomeserverState();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(HomeserverPaths.RootDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(HomeserverPaths.StateFilePath, json);
    }
}

/// <summary>Manifest published next to the homeserver zip on the update feed.</summary>
public sealed class HomeserverFeedManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
