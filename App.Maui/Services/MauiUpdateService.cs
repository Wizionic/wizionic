using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using App.Core.Configuration;
using App.Core.Homeserver;

using App.Core.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace App.Maui.Services;

/// <summary>
/// Velopack-based auto-updater for the MAUI desktop target.
/// Reads the update feed URL from AppServer options in appsettings.json.
/// When a local Home Server is installed, also refreshes its binaries (never the SQLite data dir).
/// </summary>
public class MauiUpdateService : IUpdateService
{
    private readonly IAppServerEndpoint _endpoint;
    private readonly ILogger<MauiUpdateService> _logger;
    private readonly HttpClient _http;
    private readonly IHomeserverInstallService? _homeserver;

    public MauiUpdateService(
        IAppServerEndpoint endpoint,
        ILogger<MauiUpdateService> logger,
        IHttpClientFactory httpClientFactory,
        IHomeserverInstallService? homeserver = null)
    {
        _endpoint = endpoint;
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(MauiUpdateService));
        _http.Timeout = TimeSpan.FromSeconds(30);
        _homeserver = homeserver;
    }

    public string? UpdateFeedUrl => _endpoint.UpdateFeedUrl;

    private string _updateUrl => _endpoint.UpdateFeedUrl;

    public bool IsVelopackInstalled => CreateManager().IsInstalled;

    public string? GetInstalledVersion() =>
        CreateManager().CurrentVersion?.ToString();

    private Velopack.UpdateManager CreateManager() =>
        new Velopack.UpdateManager(_updateUrl);

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        var mgr = CreateManager();
        var currentVersion = mgr.CurrentVersion?.ToString();
        var latestFeedVersion = await GetLatestFeedVersionAsync();
        var appImagePath = GetLinuxAppImagePath();
        var appImageWritable = IsLinuxAppImageReplaceable(appImagePath);

        _logger.LogInformation(
            "[Update] AppImage path={AppImage} writable={Writable}",
            appImagePath ?? "(none)",
            appImageWritable);

        try
        {
            if (!mgr.IsInstalled)
            {
                _logger.LogInformation("[Update] Skipping Velopack check — app is not a Velopack install.");
                return BuildResult(
                    UpdateCheckStatus.NotInstalled,
                    currentVersion,
                    latestFeedVersion,
                    message: "In-app updates are only available for the installed desktop app.");
            }

            _logger.LogInformation(
                "[Update] Checking feed at {FeedUrl} (installed: {CurrentVersion}, feed latest: {FeedLatest})",
                _updateUrl, currentVersion ?? "unknown", latestFeedVersion ?? "unknown");

            // Root-owned AppImages (e.g. /opt/wizionic from .deb) cannot be replaced in-process.
            // Force the manual install path so Settings does not claim a successful update.
            if (!appImageWritable && IsFeedNewer(currentVersion, latestFeedVersion))
            {
                _pendingUpdateInfo = null;
                return BuildResult(
                    UpdateCheckStatus.UpdateAvailable,
                    currentVersion,
                    latestFeedVersion,
                    update: new UpdateInfo
                    {
                        TargetRelease = new UpdateInfo.Release { Version = latestFeedVersion! }
                    },
                    requiresManualInstall: true,
                    message: BuildNonWritableAppImageMessage(appImagePath, latestFeedVersion!));
            }

            Velopack.UpdateInfo? vi = await mgr.CheckForUpdatesAsync();
            if (vi != null)
            {
                var availableVersion = vi.TargetFullRelease.Version.ToString();
                if (!appImageWritable)
                {
                    _pendingUpdateInfo = null;
                    return BuildResult(
                        UpdateCheckStatus.UpdateAvailable,
                        currentVersion,
                        latestFeedVersion,
                        update: new UpdateInfo
                        {
                            TargetRelease = new UpdateInfo.Release { Version = availableVersion }
                        },
                        requiresManualInstall: true,
                        message: BuildNonWritableAppImageMessage(appImagePath, availableVersion));
                }

                _pendingUpdateInfo = vi;
                return BuildResult(
                    UpdateCheckStatus.UpdateAvailable,
                    currentVersion,
                    latestFeedVersion,
                    update: new UpdateInfo
                    {
                        TargetRelease = new UpdateInfo.Release { Version = availableVersion }
                    });
            }

            if (IsFeedNewer(currentVersion, latestFeedVersion))
            {
                _logger.LogWarning(
                    "[Update] Velopack reported up-to-date but feed has {FeedLatest} > installed {CurrentVersion}.",
                    latestFeedVersion, currentVersion);

                return BuildResult(
                    UpdateCheckStatus.UpdateAvailable,
                    currentVersion,
                    latestFeedVersion,
                    update: new UpdateInfo
                    {
                        TargetRelease = new UpdateInfo.Release { Version = latestFeedVersion! }
                    },
                    requiresManualInstall: true,
                    message: $"Version {latestFeedVersion} is on the server, but this app build cannot auto-download it. Use the installer link below.");
            }

            return BuildResult(
                UpdateCheckStatus.UpToDate,
                currentVersion,
                latestFeedVersion,
                message: currentVersion != null
                    ? $"You're on version {currentVersion}."
                    : "You're on the latest version.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] Velopack check failed for feed {FeedUrl}", _updateUrl);

            if (IsFeedNewer(currentVersion, latestFeedVersion))
            {
                return BuildResult(
                    UpdateCheckStatus.UpdateAvailable,
                    currentVersion,
                    latestFeedVersion,
                    update: new UpdateInfo
                    {
                        TargetRelease = new UpdateInfo.Release { Version = latestFeedVersion! }
                    },
                    requiresManualInstall: true,
                    message: $"Version {latestFeedVersion} is available, but the update check failed ({ex.Message}). Use the installer link below.");
            }

            return BuildResult(
                UpdateCheckStatus.CheckFailed,
                currentVersion,
                latestFeedVersion,
                message: $"Could not check for updates: {ex.Message} (feed: {_updateUrl})");
        }
    }

    /// <summary>
    /// AppImage runtime mounts under /tmp/.mount_* for execution; APPIMAGE env points at the real file on disk.
    /// Velopack must replace that on-disk file. System installs under /opt are root-owned and not replaceable.
    /// </summary>
    private static string? GetLinuxAppImagePath()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        var path = Environment.GetEnvironmentVariable("APPIMAGE");
        return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
    }

    private static bool IsLinuxAppImageReplaceable(string? appImagePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return true;

        // Not running as AppImage (e.g. unpackaged debug) — let Velopack decide.
        if (string.IsNullOrWhiteSpace(appImagePath))
            return true;

        try
        {
            var full = Path.GetFullPath(appImagePath);
            if (full.StartsWith("/opt/", StringComparison.Ordinal)
                || full.StartsWith("/usr/", StringComparison.Ordinal))
                return false;

            var dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            // Probe parent directory write access (required to replace the AppImage file).
            var probe = Path.Combine(dir, $".app-update-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildNonWritableAppImageMessage(string? appImagePath, string availableVersion) =>
        $"Version {availableVersion} is available, but this install is not user-writable "
        + $"({appImagePath ?? "system AppImage"}). "
        + "System packages under /opt cannot be replaced in-app. "
        + "Reinstall with the user AppImage installer for automatic updates: "
        + "curl -fsSL https://wizionic.com/install.sh | bash";

    private UpdateCheckResult BuildResult(
        UpdateCheckStatus status,
        string? currentVersion,
        string? latestFeedVersion,
        UpdateInfo? update = null,
        string? message = null,
        bool requiresManualInstall = false) =>
        new()
        {
            Status = status,
            CurrentVersion = currentVersion,
            LatestFeedVersion = latestFeedVersion,
            FeedUrl = _updateUrl,
            Update = update,
            Message = message,
            RequiresManualInstall = requiresManualInstall
        };

    private async Task<string?> GetLatestFeedVersionAsync()
    {
        try
        {
            var feedUrl = _updateUrl.TrimEnd('/') + "/" + AppServerOptions.VelopackReleasesIndexFile;
            var feed = await _http.GetFromJsonAsync<VelopackFeed>(feedUrl);
            if (feed?.Assets == null || feed.Assets.Length == 0)
                return null;

            return feed.Assets
                .Where(a => a.Type == "Full" && a.PackageId is "Wizionic" or "com.wizionic.app")
                .Select(a => a.Version)
                .OrderByDescending(ParseVersion)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] Could not read feed JSON from {FeedUrl}", _updateUrl);
            return null;
        }
    }

    private static bool IsFeedNewer(string? currentVersion, string? latestFeedVersion)
    {
        if (string.IsNullOrWhiteSpace(latestFeedVersion))
            return false;

        if (string.IsNullOrWhiteSpace(currentVersion))
            return true;

        return ParseVersion(latestFeedVersion) > ParseVersion(currentVersion);
    }

    private static Version ParseVersion(string version)
    {
        if (Version.TryParse(version, out var parsed))
            return parsed;

        var core = version.Split('+', '-')[0];
        return Version.TryParse(core, out parsed) ? parsed : new Version(0, 0);
    }

    private static Velopack.UpdateInfo? _pendingUpdateInfo;

    public async Task DownloadAndInstallAsync(UpdateInfo update)
    {
        var appImagePath = GetLinuxAppImagePath();
        if (!IsLinuxAppImageReplaceable(appImagePath))
        {
            throw new InvalidOperationException(
                BuildNonWritableAppImageMessage(appImagePath, update.TargetRelease?.Version ?? "latest"));
        }

        var mgr = CreateManager();
        if (_pendingUpdateInfo == null)
            throw new InvalidOperationException("No update available. Check for updates first.");

        // Update homeserver binaries before MAUI restarts (if installed). Never re-prompts;
        // never touches ProgramData data/homeserver.db.
        if (_homeserver is { IsSupported: true })
        {
            try
            {
                var hs = _homeserver.GetState();
                if (hs.IsInstalled)
                {
                    _logger.LogInformation("[Update] Updating Home Server binaries before app restart…");
                    var result = await _homeserver.UpdateIfNeededAsync();
                    _logger.LogInformation("[Update] Home Server: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Update] Home Server update failed; continuing with MAUI update");
            }
        }

        await mgr.DownloadUpdatesAsync(_pendingUpdateInfo);
        mgr.ApplyUpdatesAndRestart(_pendingUpdateInfo);
    }

    private sealed class VelopackFeed
    {
        [JsonPropertyName("Assets")]
        public VelopackFeedAsset[]? Assets { get; init; }
    }

    private sealed class VelopackFeedAsset
    {
        [JsonPropertyName("PackageId")]
        public string PackageId { get; init; } = "";

        [JsonPropertyName("Version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("Type")]
        public string Type { get; init; } = "";
    }
}