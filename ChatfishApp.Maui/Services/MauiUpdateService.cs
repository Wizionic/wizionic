using ChatfishApp.Core.Configuration;
using ChatfishApp.Core.Update;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Velopack-based auto-updater for the MAUI desktop target.
/// Reads the update source URL from ChatfishServer.BaseUrl in appsettings.json.
/// </summary>
public class MauiUpdateService : IUpdateService
{
    private readonly string _updateUrl;

    public MauiUpdateService(IOptions<ChatfishServerOptions> options)
    {
        // BaseUrl is configured to the releases directory, e.g. https://chatfish.me/releases/windows/
        _updateUrl = options.Value.BaseUrl;
    }

    private Velopack.UpdateManager CreateManager() =>
        new Velopack.UpdateManager(_updateUrl);

    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            var mgr = CreateManager();
            Velopack.UpdateInfo? vi = await mgr.CheckForUpdatesAsync();
            if (vi == null) return null;

            _pendingUpdateInfo = vi;

            // Map to our lightweight DTO so the shared project never references Velopack.
            return new UpdateInfo
            {
                TargetRelease = new Core.Update.UpdateInfo.Release
                {
                    Version = vi.TargetFullRelease.Version.ToString()
                }
            };
        }
        catch
        {
            // Running in dev or an unbundled build, or network error — silently return null.
            return null;
        }
    }

    private static Velopack.UpdateInfo? _pendingUpdateInfo;

    public async Task DownloadAndInstallAsync(Core.Update.UpdateInfo update)
    {
        var mgr = CreateManager();
        if (_pendingUpdateInfo == null) throw new InvalidOperationException("No update available.");
        await mgr.DownloadUpdatesAsync(_pendingUpdateInfo);
        mgr.ApplyUpdatesAndRestart(_pendingUpdateInfo);
    }
}
