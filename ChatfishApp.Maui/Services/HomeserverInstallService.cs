using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using ChatfishApp.Core.Configuration;
using ChatfishApp.Core.Homeserver;
// IChatfishServerEndpoint
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Downloads, installs, updates, and removes the optional Windows Chatfish Home Server.
/// Data lives under ProgramData and is never deleted by update/uninstall of binaries.
/// </summary>
public sealed class HomeserverInstallService : IHomeserverInstallService
{
    private readonly string _feedBaseUrl;
    private readonly string _productionBaseUrl;
    private readonly ILogger<HomeserverInstallService> _logger;
    private readonly HttpClient _http;
    private readonly IChatfishServerEndpoint? _serverEndpoint;

    public HomeserverInstallService(
        IOptions<ChatfishServerOptions> options,
        ILogger<HomeserverInstallService> logger,
        IHttpClientFactory httpClientFactory,
        IChatfishServerEndpoint? serverEndpoint = null)
    {
        // Feed is always production (or configured BaseUrl) so local homeserver installs
        // still pull packages from the public release site.
        _productionBaseUrl = string.IsNullOrWhiteSpace(options.Value.BaseUrl)
            ? "https://chatfish.me"
            : options.Value.BaseUrl.TrimEnd('/');
        // Prefer public site for package downloads even if MAUI was retargeted to localhost.
        _feedBaseUrl = _productionBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            ? "https://chatfish.me"
            : _productionBaseUrl;
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(HomeserverInstallService));
        _http.Timeout = TimeSpan.FromMinutes(15);
        _serverEndpoint = serverEndpoint;

        // Velopack after-update flag file (written from FastCallback — no UI/network there).
        if (File.Exists(HomeserverPaths.PendingUpdateFlagPath))
            PendingUpdateCheck = true;
    }

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool ShouldPromptOnStartup { get; set; }

    public bool PendingUpdateCheck { get; set; }

    public HomeserverState GetState() => HomeserverState.Load();

    public void Decline()
    {
        var state = HomeserverState.Load();
        state.InstallMode = HomeserverInstallMode.Declined;
        state.AskedAt ??= DateTimeOffset.UtcNow;
        state.DeclinedAt = DateTimeOffset.UtcNow;
        state.Save();
        ShouldPromptOnStartup = false;
        _logger.LogInformation("[Homeserver] User declined homeserver install.");
    }

    public async Task<HomeserverInstallResult> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return HomeserverInstallResult.Fail("Home Server is only supported on Windows.");

        try
        {
            progress?.Report("Checking for Home Server package…");
            var manifest = await GetFeedManifestAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "Could not find a Home Server package on the update feed. Deploy homeserver-win-x64 first.");

            progress?.Report($"Downloading Home Server {manifest.Version}…");
            var zipPath = await DownloadPackageAsync(manifest, cancellationToken);

            progress?.Report("Installing files…");
            EnsureDataLayout();
            ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            WriteHomeserverAppsettings(HomeserverPaths.DefaultPort);
            TryDelete(zipPath);

            progress?.Report("Starting Home Server service…");
            var mode = await StartAsServiceOrUserSessionAsync(cancellationToken);

            var state = HomeserverState.Load();
            state.InstallMode = mode;
            state.InstalledVersion = manifest.Version;
            state.Port = HomeserverPaths.DefaultPort;
            state.BaseUrl = HomeserverPaths.DefaultBaseUrl;
            state.AskedAt ??= DateTimeOffset.UtcNow;
            state.InstalledAt = DateTimeOffset.UtcNow;
            state.DeclinedAt = null;
            state.Save();

            await RetargetMauiToLocalHomeserverAsync(state.BaseUrl);
            ShouldPromptOnStartup = false;

            var modeLabel = mode == HomeserverInstallMode.WindowsService
                ? "Windows Service (starts automatically)"
                : "user session (starts at logon)";
            return HomeserverInstallResult.Ok(
                $"Home Server installed ({modeLabel}) at {state.BaseUrl}",
                mode,
                state.BaseUrl,
                manifest.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Homeserver] Install failed");
            return HomeserverInstallResult.Fail($"Home Server install failed: {ex.Message}");
        }
    }

    public async Task<HomeserverInstallResult> UpdateIfNeededAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PendingUpdateCheck = false;
        TryDelete(HomeserverPaths.PendingUpdateFlagPath);

        if (!IsSupported)
            return HomeserverInstallResult.Ok("Not supported.", HomeserverInstallMode.Unknown);

        var state = HomeserverState.Load();
        if (!state.IsInstalled)
            return HomeserverInstallResult.Ok("Home Server not installed.", state.InstallMode);

        try
        {
            progress?.Report("Checking Home Server updates…");
            var manifest = await GetFeedManifestAsync(cancellationToken);
            if (manifest is null)
                return HomeserverInstallResult.Ok("No Home Server feed available.", state.InstallMode);

            if (!IsNewerVersion(manifest.Version, state.InstalledVersion))
            {
                return HomeserverInstallResult.Ok(
                    $"Home Server is up to date ({state.InstalledVersion}).",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            progress?.Report($"Updating Home Server to {manifest.Version}…");
            await StopHostAsync(state.InstallMode, cancellationToken);

            var zipPath = await DownloadPackageAsync(manifest, cancellationToken);
            // Replace binaries only — never touch data/ or state identity.
            ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            // Re-write appsettings in case template changed; preserve port from state.
            WriteHomeserverAppsettings(state.Port);
            TryDelete(zipPath);

            await StartHostAsync(state.InstallMode, cancellationToken);

            state.InstalledVersion = manifest.Version;
            state.Save();

            return HomeserverInstallResult.Ok(
                $"Home Server updated to {manifest.Version}.",
                state.InstallMode,
                state.BaseUrl,
                manifest.Version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Homeserver] Update failed");
            return HomeserverInstallResult.Fail($"Home Server update failed: {ex.Message}");
        }
    }

    public async Task UninstallBinariesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return;

        var state = HomeserverState.Load();
        try
        {
            await StopHostAsync(state.InstallMode, cancellationToken);
            if (state.InstallMode == HomeserverInstallMode.WindowsService)
                await DeleteServiceAsync(cancellationToken);
            RemoveUserStartupShortcut();

            // Remove app binaries only — keep data/ and state (or mark declined).
            if (Directory.Exists(HomeserverPaths.AppDirectory))
            {
                try
                {
                    Directory.Delete(HomeserverPaths.AppDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Homeserver] Could not fully delete app directory");
                }
            }

            _logger.LogInformation(
                "[Homeserver] Binaries removed. Database preserved at {DbPath}",
                HomeserverPaths.DatabasePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Uninstall binaries encountered errors");
        }
    }

    public string? GetServiceStatusText()
    {
        if (!IsSupported)
            return null;

        var state = HomeserverState.Load();
        if (state.InstallMode == HomeserverInstallMode.Declined)
            return "Declined";
        if (!state.IsInstalled)
            return "Not installed";

        if (state.InstallMode == HomeserverInstallMode.WindowsService)
        {
            try
            {
                using var sc = new ServiceController(HomeserverPaths.ServiceName);
                return $"Installed v{state.InstalledVersion ?? "?"} — Service {sc.Status} ({state.BaseUrl})";
            }
            catch
            {
                return $"Installed v{state.InstalledVersion ?? "?"} — Service not found ({state.BaseUrl})";
            }
        }

        var running = IsHostProcessRunning();
        return $"Installed v{state.InstalledVersion ?? "?"} — User session {(running ? "running" : "stopped")} ({state.BaseUrl})";
    }

    // ── feed / download ──────────────────────────────────────────────────

    private async Task<HomeserverFeedManifest?> GetFeedManifestAsync(CancellationToken ct)
    {
        var url = $"{_feedBaseUrl}/{HomeserverPaths.ReleasesFeedPath}/{HomeserverPaths.LatestManifestFile}";
        try
        {
            var manifest = await _http.GetFromJsonAsync<HomeserverFeedManifest>(url, ct);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return null;

            if (string.IsNullOrWhiteSpace(manifest.Url))
            {
                var fileName = string.IsNullOrWhiteSpace(manifest.FileName)
                    ? $"homeserver-win-x64-{manifest.Version}.zip"
                    : manifest.FileName;
                manifest.Url = $"{_feedBaseUrl}/{HomeserverPaths.ReleasesFeedPath}/{fileName}";
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Failed to read feed {Url}", url);
            return null;
        }
    }

    private async Task<string> DownloadPackageAsync(HomeserverFeedManifest manifest, CancellationToken ct)
    {
        var url = manifest.Url
            ?? throw new InvalidOperationException("Manifest has no package URL.");
        Directory.CreateDirectory(HomeserverPaths.RootDirectory);
        var zipPath = Path.Combine(Path.GetTempPath(), $"chatfish-homeserver-{manifest.Version}-{Guid.NewGuid():N}.zip");

        await using (var remote = await _http.GetStreamAsync(url, ct))
        await using (var file = File.Create(zipPath))
            await remote.CopyToAsync(file, ct);

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            await using var fs = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (!hash.Equals(manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(zipPath);
                throw new InvalidOperationException("Home Server package hash mismatch.");
            }
        }

        return zipPath;
    }

    private static void ExtractPackage(string zipPath, string targetDir)
    {
        // Stage into a temp folder then swap so a failed extract never leaves a half-deleted app.
        var staging = targetDir + ".staging";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        // If the zip has a single top-level folder, unwrap it.
        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var inner = entries[0];
            var unwrap = staging + ".unwrap";
            if (Directory.Exists(unwrap))
                Directory.Delete(unwrap, recursive: true);
            Directory.Move(inner, unwrap);
            Directory.Delete(staging, recursive: true);
            staging = unwrap;
        }

        if (Directory.Exists(targetDir))
        {
            var backup = targetDir + ".old";
            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
            Directory.Move(targetDir, backup);
            try
            {
                Directory.Move(staging, targetDir);
                try { Directory.Delete(backup, recursive: true); } catch { /* best effort */ }
            }
            catch
            {
                // Roll back
                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, recursive: true);
                Directory.Move(backup, targetDir);
                throw;
            }
        }
        else
        {
            Directory.Move(staging, targetDir);
        }

        if (!File.Exists(HomeserverPaths.HostExecutablePath))
            throw new InvalidOperationException(
                $"Package did not contain ChatfishApp.exe at {HomeserverPaths.HostExecutablePath}");
    }

    private static void EnsureDataLayout()
    {
        Directory.CreateDirectory(HomeserverPaths.DataDirectory);
        Directory.CreateDirectory(HomeserverPaths.AppDirectory);
    }

    private static void WriteHomeserverAppsettings(string port)
    {
        Directory.CreateDirectory(HomeserverPaths.RootDirectory);
        var dbPath = HomeserverPaths.DatabasePath.Replace('\\', '/');
        var json = $$"""
            {
              "Homeserver": {
                "AllowHttpCookies": true
              },
              "ConnectionStrings": {
                "DefaultConnection": "Data Source={{dbPath}}"
              },
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning"
                }
              },
              "Kestrel": {
                "Endpoints": {
                  "Http": {
                    "Url": "http://127.0.0.1:{{port}}"
                  }
                }
              }
            }
            """;
        File.WriteAllText(HomeserverPaths.AppsettingsPath, json);
    }

    // ── service / process control ────────────────────────────────────────

    private async Task<HomeserverInstallMode> StartAsServiceOrUserSessionAsync(CancellationToken ct)
    {
        if (await TryInstallAndStartServiceAsync(ct))
            return HomeserverInstallMode.WindowsService;

        _logger.LogWarning("[Homeserver] Service install failed or elevation denied — using user-session fallback.");
        StartUserSessionHost();
        InstallUserStartupShortcut();
        return HomeserverInstallMode.UserSession;
    }

    private async Task<bool> TryInstallAndStartServiceAsync(CancellationToken ct)
    {
        try
        {
            // Create/update service via elevated sc.exe
            var binPath = $"\"{HomeserverPaths.HostExecutablePath}\"";
            var createArgs =
                $"create {HomeserverPaths.ServiceName} binPath= {binPath} start= auto " +
                $"DisplayName= \"{HomeserverPaths.ServiceDisplayName}\"";

            if (!await RunElevatedAsync("sc.exe", createArgs, ct))
            {
                // Service may already exist — try config + start
            }
            else
            {
                await RunElevatedAsync("sc.exe",
                    $"description {HomeserverPaths.ServiceName} \"Chatfish local login server and website\"",
                    ct);
            }

            // Ensure binPath is correct on reinstall
            await RunElevatedAsync("sc.exe",
                $"config {HomeserverPaths.ServiceName} binPath= {binPath} start= auto",
                ct);

            // sc start returns non-zero if already running — ignore exit code and verify status.
            await RunElevatedAsync("sc.exe", $"start {HomeserverPaths.ServiceName}", ct, acceptAnyExitCode: true);

            // Verify
            await Task.Delay(1500, ct);
            using var sc = new ServiceController(HomeserverPaths.ServiceName);
            sc.Refresh();
            return sc.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Service path failed");
            return false;
        }
    }

    private async Task StartHostAsync(HomeserverInstallMode mode, CancellationToken ct)
    {
        if (mode == HomeserverInstallMode.WindowsService)
        {
            await RunElevatedAsync("sc.exe", $"start {HomeserverPaths.ServiceName}", ct);
            return;
        }

        StartUserSessionHost();
    }

    private async Task StopHostAsync(HomeserverInstallMode mode, CancellationToken ct)
    {
        if (mode == HomeserverInstallMode.WindowsService)
        {
            try
            {
                using var sc = new ServiceController(HomeserverPaths.ServiceName);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    await RunElevatedAsync("sc.exe", $"stop {HomeserverPaths.ServiceName}", ct);
                    await Task.Delay(1000, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Homeserver] Stop service note");
            }
            return;
        }

        KillHostProcesses();
    }

    private async Task DeleteServiceAsync(CancellationToken ct)
    {
        try
        {
            await RunElevatedAsync("sc.exe", $"stop {HomeserverPaths.ServiceName}", ct);
        }
        catch { /* ignore */ }

        await RunElevatedAsync("sc.exe", $"delete {HomeserverPaths.ServiceName}", ct);
    }

    private void StartUserSessionHost()
    {
        if (!File.Exists(HomeserverPaths.HostExecutablePath))
            throw new InvalidOperationException("Home Server executable not found.");

        if (IsHostProcessRunning())
            return;

        var psi = new ProcessStartInfo
        {
            FileName = HomeserverPaths.HostExecutablePath,
            WorkingDirectory = HomeserverPaths.AppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        // Ensure the homeserver settings path is discoverable (host always checks ProgramData).
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        Process.Start(psi);
    }

    private static bool IsHostProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("ChatfishApp")
                .Any(p =>
                {
                    try
                    {
                        return p.MainModule?.FileName?.StartsWith(
                            HomeserverPaths.AppDirectory,
                            StringComparison.OrdinalIgnoreCase) == true;
                    }
                    catch
                    {
                        return false;
                    }
                });
        }
        catch
        {
            return false;
        }
    }

    private static void KillHostProcesses()
    {
        foreach (var p in Process.GetProcessesByName("ChatfishApp"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null &&
                    path.StartsWith(HomeserverPaths.AppDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string StartupShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Chatfish Home Server.cmd");

    private static void InstallUserStartupShortcut()
    {
        var cmd = $"""
            @echo off
            start "" /D "{HomeserverPaths.AppDirectory}" "{HomeserverPaths.HostExecutablePath}"
            """;
        File.WriteAllText(StartupShortcutPath, cmd.Trim() + Environment.NewLine);
    }

    private static void RemoveUserStartupShortcut()
    {
        TryDelete(StartupShortcutPath);
    }

    /// <summary>
    /// Retarget MAUI login server so auth/sync hit the local homeserver (live Settings field + HttpClient).
    /// </summary>
    private async Task RetargetMauiToLocalHomeserverAsync(string baseUrl)
    {
        try
        {
            if (_serverEndpoint is not null)
            {
                await _serverEndpoint.SetBaseUrlAsync(baseUrl);
                _logger.LogInformation("[Homeserver] Login server set via endpoint to {BaseUrl}", baseUrl);
                return;
            }

            // Fallback if endpoint not registered
            var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
            Directory.CreateDirectory(MauiAppData.Directory);
            var json = JsonSerializer.Serialize(new
            {
                ChatfishServer = new
                {
                    BaseUrl = baseUrl,
                    UpdateFeedUrl = $"{_feedBaseUrl}/releases/windows"
                }
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            _logger.LogInformation("[Homeserver] Wrote MAUI local override {Path} BaseUrl={BaseUrl}", path, baseUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Could not retarget MAUI BaseUrl");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<bool> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        bool acceptAnyExitCode = false)
    {
        // Write a tiny helper so we can wait for elevation result via exit code file.
        var marker = Path.Combine(Path.GetTempPath(), $"chatfish-elev-{Guid.NewGuid():N}.exit");
        TryDelete(marker);

        var bat = Path.Combine(Path.GetTempPath(), $"chatfish-elev-{Guid.NewGuid():N}.cmd");
        var batBody = $"""
            @echo off
            {fileName} {arguments}
            echo %ERRORLEVEL%> "{marker}"
            """;
        await File.WriteAllTextAsync(bat, batBody, ct);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bat,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;

            await proc.WaitForExitAsync(ct);

            // If UAC was denied, process may exit without writing marker.
            if (!File.Exists(marker))
                return false;

            if (acceptAnyExitCode)
                return true;

            var codeText = (await File.ReadAllTextAsync(marker, ct)).Trim();
            return int.TryParse(codeText, out var code) && code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC
            return false;
        }
        finally
        {
            TryDelete(bat);
            TryDelete(marker);
        }
    }

    private static bool IsNewerVersion(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(current))
            return true;

        static Version Parse(string v)
        {
            var core = v.Split('+', '-')[0];
            return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0);
        }

        return Parse(candidate) > Parse(current);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
