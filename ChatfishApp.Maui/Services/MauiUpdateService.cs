using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChatfishApp.Core.Configuration;

using ChatfishApp.Core.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Velopack-based auto-updater for the MAUI desktop target.
/// Reads the update feed URL from ChatfishServer options in appsettings.json.
/// </summary>
public class MauiUpdateService : IUpdateService
{
    private readonly string _updateUrl;
    private readonly ILogger<MauiUpdateService> _logger;
    private readonly HttpClient _http;

    public MauiUpdateService(
        IOptions<ChatfishServerOptions> options,
        ILogger<MauiUpdateService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _updateUrl = options.Value.GetUpdateFeedUrl();
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(MauiUpdateService));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public string? UpdateFeedUrl => _updateUrl;

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

            Velopack.UpdateInfo? vi = await mgr.CheckForUpdatesAsync();
            if (vi != null)
            {
                _pendingUpdateInfo = vi;
                var availableVersion = vi.TargetFullRelease.Version.ToString();
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
                message: $"Could not check for updates: {ex.Message}");
        }
    }

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
            var feedUrl = _updateUrl.TrimEnd('/') + "/" + ChatfishServerOptions.VelopackReleasesIndexFile;
            var feed = await _http.GetFromJsonAsync<VelopackFeed>(feedUrl);
            if (feed?.Assets == null || feed.Assets.Length == 0)
                return null;

            return feed.Assets
                .Where(a => a.Type == "Full" && a.PackageId is "Chatfish" or "com.chatfish.app")
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
        var mgr = CreateManager();
        if (_pendingUpdateInfo == null)
            throw new InvalidOperationException("No update available. Check for updates first.");

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