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
/// Downloads, installs, updates, and removes the optional Wizionic Home Server
/// (Windows Service / Linux systemd, with user-session fallback).
/// Data lives under a stable path and is never deleted by update/uninstall of binaries.
/// </summary>
public sealed class HomeserverInstallService : IHomeserverInstallService
{
    private readonly string _feedBaseUrl;
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
        _feedBaseUrl = _productionBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            ? "https://wizionic.com"
            : _productionBaseUrl;
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(HomeserverInstallService));
        _http.Timeout = TimeSpan.FromMinutes(15);
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
            progress?.Report("Checking for Home Server package…");
            var manifest = await GetFeedManifestAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "Could not find a Home Server package on the update feed. Deploy the homeserver package first.");

            progress?.Report($"Downloading Home Server {manifest.Version}…");
            var zipPath = await DownloadPackageAsync(manifest, cancellationToken);

            progress?.Report("Installing files…");
            EnsureDataLayout();
            ExtractPackage(zipPath, HomeserverPaths.AppDirectory);
            EnsureHostExecutable();
            WriteHomeserverAppsettings(HomeserverPaths.DefaultPort);
            TryDelete(zipPath);

            progress?.Report("Starting Home Server…");
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

            var modeLabel = mode switch
            {
                HomeserverInstallMode.WindowsService => "Windows Service (starts automatically)",
                HomeserverInstallMode.Systemd => "systemd unit (starts automatically)",
                _ => "user session (starts at logon)"
            };
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

    public async Task UninstallBinariesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return;

        var state = HomeserverState.Load();
        try
        {
            await StopHostAsync(state.InstallMode, cancellationToken);
#if WINDOWS
            if (state.InstallMode == HomeserverInstallMode.WindowsService)
                await DeleteWindowsServiceAsync(cancellationToken);
#endif
            if (state.InstallMode == HomeserverInstallMode.Systemd)
                await DeleteSystemdUnitAsync(cancellationToken);

            RemoveUserStartupShortcut();

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

#if WINDOWS
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
#endif

        if (state.InstallMode == HomeserverInstallMode.Systemd)
        {
            var active = IsSystemdUnitActive();
            return $"Installed v{state.InstalledVersion ?? "?"} — systemd {(active ? "active" : "inactive")} ({state.BaseUrl})";
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
                var rid = OperatingSystem.IsLinux() ? "linux-x64" : "win-x64";
                var fileName = string.IsNullOrWhiteSpace(manifest.FileName)
                    ? $"homeserver-{rid}-{manifest.Version}.zip"
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

    private static void ExtractPackage(string zipPath, string targetDir)
    {
        var staging = targetDir + ".staging";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

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
            var binPath = $"\"{HomeserverPaths.HostExecutablePath}\"";
            var createArgs =
                $"create {HomeserverPaths.ServiceName} binPath= {binPath} start= auto " +
                $"DisplayName= \"{HomeserverPaths.ServiceDisplayName}\"";

            if (!await RunElevatedWindowsAsync("sc.exe", createArgs, ct))
            {
                // Service may already exist
            }
            else
            {
                await RunElevatedWindowsAsync("sc.exe",
                    $"description {HomeserverPaths.ServiceName} \"Wizionic local login server and website\"",
                    ct);
            }

            await RunElevatedWindowsAsync("sc.exe",
                $"config {HomeserverPaths.ServiceName} binPath= {binPath} start= auto",
                ct);

            await RunElevatedWindowsAsync("sc.exe", $"start {HomeserverPaths.ServiceName}", ct, acceptAnyExitCode: true);

            await Task.Delay(1500, ct);
            using var sc = new ServiceController(HomeserverPaths.ServiceName);
            sc.Refresh();
            return sc.Status is ServiceControllerStatus.Running
                or ServiceControllerStatus.StartPending;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Windows service path failed");
            return false;
        }
    }

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
        foreach (var p in Process.GetProcessesByName("App"))
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

            var path = Path.Combine(MauiAppData.Directory, "appsettings.Local.json");
            Directory.CreateDirectory(MauiAppData.Directory);
            var updateFeed = OperatingSystem.IsLinux()
                ? $"{_feedBaseUrl}/releases/linux"
                : $"{_feedBaseUrl}/releases/windows";
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Homeserver] Could not retarget MAUI BaseUrl");
        }
    }

    // ── elevation helpers ────────────────────────────────────────────────

#if WINDOWS
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
