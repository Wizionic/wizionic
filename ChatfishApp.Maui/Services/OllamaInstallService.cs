using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ChatfishApp.Core.Ollama;
using ChatfishApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace ChatfishApp.Maui.Services;

/// <summary>
/// Installs Ollama on Windows (skip if already present), pulls models via CLI, configures KeyStore.
/// </summary>
public sealed class OllamaInstallService : IOllamaInstallService
{
    public const string SetupDownloadUrl = "https://ollama.com/download/OllamaSetup.exe";

    private const int PullTimeoutMs = 20 * 60 * 1000;

    private readonly ILogger<OllamaInstallService> _logger;
    private readonly HttpClient _http;

    private static readonly OllamaInstallModelChoice[] Models =
    [
        new()
        {
            Id = "gemma3:1b",
            DisplayName = "Gemma 3 1B",
            Description = "Very small chat model — good first install.",
            DefaultSelected = true
        },
        new()
        {
            Id = "llama3.2:1b",
            DisplayName = "Llama 3.2 1B",
            Description = "Small general instruct model.",
            DefaultSelected = false
        },
        new()
        {
            Id = "qwen2.5:0.5b",
            DisplayName = "Qwen 2.5 0.5B",
            Description = "Tiny model for quick tests.",
            DefaultSelected = false
        }
    ];

    public OllamaInstallService(ILogger<OllamaInstallService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _http = httpClientFactory.CreateClient(nameof(OllamaInstallService));
        _http.Timeout = TimeSpan.FromMinutes(30);
    }

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool IsInstalled => DetectInstallation() is not OllamaDetection.NotFound;

    public string DefaultBaseUrl => "http://localhost:11434";

    public IReadOnlyList<OllamaInstallModelChoice> AvailableInstallModels => Models;

    public string GetInstalledStatusDescription()
    {
        var d = DetectInstallation();
        return d switch
        {
            OllamaDetection.NotFound => "Not detected.",
            OllamaDetection.ServerResponding => "Ollama API is responding on port 11434.",
            OllamaDetection.ProcessRunning => "Ollama process is running.",
            OllamaDetection.CliOnDisk => $"Ollama CLI found at {ResolveOllamaCli()}.",
            OllamaDetection.InstallFolder => "Ollama install folder found on disk.",
            _ => "Ollama appears to be installed."
        };
    }

    public async Task<OllamaInstallResult> InstallServerAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
            return OllamaInstallResult.Fail("Ollama install is only supported on Windows.");

        try
        {
            var detection = DetectInstallation();
            if (detection is not OllamaDetection.NotFound)
            {
                var detail = GetInstalledStatusDescription();
                progress?.Report($"Skipping Ollama installer — {detail}");
                _logger.LogInformation("[Ollama] Skipping install; detection={Detection}", detection);
                if (detection is not OllamaDetection.ServerResponding)
                    await TryEnsureServerRunningAsync(progress, cancellationToken);
                return OllamaInstallResult.Ok($"Ollama already installed ({detail}). Skipped reinstall.");
            }

            // Prefer winget when available (no large download in-process).
            progress?.Report("Installing Ollama via winget (if available)…");
            var wingetOk = await RunProcessWithOutputAsync(
                "winget",
                "install -e --id Ollama.Ollama --accept-package-agreements --accept-source-agreements --disable-interactivity",
                cancellationToken,
                timeoutMs: 15 * 60 * 1000);

            if (wingetOk.Ok || DetectInstallation() is not OllamaDetection.NotFound)
            {
                await TryEnsureServerRunningAsync(progress, cancellationToken);
                return OllamaInstallResult.Ok("Ollama installed (winget).");
            }

            progress?.Report("Downloading OllamaSetup.exe…");
            var setupPath = Path.Combine(Path.GetTempPath(), $"OllamaSetup-{Guid.NewGuid():N}.exe");
            await using (var remote = await _http.GetStreamAsync(SetupDownloadUrl, cancellationToken))
            await using (var file = File.Create(setupPath))
                await remote.CopyToAsync(file, cancellationToken);

            progress?.Report("Running Ollama installer (may prompt for permission)…");
            // Inno Setup silent flags commonly used by OllamaSetup.
            var silent = await RunProcessWithOutputAsync(
                setupPath,
                "/VERYSILENT /NORESTART",
                cancellationToken,
                timeoutMs: 15 * 60 * 1000,
                elevate: true);

            if (!silent.Ok)
            {
                progress?.Report("Silent install failed — opening interactive installer…");
                await RunProcessWithOutputAsync(setupPath, "", cancellationToken, timeoutMs: 15 * 60 * 1000, elevate: true);
                await Task.Delay(3000, cancellationToken);
            }

            try { File.Delete(setupPath); } catch { /* ignore */ }

            await Task.Delay(2000, cancellationToken);
            if (DetectInstallation() is OllamaDetection.NotFound)
            {
                return OllamaInstallResult.Fail(
                    "Ollama installer finished but Ollama was not detected yet. " +
                    "Finish the installer if it is still open, then re-run this step.");
            }

            await TryEnsureServerRunningAsync(progress, cancellationToken);
            return OllamaInstallResult.Ok("Ollama installed.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ollama] Install failed");
            return OllamaInstallResult.Fail($"Ollama install failed: {ex.Message}");
        }
    }

    public async Task<OllamaInstallResult> PullModelsAsync(
        IEnumerable<string> modelIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var models = modelIds.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToList();
        if (models.Count == 0)
            return OllamaInstallResult.Ok("No models selected to pull.");

        var cli = ResolveOllamaCli();
        if (cli is null)
            return OllamaInstallResult.Fail("Ollama CLI not found. Install Ollama first.");

        if (!ProbeServerRunning())
            await TryEnsureServerRunningAsync(progress, cancellationToken);

        var failures = new List<string>();
        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Pulling Ollama model {model} (up to {PullTimeoutMs / 60_000} min)…");
            var (ok, detail) = await RunProcessWithOutputAsync(
                cli,
                $"pull {QuoteArg(model)}",
                cancellationToken,
                timeoutMs: PullTimeoutMs);

            if (!ok)
            {
                failures.Add(model);
                progress?.Report($"Pull failed or timed out for {model}. {detail}");
            }
            else
            {
                progress?.Report($"Pulled {model}.");
            }
        }

        if (failures.Count == models.Count)
            return OllamaInstallResult.Fail("All Ollama model pulls failed. You can pull later with: ollama pull <model>");

        if (failures.Count > 0)
            return OllamaInstallResult.Ok($"Pulled some models. Failed: {string.Join(", ", failures)}");

        return OllamaInstallResult.Ok($"Pulled {models.Count} model(s).");
    }

    public async Task<OllamaInstallResult> ConfigureChatfishAsync(
        HttpClient http,
        IKeyStore keyStore,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await keyStore.SetOllamaBaseUrlAsync(DefaultBaseUrl, cancellationToken);
            try
            {
                await keyStore.RefreshOllamaModelsFromServerAsync(http, DefaultBaseUrl, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Ollama] Refresh after configure failed");
                return OllamaInstallResult.Ok(
                    $"Ollama URL set to {DefaultBaseUrl}, but model refresh failed: {ex.Message}. Use the Ollama page to refresh later.");
            }

            var first = keyStore.OllamaModelSettingsList.FirstOrDefault();
            if (first is not null)
                await keyStore.SetLastSelectedModelAsync($"ollama/{first.Name}", cancellationToken);

            return OllamaInstallResult.Ok(
                $"Chatfish configured for Ollama at {DefaultBaseUrl}" +
                (first is not null ? $" (selected {first.Name})." : "."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] Configure Chatfish failed");
            return OllamaInstallResult.Fail($"Could not configure Ollama: {ex.Message}");
        }
    }

    private enum OllamaDetection
    {
        NotFound = 0,
        ServerResponding,
        ProcessRunning,
        CliOnDisk,
        InstallFolder
    }

    private OllamaDetection DetectInstallation()
    {
        if (ProbeServerRunning())
            return OllamaDetection.ServerResponding;
        if (IsOllamaProcessRunning())
            return OllamaDetection.ProcessRunning;
        if (ResolveOllamaCli() is not null)
            return OllamaDetection.CliOnDisk;
        if (FindInstallFolders().Any())
            return OllamaDetection.InstallFolder;
        return OllamaDetection.NotFound;
    }

    private static bool IsOllamaProcessRunning()
    {
        try
        {
            foreach (var name in new[] { "ollama", "ollama app", "Ollama" })
            {
                if (Process.GetProcessesByName(name).Length > 0)
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private async Task TryEnsureServerRunningAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (ProbeServerRunning())
            return;

        var cli = ResolveOllamaCli();
        if (cli is null)
            return;

        progress?.Report("Starting Ollama serve…");
        try
        {
            // ollama serve is long-running; start detached.
            Process.Start(new ProcessStartInfo
            {
                FileName = cli,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            for (var i = 0; i < 30 && !ProbeServerRunning(); i++)
                await Task.Delay(500, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ollama] Could not start serve");
        }
    }

    private static bool ProbeServerRunning()
    {
        foreach (var url in new[]
                 {
                     "http://127.0.0.1:11434/api/tags",
                     "http://localhost:11434/api/tags",
                     "http://127.0.0.1:11434/v1/models"
                 })
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = http.GetAsync(url).GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch { /* next */ }
        }

        return false;
    }

    private static string? ResolveOllamaCli()
    {
        foreach (var c in EnumerateCliCandidates())
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
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(local, "Programs", "Ollama");
        yield return Path.Combine(pf, "Ollama");
    }

    private static IEnumerable<string> EnumerateCliCandidates()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "ollama.exe");

        foreach (var dir in EnumerateInstallDirs())
            yield return Path.Combine(dir, "ollama.exe");
    }

    private static string QuoteArg(string arg) =>
        arg.Contains(' ') || arg.Contains('"')
            ? "\"" + arg.Replace("\"", "\\\"") + "\""
            : arg;

    private async Task<(bool Ok, string Detail)> RunProcessWithOutputAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        int timeoutMs,
        bool elevate = false)
    {
        try
        {
            if (elevate)
            {
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
                : await WaitExit(proc, ct);

            if (!finished)
            {
                try { proc.Kill(true); } catch { }
                return (false, $"Timed out after {timeoutMs / 1000}s.");
            }

            try { proc.WaitForExit(2000); } catch { }
            var code = proc.ExitCode;
            var tail = Truncate((stdout + "\n" + stderr).Trim(), 400);
            return (code == 0, code == 0 ? "ok" : $"exit {code}: {tail}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ollama] Process failed: {File} {Args}", fileName, arguments);
            return (false, ex.Message);
        }
    }

    private static async Task<bool> WaitExit(Process proc, CancellationToken ct)
    {
        await proc.WaitForExitAsync(ct);
        return true;
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
