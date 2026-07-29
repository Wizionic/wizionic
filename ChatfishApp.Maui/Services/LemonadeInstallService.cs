using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ChatfishApp.Core.Lemonade;
using ChatfishApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Downloads Lemonade MSI, installs (silent when possible), pulls models via CLI, configures KeyStore.
/// Never re-downloads the MSI when Lemonade is already present on the machine.
/// </summary>
public sealed class LemonadeInstallService : ILemonadeInstallService
{
    public const string MsiDownloadUrl =
        "https://github.com/lemonade-sdk/lemonade/releases/latest/download/lemonade.msi";

    /// <summary>Per-model pull timeout (downloads can be large but must not hang forever).</summary>
    private const int PullTimeoutMs = 20 * 60 * 1000;

    private readonly ILogger<LemonadeInstallService> _logger;
    private readonly HttpClient _http;

    private static readonly LemonadeInstallModelChoice[] Models =
    [
        new()
        {
            Id = "Qwen3-0.6B-GGUF",
            DisplayName = "Qwen3 0.6B",
            Description = "Very small chat model — good first install.",
            DefaultSelected = true
        },
        new()
        {
            Id = "Gemma-4-E2B-it-GGUF",
            DisplayName = "Gemma 4 E2B",
            Description = "Small multimodal-friendly model (if listed in your Lemonade catalog).",
            DefaultSelected = false
        },
        new()
        {
            Id = "Llama-3.2-1B-Instruct-CPU",
            DisplayName = "Llama 3.2 1B (CPU)",
            Description = "CPU-friendly baseline instruct model.",
            DefaultSelected = false
        }
    ];

    /// <summary>Process names seen in Task Manager for a full Lemonade install.</summary>
    private static readonly string[] LemonadeProcessNames =
    [
        "lemonade",
        "lemonade-app",
        "lemonade-server",
        "Lemonade Server",
        "LemonadeApp",
        "Lemonade"
    ];

    public LemonadeInstallService(ILogger<LemonadeInstallService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(LemonadeInstallService));
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public bool IsSupported =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public bool IsInstalled => DetectInstallation() is not LemonadeDetection.NotFound;

    public string DefaultBaseUrl => "http://localhost:13305";

    public IReadOnlyList<LemonadeInstallModelChoice> AvailableInstallModels => Models;

    /// <summary>Human-readable why we think Lemonade is present (for wizard UI).</summary>
    public string GetInstalledStatusDescription()
    {
        var d = DetectInstallation();
        return d switch
        {
            LemonadeDetection.NotFound => "Not detected.",
            LemonadeDetection.ServerResponding => "Lemonade server is responding on port 13305.",
            LemonadeDetection.ProcessRunning => "Lemonade process is running (lemonade-app / lemonade.exe / Lemonade Server).",
            LemonadeDetection.CliOnDisk => $"Lemonade CLI found at {ResolveLemonadeCli()}.",
            LemonadeDetection.InstallFolder => "Lemonade install folder found on disk.",
            _ => "Lemonade appears to be installed."
        };
    }

    public async Task<LemonadeInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return LemonadeInstallResult.Fail("Lemonade install is only supported on Windows and Linux.");

        try
        {
            var detection = DetectInstallation();
            if (detection is not LemonadeDetection.NotFound)
            {
                var detail = GetInstalledStatusDescription();
                progress?.Report($"Skipping reinstall — {detail}");
                _logger.LogInformation("[Lemonade] Skipping install; detection={Detection} detail={Detail}", detection, detail);
                if (detection is not LemonadeDetection.ServerResponding)
                    await TryEnsureServerRunningAsync(progress, cancellationToken);
                return LemonadeInstallResult.Ok($"Lemonade already installed ({detail}). Skipped reinstall.");
            }

            if (OperatingSystem.IsLinux())
                return await InstallServerLinuxAsync(progress, cancellationToken);

            return await InstallServerWindowsAsync(progress, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Lemonade] Install failed");
            return LemonadeInstallResult.Fail($"Lemonade install failed: {ex.Message}");
        }
    }

    private async Task<LemonadeInstallResult> InstallServerWindowsAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Downloading Lemonade installer…");
        var msiPath = Path.Combine(Path.GetTempPath(), $"lemonade-{Guid.NewGuid():N}.msi");
        await using (var remote = await _http.GetStreamAsync(MsiDownloadUrl, cancellationToken))
        await using (var file = File.Create(msiPath))
            await remote.CopyToAsync(file, cancellationToken);

        progress?.Report("Installing Lemonade (may prompt for permission)…");
        var silentOk = await RunProcessAsync(
            "msiexec.exe",
            $"/i \"{msiPath}\" /qn /norestart",
            elevate: true,
            cancellationToken,
            timeoutMs: 15 * 60 * 1000);

        if (!silentOk)
        {
            progress?.Report("Silent install failed or was cancelled — opening interactive installer…");
            await RunProcessAsync(
                "msiexec.exe",
                $"/i \"{msiPath}\"",
                elevate: true,
                cancellationToken,
                timeoutMs: 15 * 60 * 1000);
            await Task.Delay(3000, cancellationToken);
        }

        try { File.Delete(msiPath); } catch { /* ignore */ }

        await Task.Delay(2000, cancellationToken);
        if (DetectInstallation() is LemonadeDetection.NotFound)
        {
            return LemonadeInstallResult.Fail(
                "Lemonade installer finished but Lemonade was not detected yet. " +
                "Finish the MSI if it is still open, then re-run this step or open the Lemonade app once.");
        }

        await TryEnsureServerRunningAsync(progress, cancellationToken);
        return LemonadeInstallResult.Ok("Lemonade installed.");
    }

    private async Task<LemonadeInstallResult> InstallServerLinuxAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // 1) Prefer snap (cross-distro)
        if (CommandExists("snap"))
        {
            progress?.Report("Installing Lemonade via snap (lemonade-server)…");
            var snap = await RunElevatedShellAsync(
                "snap install lemonade-server",
                cancellationToken,
                timeoutMs: 15 * 60 * 1000);
            if (snap.Ok || DetectInstallation() is not LemonadeDetection.NotFound)
            {
                await TryEnsureServerRunningAsync(progress, cancellationToken);
                return LemonadeInstallResult.Ok("Lemonade installed (snap).");
            }

            progress?.Report($"Snap install failed ({snap.Detail}). Trying apt/PPA…");
        }

        // 2) Ubuntu/Debian apt + official PPA
        if (CommandExists("apt-get") || CommandExists("apt"))
        {
            progress?.Report("Installing Lemonade via apt (PPA lemonade-team/stable)…");
            var aptScript = """
                set -e
                if command -v add-apt-repository >/dev/null 2>&1; then
                  add-apt-repository -y ppa:lemonade-team/stable || true
                fi
                apt-get update -y
                DEBIAN_FRONTEND=noninteractive apt-get install -y lemonade-server || apt-get install -y lemonade
                """;
            var apt = await RunElevatedShellAsync(aptScript, cancellationToken, timeoutMs: 20 * 60 * 1000);
            if (apt.Ok || DetectInstallation() is not LemonadeDetection.NotFound)
            {
                // Best-effort start common service names
                await RunElevatedShellAsync(
                    "systemctl enable --now lemonade-server 2>/dev/null || systemctl enable --now lemond 2>/dev/null || true",
                    cancellationToken,
                    timeoutMs: 30_000);
                await TryEnsureServerRunningAsync(progress, cancellationToken);
                return LemonadeInstallResult.Ok("Lemonade installed (apt).");
            }
        }

        return LemonadeInstallResult.Fail(
            "Could not install Lemonade automatically. Install manually, then re-run this step:\n" +
            "  sudo snap install lemonade-server\n" +
            "  # or on Ubuntu: sudo add-apt-repository ppa:lemonade-team/stable && sudo apt install lemonade-server\n" +
            "See https://lemonade-server.ai/docs/guide/install/");
    }

    public async Task<LemonadeInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var models = modelIds.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        if (models.Count == 0)
            return LemonadeInstallResult.Ok("No models selected to pull.");

        var cli = ResolveLemonadeCli();
        if (cli is null)
        {
            // Server may still be running without CLI on PATH — fail clearly.
            if (ProbeServerRunning())
            {
                return LemonadeInstallResult.Fail(
                    "Lemonade server is running but the lemonade CLI was not found on PATH. " +
                    "Pull models from the Lemonade app Model Manager, or reinstall Lemonade so the CLI is available.");
            }

            return LemonadeInstallResult.Fail("Lemonade CLI not found. Install Lemonade first.");
        }

        if (!ProbeServerRunning())
            await TryEnsureServerRunningAsync(progress, cancellationToken);

        var failures = new List<string>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Pulling model {model} (up to {PullTimeoutMs / 60_000} min; can be slow on first download)…");

            var (ok, detail) = await RunProcessWithOutputAsync(
                cli,
                $"pull {QuoteArg(model)}",
                cancellationToken,
                timeoutMs: PullTimeoutMs);

            if (!ok)
            {
                failures.Add(model);
                progress?.Report($"Pull failed or timed out for {model}. {detail}");
                _logger.LogWarning("[Lemonade] Pull failed for {Model}: {Detail}", model, detail);
            }
            else
            {
                progress?.Report($"Pulled {model}.");
            }
        }

        if (failures.Count == models.Count)
            return LemonadeInstallResult.Fail(
                "All model pulls failed or timed out. Check Lemonade is running and model names are valid. " +
                "You can pull models later from the Lemonade app.");

        if (failures.Count > 0)
            return LemonadeInstallResult.Ok($"Pulled some models. Failed: {string.Join(", ", failures)}");

        return LemonadeInstallResult.Ok($"Pulled {models.Count} model(s).");
    }

    public async Task<LemonadeInstallResult> ConfigureChatfishAsync(
        HttpClient http,
        IKeyStore keyStore,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await keyStore.SetLemonadeBaseUrlAsync(DefaultBaseUrl, cancellationToken);
            await keyStore.RefreshLemonadeModelsFromServerAsync(http, DefaultBaseUrl, null, cancellationToken);
            var chat = keyStore.LemonadeModelSettingsList.FirstOrDefault(m => m.IsChatEligible);
            if (chat is not null)
                await keyStore.SetLastSelectedModelAsync($"lemonade/{chat.Name}", cancellationToken);

            return LemonadeInstallResult.Ok(
                $"Chatfish configured for Lemonade at {DefaultBaseUrl}" +
                (chat is not null ? $" (selected {chat.Name})." : "."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Lemonade] Configure Chatfish failed");
            return LemonadeInstallResult.Fail(
                $"Lemonade URL saved, but model refresh failed: {ex.Message}. Open Lemonade settings later to refresh.");
        }
    }

    // ── detection ────────────────────────────────────────────────────────

    private enum LemonadeDetection
    {
        NotFound = 0,
        ServerResponding,
        ProcessRunning,
        CliOnDisk,
        InstallFolder
    }

    private LemonadeDetection DetectInstallation()
    {
        if (ProbeServerRunning())
            return LemonadeDetection.ServerResponding;

        if (IsLemonadeProcessRunning())
            return LemonadeDetection.ProcessRunning;

        if (ResolveLemonadeCli() is not null)
            return LemonadeDetection.CliOnDisk;

        if (FindInstallFolders().Any())
            return LemonadeDetection.InstallFolder;

        return LemonadeDetection.NotFound;
    }

    private static bool IsLemonadeProcessRunning()
    {
        try
        {
            foreach (var name in LemonadeProcessNames)
            {
                // Process.GetProcessesByName expects name without .exe
                var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name[..^4]
                    : name;
                if (Process.GetProcessesByName(bare).Length > 0)
                    return true;
            }
        }
        catch
        {
            // ignore access denied on some processes
        }

        return false;
    }

    private async Task TryEnsureServerRunningAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (ProbeServerRunning())
            return;

        var cli = ResolveLemonadeCli();
        if (cli is null)
        {
            // Try launching lemonade-app if present.
            var app = FindLemonadeAppExe();
            if (app is not null)
            {
                progress?.Report("Starting Lemonade app…");
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = app,
                        UseShellExecute = true
                    });
                    // Wait for API
                    for (var i = 0; i < 20 && !ProbeServerRunning(); i++)
                        await Task.Delay(500, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Lemonade] Could not start lemonade-app");
                }
            }
            return;
        }

        progress?.Report("Checking Lemonade server status…");
        try
        {
            // Prefer non-interactive status; do not hang on GUI.
            await RunProcessWithOutputAsync(cli, "status", ct, timeoutMs: 15_000);
            await Task.Delay(1000, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Lemonade] Could not run status");
        }
    }

    private static bool ProbeServerRunning()
    {
        foreach (var url in new[]
                 {
                     "http://127.0.0.1:13305/api/v1/health",
                     "http://127.0.0.1:13305/v1/models",
                     "http://localhost:13305/api/v1/health",
                     "http://localhost:13305/v1/models"
                 })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = http.GetAsync(url).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    private static string? ResolveLemonadeCli()
    {
        foreach (var c in EnumerateCliCandidates())
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    private static string? FindLemonadeAppExe()
    {
        foreach (var c in EnumerateAppCandidates())
        {
            if (File.Exists(c))
                return c;
        }

        return null;
    }

    private static IEnumerable<string> FindInstallFolders()
    {
        foreach (var dir in EnumerateInstallDirs())
        {
            if (Directory.Exists(dir))
                yield return dir;
        }
    }

    private static IEnumerable<string> EnumerateInstallDirs()
    {
        if (OperatingSystem.IsLinux())
        {
            yield return "/snap/lemonade-server/current";
            yield return "/usr/share/lemonade";
            yield return "/opt/lemonade";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "lemonade");
            yield break;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        yield return Path.Combine(local, "lemonade_server");
        yield return Path.Combine(local, "lemonade");
        yield return Path.Combine(local, "Programs", "lemonade");
        yield return Path.Combine(local, "Programs", "Lemonade");
        yield return Path.Combine(roaming, "lemonade");
        yield return Path.Combine(pf, "Lemonade");
        yield return Path.Combine(pf, "Lemonade Server");
        yield return Path.Combine(pf86, "Lemonade");
        yield return Path.Combine(pf86, "Lemonade Server");
    }

    private static IEnumerable<string> EnumerateCliCandidates()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Path.Combine(dir, "lemonade");
                yield return Path.Combine(dir, "lemonade-server");
            }

            yield return "/snap/bin/lemonade-server";
            yield return "/snap/bin/lemonade";
            yield return "/usr/bin/lemonade";
            yield return "/usr/bin/lemonade-server";
            yield return "/usr/local/bin/lemonade";
            yield return "/usr/local/bin/lemonade-server";

            foreach (var dir in EnumerateInstallDirs())
            {
                yield return Path.Combine(dir, "lemonade");
                yield return Path.Combine(dir, "lemonade-server");
                yield return Path.Combine(dir, "bin", "lemonade");
            }

            yield break;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Path.Combine(dir, "lemonade.exe");
            yield return Path.Combine(dir, "lemonade-server.exe");
        }

        foreach (var dir in EnumerateInstallDirs())
        {
            yield return Path.Combine(dir, "lemonade.exe");
            yield return Path.Combine(dir, "lemonade-server.exe");
            yield return Path.Combine(dir, "bin", "lemonade.exe");
            yield return Path.Combine(dir, "resources", "lemonade.exe");
        }
    }

    private static IEnumerable<string> EnumerateAppCandidates()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in EnumerateInstallDirs())
            {
                yield return Path.Combine(dir, "lemonade-app");
                yield return Path.Combine(dir, "lemonade");
            }

            yield break;
        }

        foreach (var dir in EnumerateInstallDirs())
        {
            yield return Path.Combine(dir, "lemonade-app.exe");
            yield return Path.Combine(dir, "LemonadeApp.exe");
            yield return Path.Combine(dir, "Lemonade Server.exe");
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Programs", "lemonade", "lemonade-app.exe");
    }

    private async Task<(bool Ok, string Detail)> RunElevatedShellAsync(
        string shellCommand,
        CancellationToken ct,
        int timeoutMs)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"chatfish-lemonade-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/bash\n" + shellCommand + "\n", ct);
        try
        {
            await RunProcessWithOutputAsync("chmod", $"+x \"{scriptPath}\"", ct, timeoutMs: 5000);

            foreach (var (file, args) in new[]
                     {
                         ("pkexec", $"bash \"{scriptPath}\""),
                         ("sudo", $"-n bash \"{scriptPath}\""),
                         ("sudo", $"bash \"{scriptPath}\"")
                     })
            {
                if (!CommandExists(file))
                    continue;

                var result = await RunProcessWithOutputAsync(file, args, ct, timeoutMs);
                if (result.Ok)
                    return result;
            }

            return (false, "Elevation failed or cancelled.");
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
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

    // ── process helpers ──────────────────────────────────────────────────

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;

    private async Task<bool> RunProcessAsync(
        string fileName,
        string arguments,
        bool elevate,
        CancellationToken ct,
        int timeoutMs = 600_000)
    {
        var (ok, _) = await RunProcessWithOutputAsync(fileName, arguments, ct, timeoutMs, elevate);
        return ok;
    }

    /// <summary>
    /// Runs a process and always drains stdout/stderr so redirected pipes cannot deadlock
    /// (this is what hung <c>lemonade pull</c> for 15+ minutes).
    /// </summary>
    private async Task<(bool Ok, string Detail)> RunProcessWithOutputAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        int timeoutMs,
        bool elevate = false)
    {
        try
        {
            if (fileName is "lemonade" or "lemonade.exe")
            {
                var resolved = ResolveLemonadeCli();
                if (resolved is not null)
                    fileName = resolved;
            }

            if (elevate && OperatingSystem.IsWindows())
            {
                // Elevated msiexec: cannot reliably redirect; use shell + wait.
                var psiElev = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var elev = Process.Start(psiElev);
                if (elev is null)
                    return (false, "Failed to start elevated process.");

                using var reg = ct.Register(() => { try { elev.Kill(true); } catch { } });
                var done = await Task.Run(() => elev.WaitForExit(timeoutMs > 0 ? timeoutMs : 600_000), ct);
                if (!done)
                {
                    try { elev.Kill(true); } catch { }
                    return (false, "Timed out.");
                }

                return (elev.ExitCode == 0, $"exit {elev.ExitCode}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Pulls can emit non-UTF8 progress; ignore invalid bytes.
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            if (!proc.Start())
                return (false, "Process failed to start.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var reg2 = ct.Register(() => { try { proc.Kill(true); } catch { } });

            var finished = timeoutMs > 0
                ? await Task.Run(() => proc.WaitForExit(timeoutMs), ct)
                : await WaitForExitAsyncCompat(proc, ct);

            if (!finished)
            {
                try { proc.Kill(true); } catch { }
                return (false, $"Timed out after {timeoutMs / 1000}s.");
            }

            // Ensure async readers finish
            try { proc.WaitForExit(2000); } catch { }

            var code = proc.ExitCode;
            var tail = Truncate((stdout.ToString() + "\n" + stderr).Trim(), 400);
            return (code == 0, code == 0 ? "ok" : $"exit {code}: {tail}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // UAC denied or file not found
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Lemonade] Process failed: {File} {Args}", fileName, arguments);
            return (false, ex.Message);
        }
    }

    private static async Task<bool> WaitForExitAsyncCompat(Process proc, CancellationToken ct)
    {
        await proc.WaitForExitAsync(ct);
        return true;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
