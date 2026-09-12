using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Core.Configuration;
using App.Core.Homeserver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if WINDOWS
using System.ServiceProcess;
#endif

namespace App.Maui.Services;

/// <summary>
/// Downloads, installs, updates, starts, stops, and uninstalls the optional Wizionic Home Server
/// (Windows Service / Linux systemd, with user-session fallback).
/// Updates replace binaries only; uninstall deletes login data under ProgramData.
/// </summary>
public sealed class HomeserverInstallService : IHomeserverInstallService
{
    private readonly string _productionBaseUrl;
    private readonly ILogger<HomeserverInstallService> _logger;
    private readonly HttpClient _http;
    private readonly IAppServerEndpoint? _serverEndpoint;

    public HomeserverInstallService(
        IOptions<AppServerOptions> options,
        ILogger<HomeserverInstallService> logger,
        IHttpClientFactory httpClientFactory,
        IAppServerEndpoint? serverEndpoint = null)
    {
        _productionBaseUrl = string.IsNullOrWhiteSpace(options.Value.BaseUrl)
            ? "https://wizionic.com"
            : options.Value.BaseUrl.TrimEnd('/');
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(HomeserverInstallService));
        _http.Timeout = TimeSpan.FromMinutes(15);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Wizionic");
        _serverEndpoint = serverEndpoint;

        if (File.Exists(HomeserverPaths.PendingUpdateFlagPath))
            PendingUpdateCheck = true;
    }

    public bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

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
            return HomeserverInstallResult.Fail("Home Server is only supported on Windows and Linux.");

        try
        {
            if (!HomeserverState.Load().IsInstalled)
            {
                progress?.Report("Clearing leftover Home Server data…");
                await ClearStaleInstallAsync(cancellationToken);
            }

            KillHostProcesses();
            HomeserverPaths.ResetWindowsRootCache();
            AppendInstallLog($"Install start root={HomeserverPaths.RootDirectory}");
            progress?.Report("Checking for Home Server package…");
            var manifest = await GetFeedManifestAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "Could not find a Home Server package on the update feed. Deploy the homeserver package first.");
            AppendInstallLog($"Feed ok version={manifest.Version} url={manifest.Url}");

            progress?.Report($"Downloading Home Server {manifest.Version}…");
            var zipPath = await DownloadPackageAsync(manifest, cancellationToken);

            progress?.Report("Installing files…");
            EnsureDataLayout();
            try
            {
                ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            }
            catch (Exception ex) when (OperatingSystem.IsWindows()
                && IsAccessFailure(ex)
                && IsProgramDataRoot())
            {
                AppendInstallLog("ProgramData replace failed without elevation; extracting to this user folder first.", ex);
                HomeserverPaths.ForceUserLocalRoot();
                progress?.Report("Installing files (this user folder)…");
                EnsureDataLayout();
                ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            }
            EnsureHostExecutable();
            WriteHomeserverAppsettings(HomeserverPaths.DefaultPort);
            TryDelete(zipPath);

            progress?.Report("Starting Home Server …");
            var mode = await StartAsServiceOrUserSessionAsync(cancellationToken);
            if (mode == HomeserverInstallMode.UserSession)
                await EnsureLanFirewallAsync(cancellationToken);

            var state = HomeserverState.Load();
            state.InstallMode = mode;
            state.InstalledVersion = manifest.Version;
            state.Port = HomeserverPaths.DefaultPort;
            state.BaseUrl = HomeserverPaths.DefaultBaseUrl;
            state.AskedAt ??= DateTimeOffset.UtcNow;
            state.InstalledAt = DateTimeOffset.UtcNow;
            state.DeclinedAt = null;
            state.OnboardingCompletedAt = null;
            state.Save();

            var restartRequired = await RetargetMauiToLocalHomeserverAsync(state.BaseUrl);
            ShouldPromptOnStartup = false;
            AppendInstallLog($"Install ok mode={mode} version={manifest.Version} restart={restartRequired}");

            var modeLabel = mode switch
            {
                HomeserverInstallMode.WindowsService => "Windows Service (starts automatically)",
                HomeserverInstallMode.Systemd => "systemd unit (starts automatically)",
                _ => "user session (starts at logon)"
            };
            var message = restartRequired
                ? $"Home Server installed ({modeLabel}) at {state.BaseUrl}. Sign out and restart to use it."
                : $"Home Server installed ({modeLabel}) at {state.BaseUrl}";
            return HomeserverInstallResult.Ok(
                message,
                mode,
                state.BaseUrl,
                manifest.Version,
                restartRequired: restartRequired);
        }
        catch (Exception ex)
        {
            AppendInstallLog("Install failed", ex);
            _logger.LogError(ex, "[Homeserver] Install failed");
            return HomeserverInstallResult.Fail(
                $"Home Server install failed: {FormatException(ex)} Log: {InstallLogPath}");
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
            ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            EnsureHostExecutable();
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

    public async Task<HomeserverInstallResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return HomeserverInstallResult.Fail("Home Server is only supported on Windows and Linux.");

        var state = HomeserverState.Load();
        if (!state.IsInstalled)
            return HomeserverInstallResult.Fail("Home Server is not installed.");

        try
        {
            await EnsureLanAccessAsync(cancellationToken);
            if (IsRunning())
            {
                return HomeserverInstallResult.Ok(
                    "Home Server is already running.",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            await StartHostAsync(state.InstallMode, cancellationToken);
            await Task.Delay(800, cancellationToken);
            if (!IsRunning())
                return HomeserverInstallResult.Fail("Home Server did not start.");

            return HomeserverInstallResult.Ok(
                "Home Server started.",
                state.InstallMode,
                state.BaseUrl,
                state.InstalledVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Homeserver] Start failed");
            return HomeserverInstallResult.Fail($"Could not start Home Server: {ex.Message}");
        }
    }

    public async Task<HomeserverInstallResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return HomeserverInstallResult.Fail("Home Server is only supported on Windows and Linux.");

        var state = HomeserverState.Load();
        if (!state.IsInstalled)
            return HomeserverInstallResult.Fail("Home Server is not installed.");

        try
        {
            if (!IsRunning())
            {
                return HomeserverInstallResult.Ok(
                    "Home Server is already stopped.",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            await StopHostAsync(state.InstallMode, cancellationToken);
            await Task.Delay(500, cancellationToken);
            return HomeserverInstallResult.Ok(
                "Home Server stopped. Login accounts on this PC are unchanged.",
                state.InstallMode,
                state.BaseUrl,
                state.InstalledVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Homeserver] Stop failed");
            return HomeserverInstallResult.Fail($"Could not stop Home Server: {ex.Message}");
        }
    }

    public async Task<HomeserverInstallResult> UninstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return HomeserverInstallResult.Fail("Home Server is only supported on Windows and Linux.");

        var state = HomeserverState.Load();
        var retarget = IsLoginServerThisHomeserver(_serverEndpoint?.BaseUrl);
        try
        {
            await StopHostAsync(state.InstallMode, cancellationToken);
            await Task.Delay(800, cancellationToken);
#if WINDOWS
            if (state.InstallMode == HomeserverInstallMode.WindowsService)
                await DeleteWindowsServiceAsync(cancellationToken);
#endif
            if (state.InstallMode == HomeserverInstallMode.Systemd)
                await DeleteSystemdUnitAsync(cancellationToken);

            RemoveUserStartupShortcut();
            await RemoveLanFirewallAsync(cancellationToken);
            DeleteInstallRoot();

            var restartRequired = false;
            if (retarget && _serverEndpoint is not null)
            {
                try
                {
                    restartRequired = await _serverEndpoint.SetBaseUrlAsync("https://wizionic.com", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Homeserver] Could not retarget login server after uninstall");
                }
            }

            var message = restartRequired
                ? "Home Server uninstalled and login data deleted. Login server set to wizionic.com. Restart Wizionic to connect."
                : "Home Server uninstalled and login data deleted.";
            _logger.LogInformation("[Homeserver] Uninstalled (data deleted).");
            return HomeserverInstallResult.Ok(
                message,
                HomeserverInstallMode.Unknown,
                restartRequired: restartRequired);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Homeserver] Uninstall failed");
            return HomeserverInstallResult.Fail($"Home Server uninstall failed: {ex.Message}");
        }
    }

    public async Task<HomeserverInstallResult> EnsureLanAccessAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return HomeserverInstallResult.Ok("Not supported.", HomeserverInstallMode.Unknown);

        var state = HomeserverState.Load();
        if (!state.IsInstalled)
            return HomeserverInstallResult.Ok("Home Server not installed.", state.InstallMode);

        try
        {
            var bindChanged = NeedsLanBindRewrite(state.Port);
            if (bindChanged)
                WriteHomeserverAppsettings(state.Port);

            var firewallChanged = await EnsureLanFirewallAsync(cancellationToken);
            var envChanged = false;
#if WINDOWS
            if (state.InstallMode == HomeserverInstallMode.WindowsService)
                envChanged = await EnsureWindowsServiceListenEnvAsync(state.Port, cancellationToken);
#endif

            if ((bindChanged || envChanged) && IsRunning())
            {
                await StopHostAsync(state.InstallMode, cancellationToken);
                await StartHostAsync(state.InstallMode, cancellationToken);
                return HomeserverInstallResult.Ok(
                    "Home Server is now reachable on your network.",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            if (bindChanged || envChanged)
            {
                return HomeserverInstallResult.Ok(
                    "Home Server will listen on the network the next time it starts.",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            if (firewallChanged)
            {
                return HomeserverInstallResult.Ok(
                    "Opened the firewall so other devices can reach this Home Server.",
                    state.InstallMode,
                    state.BaseUrl,
                    state.InstalledVersion);
            }

            return HomeserverInstallResult.Ok(
                "LAN access already configured.",
                state.InstallMode,
                state.BaseUrl,
                state.InstalledVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Ensure LAN access failed");
            return HomeserverInstallResult.Fail($"Could not enable network access: {ex.Message}");
        }
    }

    public bool IsRunning()
    {
        if (!IsSupported)
            return false;

        var state = HomeserverState.Load();
        if (!state.IsInstalled)
            return false;

#if WINDOWS
        if (state.InstallMode == HomeserverInstallMode.WindowsService)
        {
            try
            {
                using var sc = new ServiceController(HomeserverPaths.ServiceName);
                return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            }
            catch
            {
                return false;
            }
        }
#endif

        if (state.InstallMode == HomeserverInstallMode.Systemd)
            return IsSystemdUnitActive();

        return IsHostProcessRunning();
    }

    public HomeserverListenAddresses GetListenAddresses()
    {
        var state = HomeserverState.Load();
        var port = string.IsNullOrWhiteSpace(state.Port) ? HomeserverPaths.DefaultPort : state.Port;
        return HomeserverListenAddresses.Detect(port);
    }

    public bool IsLoginServerThisHomeserver(string? baseUrl)
    {
        if (!GetState().IsInstalled)
            return false;
        return GetListenAddresses().MatchesLoginServer(baseUrl);
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

        var local = state.BaseUrl;
#if WINDOWS
        if (state.InstallMode == HomeserverInstallMode.WindowsService)
        {
            try
            {
                using var sc = new ServiceController(HomeserverPaths.ServiceName);
                return $"Installed v{state.InstalledVersion ?? "?"} — Service {sc.Status} ({local})";
            }
            catch
            {
                return $"Installed v{state.InstalledVersion ?? "?"} — Service not found ({local})";
            }
        }
#endif

        if (state.InstallMode == HomeserverInstallMode.Systemd)
        {
            var active = IsSystemdUnitActive();
            return $"Installed v{state.InstalledVersion ?? "?"} — systemd {(active ? "active" : "inactive")} ({local})";
        }

        var running = IsHostProcessRunning();
        return $"Installed v{state.InstalledVersion ?? "?"} — User session {(running ? "running" : "stopped")} ({local})";
    }

    // ── feed / download ──────────────────────────────────────────────────

    private async Task<HomeserverFeedManifest?> GetFeedManifestAsync(CancellationToken ct)
    {
        var url = OperatingSystem.IsLinux()
            ? AppServerOptions.HomeserverLinuxManifestUrl
            : AppServerOptions.HomeserverWinManifestUrl;
        try
        {
            var manifest = await _http.GetFromJsonAsync<HomeserverFeedManifest>(url, ct);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return null;

            if (string.IsNullOrWhiteSpace(manifest.Url))
            {
                var rid = OperatingSystem.IsLinux() ? "linux-x64" : "win-x64";
                var fileName = string.IsNullOrWhiteSpace(manifest.FileName)
                    ? $"homeserver-{rid}-{manifest.Version}.zip"
                    : manifest.FileName;
                manifest.Url = AppServerOptions.GitHubLatestDownloadUrl(fileName);
            }

            return manifest;
        }
        catch (Exception ex)
        {
            AppendInstallLog($"Failed to read feed {url}", ex);
            _logger.LogWarning(ex, "[Homeserver] Failed to read feed {Url}", url);
            throw new InvalidOperationException(
                $"Could not read Home Server package list from {url}: {ex.Message}", ex);
        }
    }

    private async Task<string> DownloadPackageAsync(HomeserverFeedManifest manifest, CancellationToken ct)
    {
        var url = manifest.Url
            ?? throw new InvalidOperationException("Manifest has no package URL.");
        Directory.CreateDirectory(HomeserverPaths.RootDirectory);
        var zipPath = Path.Combine(Path.GetTempPath(), $"wizionic-homeserver-{manifest.Version}-{Guid.NewGuid():N}.zip");

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

    private static bool IsProgramDataRoot() =>
        string.Equals(
            Path.GetFullPath(HomeserverPaths.RootDirectory).TrimEnd('\\', '/'),
            Path.GetFullPath(HomeserverPaths.WindowsProgramDataRoot).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAccessFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException;

    private static void ExtractPackage(string zipPath, string targetDir)
    {
        var staging = targetDir + ".staging";
        DeleteDirectoryRobust(staging);
        Directory.CreateDirectory(staging);

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        var entries = Directory.GetFileSystemEntries(staging);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            var inner = entries[0];
            var unwrap = staging + ".unwrap";
            DeleteDirectoryRobust(unwrap);
            Directory.Move(inner, unwrap);
            DeleteDirectoryRobust(staging);
            staging = unwrap;
        }

        if (Directory.Exists(targetDir))
        {
            var backup = targetDir + ".old";
            DeleteDirectoryRobust(backup);
            Directory.Move(targetDir, backup);
            try
            {
                Directory.Move(staging, targetDir);
                try { DeleteDirectoryRobust(backup); } catch { /* leftover .old is ok */ }
            }
            catch
            {
                if (Directory.Exists(targetDir))
                    DeleteDirectoryRobust(targetDir);
                Directory.Move(backup, targetDir);
                throw;
            }
        }
        else
        {
            Directory.Move(staging, targetDir);
        }

        if (!File.Exists(HomeserverPaths.HostExecutablePath) &&
            !File.Exists(Path.Combine(targetDir, "App.dll")))
        {
            throw new InvalidOperationException(
                $"Package did not contain host entrypoint at {HomeserverPaths.HostExecutablePath}");
        }
    }

    private static void EnsureHostExecutable()
    {
        var exe = HomeserverPaths.HostExecutablePath;
        if (!File.Exists(exe))
            return;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // Ensure +x for self-contained native host binary
                Process.Start(new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{exe}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })?.WaitForExit(5000);
            }
            catch
            {
                // best effort
            }
        }
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
              "Urls": "http://0.0.0.0:{{port}}",
              "Homeserver": {
                "AllowHttpCookies": true
              },
              "ConnectionStrings": {
                "DefaultConnection": "Data Source={{dbPath}}"
              },
              "AiProviders": {
                "Proxied": []
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
                    "Url": "http://0.0.0.0:{{port}}"
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
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            if (await TryInstallAndStartWindowsServiceAsync(ct))
                return HomeserverInstallMode.WindowsService;

            _logger.LogWarning("[Homeserver] Service install failed or elevation denied — using user-session fallback.");
            StartUserSessionHost();
            InstallUserStartupShortcut();
            return HomeserverInstallMode.UserSession;
        }
#endif

        if (OperatingSystem.IsLinux())
        {
            if (await TryInstallAndStartSystemdAsync(ct))
                return HomeserverInstallMode.Systemd;

            _logger.LogWarning("[Homeserver] systemd install failed or elevation denied — using user-session fallback.");
            StartUserSessionHost();
            InstallUserStartupShortcut();
            return HomeserverInstallMode.UserSession;
        }

        StartUserSessionHost();
        InstallUserStartupShortcut();
        return HomeserverInstallMode.UserSession;
    }

#if WINDOWS
    private async Task<bool> TryInstallAndStartWindowsServiceAsync(CancellationToken ct)
    {
        try
        {
            var destRoot = HomeserverPaths.WindowsProgramDataRoot;
            var srcRoot = HomeserverPaths.RootDirectory;
            var destExe = Path.Combine(destRoot, "app", "App.exe");
            var copyFiles = !PathsEqual(srcRoot, destRoot);
            var script = BuildWindowsServiceInstallScript(srcRoot, destRoot, destExe, copyFiles);
            AppendInstallLog($"One-shot elevated service install copy={copyFiles} src={srcRoot} dest={destRoot}");
            var ok = await RunElevatedCmdScriptAsync(script, ct);
            if (!ok)
            {
                AppendInstallLog("Elevated Home Server service install was cancelled or failed.");
                return false;
            }

            if (copyFiles && File.Exists(destExe))
                HomeserverPaths.ResetWindowsRootCache();

            await Task.Delay(1500, ct);
            using var sc = new ServiceController(HomeserverPaths.ServiceName);
            sc.Refresh();
            var running = sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            AppendInstallLog($"Service status after one-shot install: {sc.Status}");
            return running;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Windows service path failed");
            AppendInstallLog("Windows service path failed", ex);
            return false;
        }
    }

    private static string BuildWindowsServiceInstallScript(
        string srcRoot, string destRoot, string destExe, bool copyFiles)
    {
        var binPath = "\"" + destExe + "\"";
        var copyBlock = copyFiles
            ? $"""
                takeown /f "{destRoot}" /r /d y
                icacls "{destRoot}" /grant *S-1-5-32-544:F /t /c
                rmdir /s /q "{destRoot}"
                mkdir "{destRoot}"
                robocopy "{srcRoot}" "{destRoot}" /E /NFL /NDL /NJH /NJS /nc /ns /np
                """
            : "";
        return $"""
            sc.exe stop {HomeserverPaths.ServiceName}
            sc.exe delete {HomeserverPaths.ServiceName}
            {copyBlock}
            sc.exe create {HomeserverPaths.ServiceName} binPath= {binPath} start= auto DisplayName= "{HomeserverPaths.ServiceDisplayName}"
            sc.exe description {HomeserverPaths.ServiceName} "Wizionic local login server and website"
            sc.exe config {HomeserverPaths.ServiceName} binPath= {binPath} start= auto
            reg.exe add "HKLM\SYSTEM\CurrentControlSet\Services\{HomeserverPaths.ServiceName}" /v Environment /t REG_MULTI_SZ /d "ASPNETCORE_URLS=http://0.0.0.0:{HomeserverPaths.DefaultPort}\0APP_HOMESERVER=1" /f
            netsh advfirewall firewall delete rule name="{HomeserverPaths.FirewallRuleName}"
            netsh advfirewall firewall add rule name="{HomeserverPaths.FirewallRuleName}" dir=in action=allow protocol=TCP localport={HomeserverPaths.DefaultPort} profile=any enable=yes
            sc.exe start {HomeserverPaths.ServiceName}
            """;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private async Task DeleteWindowsServiceAsync(CancellationToken ct)
    {
        try
        {
            await RunElevatedWindowsAsync("sc.exe", $"stop {HomeserverPaths.ServiceName}", ct);
        }
        catch { /* ignore */ }

        await RunElevatedWindowsAsync("sc.exe", $"delete {HomeserverPaths.ServiceName}", ct);
    }
#endif

    private async Task<bool> TryInstallAndStartSystemdAsync(CancellationToken ct)
    {
        try
        {
            var unitPath = Path.Combine(Path.GetTempPath(), HomeserverPaths.SystemdUnitFileName);
            var userName = Environment.UserName;
            var exe = HomeserverPaths.HostExecutablePath;
            var workDir = HomeserverPaths.AppDirectory;
            // Prefer native host; fall back to dotnet App.dll if only DLL published.
            var execStart = File.Exists(exe)
                ? exe
                : $"dotnet {Path.Combine(workDir, "App.dll")}";

            var unit = $"""
                [Unit]
                Description={HomeserverPaths.ServiceDisplayName}
                After=network-online.target
                Wants=network-online.target

                [Service]
                Type=notify
                User={userName}
                WorkingDirectory={workDir}
                ExecStart={execStart}
                Restart=on-failure
                RestartSec=5
                Environment=ASPNETCORE_ENVIRONMENT=Production
                Environment=DOTNET_PrintStackToConsoleOnException=1
                Environment=APP_HOMESERVER=1
                Environment=ASPNETCORE_URLS=http://0.0.0.0:{HomeserverPaths.DefaultPort}
                # HomeserverPaths.AppsettingsPath is under the user's LocalApplicationData
                # and is loaded by the host when present.

                [Install]
                WantedBy=multi-user.target
                """;
            await File.WriteAllTextAsync(unitPath, unit, ct);

            var dest = $"/etc/systemd/system/{HomeserverPaths.SystemdUnitFileName}";
            var script = $"""
                set -e
                cp '{unitPath}' '{dest}'
                systemctl daemon-reload
                systemctl enable '{HomeserverPaths.SystemdUnitName}.service'
                systemctl restart '{HomeserverPaths.SystemdUnitName}.service' || systemctl start '{HomeserverPaths.SystemdUnitName}.service'
                """;

            if (!await RunElevatedLinuxAsync(script, ct, timeoutMs: 60_000))
                return false;

            await Task.Delay(1500, ct);
            return IsSystemdUnitActive();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] systemd path failed");
            return false;
        }
    }

    private async Task DeleteSystemdUnitAsync(CancellationToken ct)
    {
        var script = $"""
            systemctl stop '{HomeserverPaths.SystemdUnitName}.service' 2>/dev/null || true
            systemctl disable '{HomeserverPaths.SystemdUnitName}.service' 2>/dev/null || true
            rm -f '/etc/systemd/system/{HomeserverPaths.SystemdUnitFileName}'
            systemctl daemon-reload 2>/dev/null || true
            """;
        await RunElevatedLinuxAsync(script, ct, timeoutMs: 60_000);
    }

    private static bool IsSystemdUnitActive()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = $"is-active {HomeserverPaths.SystemdUnitName}.service",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return output.Equals("active", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task StartHostAsync(HomeserverInstallMode mode, CancellationToken ct)
    {
#if WINDOWS
        if (mode == HomeserverInstallMode.WindowsService)
        {
            await RunElevatedWindowsAsync("sc.exe", $"start {HomeserverPaths.ServiceName}", ct);
            return;
        }
#endif
        if (mode == HomeserverInstallMode.Systemd)
        {
            await RunElevatedLinuxAsync(
                $"systemctl start '{HomeserverPaths.SystemdUnitName}.service'",
                ct,
                timeoutMs: 30_000);
            return;
        }

        StartUserSessionHost();
    }

    private async Task StopHostAsync(HomeserverInstallMode mode, CancellationToken ct)
    {
#if WINDOWS
        if (mode == HomeserverInstallMode.WindowsService)
        {
            try
            {
                using var sc = new ServiceController(HomeserverPaths.ServiceName);
                if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    await RunElevatedWindowsAsync("sc.exe", $"stop {HomeserverPaths.ServiceName}", ct);
                    await Task.Delay(1000, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[Homeserver] Stop service note");
            }
            return;
        }
#endif
        if (mode == HomeserverInstallMode.Systemd)
        {
            await RunElevatedLinuxAsync(
                $"systemctl stop '{HomeserverPaths.SystemdUnitName}.service' || true",
                ct,
                timeoutMs: 30_000);
            return;
        }

        KillHostProcesses();
    }

    private void StartUserSessionHost()
    {
        var exe = HomeserverPaths.HostExecutablePath;
        var dll = Path.Combine(HomeserverPaths.AppDirectory, "App.dll");
        if (!File.Exists(exe) && !File.Exists(dll))
            throw new InvalidOperationException("Home Server executable not found.");

        if (IsHostProcessRunning())
            return;

        ProcessStartInfo psi;
        if (File.Exists(exe))
        {
            psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = HomeserverPaths.AppDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{dll}\"",
                WorkingDirectory = HomeserverPaths.AppDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["APP_HOMESERVER"] = "1";
        psi.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{HomeserverPaths.DefaultPort}";
        Process.Start(psi);
    }

    private static bool IsHostProcessRunning()
    {
        try
        {
            return Process.GetProcessesByName("App")
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
        var appRoots = HomeserverPaths.AllRootDirectories
            .Select(r => Path.Combine(r, "app") + Path.DirectorySeparatorChar)
            .ToArray();

        foreach (var p in Process.GetProcessesByName("App"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null &&
                    appRoots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
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

    private static void InstallUserStartupShortcut()
    {
        if (OperatingSystem.IsWindows())
        {
            var cmdPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Wizionic Home Server.cmd");
            var cmd = $"""
                @echo off
                start "" /D "{HomeserverPaths.AppDirectory}" "{HomeserverPaths.HostExecutablePath}"
                """;
            File.WriteAllText(cmdPath, cmd.Trim() + Environment.NewLine);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            var desktopPath = HomeserverPaths.LinuxAutostartDesktopPath;
            Directory.CreateDirectory(Path.GetDirectoryName(desktopPath)!);
            var exec = File.Exists(HomeserverPaths.HostExecutablePath)
                ? HomeserverPaths.HostExecutablePath
                : $"dotnet {Path.Combine(HomeserverPaths.AppDirectory, "App.dll")}";
            var desktop = $"""
                [Desktop Entry]
                Type=Application
                Name=Wizionic Home Server
                Comment=Local Wizionic login server and website
                Exec={exec}
                Path={HomeserverPaths.AppDirectory}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """;
            File.WriteAllText(desktopPath, desktop);
        }
    }

    private static void RemoveUserStartupShortcut()
    {
        if (OperatingSystem.IsWindows())
        {
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "Wizionic Home Server.cmd"));
        }

        if (OperatingSystem.IsLinux())
            TryDelete(HomeserverPaths.LinuxAutostartDesktopPath);
    }

    private async Task<bool> RetargetMauiToLocalHomeserverAsync(string baseUrl)
    {
        try
        {
            if (_serverEndpoint is not null)
            {
                var restartRequired = await _serverEndpoint.SetBaseUrlAsync(baseUrl);
                _logger.LogInformation("[Homeserver] Login server set via endpoint to {BaseUrl}", baseUrl);
                return restartRequired;
            }

            var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
            Directory.CreateDirectory(MauiAppData.Directory);
            var updateFeed = AppServerOptions.GitHubRepoUrl;
            var json = JsonSerializer.Serialize(new
            {
                AppServer = new
                {
                    BaseUrl = baseUrl,
                    UpdateFeedUrl = updateFeed
                }
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            _logger.LogInformation("[Homeserver] Wrote MAUI local override {Path} BaseUrl={BaseUrl}", path, baseUrl);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Could not retarget MAUI BaseUrl");
            return true;
        }
    }

    // ── elevation helpers ────────────────────────────────────────────────

#if WINDOWS
    private async Task<bool> RunElevatedCmdScriptAsync(string scriptBody, CancellationToken ct)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"wizionic-elev-{Guid.NewGuid():N}.exit");
        TryDelete(marker);
        var bat = Path.Combine(Path.GetTempPath(), $"wizionic-elev-{Guid.NewGuid():N}.cmd");
        var batBody = $"""
            @echo off
            {scriptBody}
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
            return File.Exists(marker);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(bat);
            TryDelete(marker);
        }
    }

    private static async Task<bool> RunElevatedWindowsAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        bool acceptAnyExitCode = false)
    {
        var marker = Path.Combine(Path.GetTempPath(), $"wizionic-elev-{Guid.NewGuid():N}.exit");
        TryDelete(marker);

        var bat = Path.Combine(Path.GetTempPath(), $"wizionic-elev-{Guid.NewGuid():N}.cmd");
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

            if (!File.Exists(marker))
                return false;

            if (acceptAnyExitCode)
                return true;

            var codeText = (await File.ReadAllTextAsync(marker, ct)).Trim();
            return int.TryParse(codeText, out var code) && code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            TryDelete(bat);
            TryDelete(marker);
        }
    }
#endif

    /// <summary>
    /// Runs a shell script with elevation (pkexec preferred, then sudo).
    /// </summary>
    private async Task<bool> RunElevatedLinuxAsync(string scriptBody, CancellationToken ct, int timeoutMs)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"wizionic-elev-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/bash\n" + scriptBody + "\n", ct);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(3000);

            // Prefer graphical polkit elevation when available.
            foreach (var (file, args) in new[]
                     {
                         ("pkexec", $"bash \"{scriptPath}\""),
                         ("sudo", $"-n bash \"{scriptPath}\""),
                         ("sudo", $"bash \"{scriptPath}\"")
                     })
            {
                if (!CommandExists(file))
                    continue;

                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc is null)
                        continue;

                    using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
                    var finished = await Task.Run(() => proc.WaitForExit(timeoutMs), ct);
                    if (!finished)
                    {
                        try { proc.Kill(true); } catch { }
                        continue;
                    }

                    if (proc.ExitCode == 0)
                        return true;

                    var err = await proc.StandardError.ReadToEndAsync(ct);
                    _logger.LogDebug("[Homeserver] Elevated {File} exited {Code}: {Err}", file, proc.ExitCode, err);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Homeserver] Elevation via {File} failed", file);
                }
            }

            return false;
        }
        finally
        {
            TryDelete(scriptPath);
        }
    }

    private static bool CommandExists(string name)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = name,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
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

    private static bool NeedsLanBindRewrite(string port)
    {
        var path = HomeserverPaths.AppsettingsPath;
        if (!File.Exists(path))
            return true;

        var text = File.ReadAllText(path);
        if (text.Contains("127.0.0.1", StringComparison.Ordinal) ||
            text.Contains("localhost:", StringComparison.OrdinalIgnoreCase))
            return true;

        var p = string.IsNullOrWhiteSpace(port) ? HomeserverPaths.DefaultPort : port.Trim();
        // Installed 0.2.x hosts honor "Urls" more reliably than Kestrel:Endpoints with "*".
        if (text.Contains($"\"Urls\": \"http://0.0.0.0:{p}\"", StringComparison.Ordinal) ||
            text.Contains($"http://0.0.0.0:{p}", StringComparison.Ordinal))
            return false;

        return true;
    }

#if WINDOWS
    private async Task<bool> EnsureWindowsServiceListenEnvAsync(string port, CancellationToken ct)
    {
        var p = string.IsNullOrWhiteSpace(port) ? HomeserverPaths.DefaultPort : port.Trim();
        if (WindowsServiceListenEnvConfigured(p))
            return false;

        var args =
            $"add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{HomeserverPaths.ServiceName}\" " +
            $"/v Environment /t REG_MULTI_SZ " +
            $"/d \"ASPNETCORE_URLS=http://0.0.0.0:{p}\\0APP_HOMESERVER=1\" /f";
        var ok = await RunElevatedWindowsAsync("reg.exe", args, ct);
        if (!ok)
        {
            _logger.LogWarning(
                "[Homeserver] Could not set ASPNETCORE_URLS on the Windows Service. LAN bind may still be loopback.");
            return false;
        }

        return true;
    }

    private static bool WindowsServiceListenEnvConfigured(string port)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments =
                    $"query \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\{HomeserverPaths.ServiceName}\" /v Environment",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 &&
                   output.Contains($"ASPNETCORE_URLS=http://0.0.0.0:{port}", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
#endif

    private async Task<bool> EnsureLanFirewallAsync(CancellationToken ct)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            if (WindowsFirewallRuleAllowsLan())
                return false;

            var args =
                $"advfirewall firewall delete rule name=\"{HomeserverPaths.FirewallRuleName}\" & " +
                $"netsh advfirewall firewall add rule name=\"{HomeserverPaths.FirewallRuleName}\" " +
                $"dir=in action=allow protocol=TCP localport={HomeserverPaths.DefaultPort} profile=any enable=yes";
            var ok = await RunElevatedWindowsAsync("netsh", args, ct, acceptAnyExitCode: true);
            if (!ok || !WindowsFirewallRuleAllowsLan())
            {
                _logger.LogWarning(
                    "[Homeserver] Could not add Windows Firewall rule. Other devices on the LAN may not connect.");
                return false;
            }
            return true;
        }
#endif
        if (OperatingSystem.IsLinux())
            return await EnsureLinuxFirewallAsync(ct);

        return false;
    }

    private async Task RemoveLanFirewallAsync(CancellationToken ct)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            await RunElevatedWindowsAsync(
                "netsh",
                $"advfirewall firewall delete rule name=\"{HomeserverPaths.FirewallRuleName}\"",
                ct,
                acceptAnyExitCode: true);
            return;
        }
#endif
        if (OperatingSystem.IsLinux() && CommandExists("ufw"))
        {
            await RunElevatedLinuxAsync(
                $"ufw delete allow {HomeserverPaths.DefaultPort}/tcp || true",
                ct,
                timeoutMs: 15_000);
        }
    }

#if WINDOWS
    private static bool WindowsFirewallRuleAllowsLan()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall show rule name=\"{HomeserverPaths.FirewallRuleName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc is null)
                return false;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0 ||
                !output.Contains(HomeserverPaths.FirewallRuleName, StringComparison.OrdinalIgnoreCase))
                return false;

            // Home Wi-Fi is often classified as Public; Private-only rules do not apply.
            return output.Contains("Public", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("Any", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("All", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
#endif

    private async Task<bool> EnsureLinuxFirewallAsync(CancellationToken ct)
    {
        if (!CommandExists("ufw"))
            return false;

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ufw",
                Arguments = "status",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc is null)
                return false;
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            proc.WaitForExit(5000);
            if (!output.Contains("Status: active", StringComparison.OrdinalIgnoreCase))
                return false;
            if (output.Contains(HomeserverPaths.DefaultPort, StringComparison.Ordinal))
                return false;

            return await RunElevatedLinuxAsync(
                $"ufw allow {HomeserverPaths.DefaultPort}/tcp comment 'Wizionic Home Server'",
                ct,
                timeoutMs: 15_000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Homeserver] ufw probe failed");
            return false;
        }
    }

    /// <summary>
    /// Incomplete uninstall can leave homeserver.db (and a running process) while
    /// state.json says "not installed". A later wizard install would skip first-admin.
    /// </summary>
    private async Task ClearStaleInstallAsync(CancellationToken ct)
    {
        try
        {
            await StopHostAsync(HomeserverInstallMode.WindowsService, ct);
        }
        catch { /* best effort */ }

        try
        {
            await StopHostAsync(HomeserverInstallMode.UserSession, ct);
        }
        catch { /* best effort */ }

        try { await Task.Delay(400, ct); }
        catch { /* ignore */ }

        KillHostProcesses();
        foreach (var root in HomeserverPaths.AllRootDirectories)
        {
            TryDeleteDirectory(Path.Combine(root, "app"));
            TryDeleteDirectory(Path.Combine(root, "app.old"));
            TryDeleteDirectory(Path.Combine(root, "app.staging"));
        }

        TryDelete(HomeserverPaths.DatabasePath);
        TryDeleteDirectory(HomeserverPaths.DataDirectory);
        TryDelete(HomeserverPaths.StateFilePath);
    }

    private void DeleteInstallRoot()
    {
        TryDeleteDirectory(HomeserverPaths.AppDirectory);
        TryDeleteDirectory(HomeserverPaths.DataDirectory);
        TryDelete(HomeserverPaths.StateFilePath);
        TryDelete(HomeserverPaths.AppsettingsPath);
        TryDelete(HomeserverPaths.PendingUpdateFlagPath);
        TryDeleteDirectory(HomeserverPaths.RootDirectory);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            DeleteDirectoryRobust(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Could not fully delete {Path}", path);
        }
    }

    private static void DeleteDirectoryRobust(string path)
    {
        if (!Directory.Exists(path))
            return;

        Exception? last = null;
        for (var i = 0; i < 6; i++)
        {
            try
            {
                NormalizeDirectoryAttributes(path);
                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(200 * (i + 1));
            }
        }

        NormalizeDirectoryAttributes(path);
        if (last is not null)
            throw last;
        Directory.Delete(path, recursive: true);
    }

    private static void NormalizeDirectoryAttributes(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            dir.Attributes = FileAttributes.Normal;
            foreach (var info in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                try { info.Attributes = FileAttributes.Normal; }
                catch { /* skip locked entries */ }
            }
        }
        catch
        {
            // ignore
        }
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

    internal static string InstallLogPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(local, "Wizionic", "userdata");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "homeserver-install.log");
        }
    }

    private void AppendInstallLog(string message, Exception? ex = null)
    {
        try
        {
            var line = $"{DateTimeOffset.Now:u} {message}";
            if (ex is not null)
                line += Environment.NewLine + ex;
            File.AppendAllText(InstallLogPath, line + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // ignore
        }
    }

    private static string FormatException(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message))
                parts.Add(e.Message);
        }

        return parts.Count == 0 ? ex.GetType().Name : string.Join(" → ", parts);
    }
}
