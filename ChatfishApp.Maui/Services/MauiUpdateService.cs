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

    public MauiUpdateService(IOptions<ChatfishServerOptions> options, ILogger<MauiUpdateService> logger)
    {
        _updateUrl = options.Value.GetUpdateFeedUrl();
        _logger = logger;
    }

    private Velopack.UpdateManager CreateManager() =>
        new Velopack.UpdateManager(_updateUrl);

    public async Task<UpdateCheckResult> CheckForUpdateAsync()
    {
        try
        {
            var mgr = CreateManager();
            var currentVersion = mgr.CurrentVersion?.ToString();

            if (!mgr.IsInstalled)
            {
                _logger.LogInformation("[Update] Skipping check — app is not a Velopack install (Debug/unbundled build).");
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.NotInstalled,
                    CurrentVersion = currentVersion,
                    Message = "In-app updates are only available for the installed desktop app."
                };
            }

            _logger.LogInformation("[Update] Checking feed at {FeedUrl} (current: {CurrentVersion})", _updateUrl, currentVersion ?? "unknown");

            Velopack.UpdateInfo? vi = await mgr.CheckForUpdatesAsync();
            if (vi == null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateCheckStatus.UpToDate,
                    CurrentVersion = currentVersion,
                    Message = currentVersion != null
                        ? $"You're on version {currentVersion}."
                        : null
                };
            }

            _pendingUpdateInfo = vi;
            var availableVersion = vi.TargetFullRelease.Version.ToString();

            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                CurrentVersion = currentVersion,
                Update = new UpdateInfo
                {
                    TargetRelease = new UpdateInfo.Release { Version = availableVersion }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Update] Check failed for feed {FeedUrl}", _updateUrl);
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.CheckFailed,
                Message = $"Could not check for updates: {ex.Message}"
            };
        }
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
}