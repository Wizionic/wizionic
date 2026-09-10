using System.Diagnostics;
using App.Core.Homeserver;

namespace App.Maui.Services;

/// <summary>
/// Best-effort wipe of Wizionic-owned machine and user data during desktop uninstall.
/// Lemonade and Ollama are separate products and are not removed.
/// FastCallbacks must not show UI (no UAC); leftover admin-owned files are retried
/// from a detached process after this process exits.
/// </summary>
internal static class WizionicUninstallCleanup
{
    private static string PendingFlagPath =>
        Path.Combine(Path.GetTempPath(), "wizionic-uninstall.pending");

    /// <summary>
    /// Call on normal app start so a delayed uninstall wipe does not delete a reinstall
    /// that began before the deferred rmdir ran.
    /// </summary>
    public static void CancelPending()
    {
        try
        {
            if (File.Exists(PendingFlagPath))
                File.Delete(PendingFlagPath);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Stop leftover Home Server and delete its ProgramData tree. Used on a Velopack
    /// first run so a fast reinstall is not sitting on the previous install's SQLite.
    /// Does not touch this user's app data.
    /// </summary>
    public static void WipeLeftoverHomeServer()
    {
        try { StopHomeServer(); }
        catch { /* best effort */ }

        try { KillHomeServerProcesses(); }
        catch { /* best effort */ }

        var paths = new List<string> { HomeserverPaths.RootDirectory };
        var hsParent = Path.GetDirectoryName(HomeserverPaths.RootDirectory);
        if (!string.IsNullOrWhiteSpace(hsParent) &&
            string.Equals(Path.GetFileName(hsParent), "Wizionic", StringComparison.OrdinalIgnoreCase))
            paths.Add(hsParent);
        TryDeleteNow(paths);
    }

    public static void Run()
    {
        try { StopHomeServer(); }
        catch { /* uninstall continues */ }

        try { RemoveHomeServerService(); }
        catch { /* best effort — may require admin */ }

        try { KillHomeServerProcesses(); }
        catch { /* uninstall continues */ }

        try { RemoveHomeServerShortcutsAndFirewall(); }
        catch { /* uninstall continues */ }

        var deletePaths = CollectDeletePaths();
        TryDeleteNow(deletePaths);
        ScheduleDelayedDelete(deletePaths);
    }

    private static void StopHomeServer()
    {
        if (OperatingSystem.IsWindows())
        {
            RunHidden("sc.exe", $"stop {HomeserverPaths.ServiceName}", 5_000);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            RunHidden("systemctl", $"stop {HomeserverPaths.SystemdUnitName}.service", 5_000);
        }
    }

    private static void RemoveHomeServerService()
    {
        if (OperatingSystem.IsWindows())
            RunHidden("sc.exe", $"delete {HomeserverPaths.ServiceName}", 5_000);
        else if (OperatingSystem.IsLinux())
        {
            RunHidden("systemctl", $"disable {HomeserverPaths.SystemdUnitName}.service", 5_000);
            RunHidden("rm", $"-f /etc/systemd/system/{HomeserverPaths.SystemdUnitFileName}", 3_000);
        }
    }

    private static void KillHomeServerProcesses()
    {
        var root = HomeserverPaths.AppDirectory;
        if (string.IsNullOrWhiteSpace(root))
            return;

        foreach (var name in new[] { "App", "WizionicHomeServer" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in processes)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is not null &&
                        path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill(entireProcessTree: true);
                        p.WaitForExit(3_000);
                    }
                }
                catch
                {
                    // access denied or already exited
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    private static void RemoveHomeServerShortcutsAndFirewall()
    {
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "Wizionic Home Server.cmd"));
        TryDeleteFile(HomeserverPaths.LinuxAutostartDesktopPath);

        if (OperatingSystem.IsWindows())
        {
            RunHidden(
                "netsh",
                $"advfirewall firewall delete rule name=\"{HomeserverPaths.FirewallRuleName}\"",
                5_000);
        }
    }

    private static List<string> CollectDeletePaths()
    {
        var paths = new List<string>();
        foreach (var dir in MauiAppData.DataDirectoriesForCleanup())
        {
            if (!string.IsNullOrWhiteSpace(dir))
                paths.Add(dir);
        }

        paths.Add(HomeserverPaths.RootDirectory);

        var hsParent = Path.GetDirectoryName(HomeserverPaths.RootDirectory);
        if (!string.IsNullOrWhiteSpace(hsParent) &&
            string.Equals(Path.GetFileName(hsParent), "Wizionic", StringComparison.OrdinalIgnoreCase))
            paths.Add(hsParent);

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryDeleteNow(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            for (var i = 0; i < 3; i++)
            {
                TryDeleteDirectory(path);
                TryDeleteFile(path);
                if (!PathExists(path))
                    break;
                Thread.Sleep(200);
            }
        }
    }

    private static void ScheduleDelayedDelete(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(PathExists).Select(TrimPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0)
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                ScheduleDelayedDeleteWindows(existing);
            else if (OperatingSystem.IsLinux())
                ScheduleDelayedDeleteLinux(existing);
        }
        catch
        {
            // best effort
        }
    }

    private static void ScheduleDelayedDeleteWindows(List<string> paths)
    {
        var script = Path.Combine(Path.GetTempPath(), $"wizionic-uninstall-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(PendingFlagPath, DateTimeOffset.UtcNow.ToString("O"));
        var lines = new List<string>
        {
            "@echo off",
            "timeout /t 8 /nobreak >nul",
            $"if not exist \"{PendingFlagPath}\" exit /b 0"
        };
        foreach (var path in paths)
            lines.Add($"if exist \"{path}\" rmdir /s /q \"{path}\" 2>nul");
        lines.Add($"del \"{PendingFlagPath}\" 2>nul");
        lines.Add($"del \"{script}\" 2>nul");
        File.WriteAllLines(script, lines);

        // `start` detaches so Velopack can kill this process without killing cleanup.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c start \"\" /min cmd.exe /c \"\"{script}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void ScheduleDelayedDeleteLinux(List<string> paths)
    {
        var script = Path.Combine(Path.GetTempPath(), $"wizionic-uninstall-{Guid.NewGuid():N}.sh");
        File.WriteAllText(PendingFlagPath, DateTimeOffset.UtcNow.ToString("O"));
        static string Q(string p) => "'" + p.Replace("'", "'\\''") + "'";
        var lines = new List<string>
        {
            "#!/bin/sh",
            "sleep 8",
            $"[ -f {Q(PendingFlagPath)} ] || exit 0"
        };
        foreach (var path in paths)
            lines.Add($"rm -rf {Q(path)}");
        lines.Add($"rm -f {Q(PendingFlagPath)}");
        lines.Add($"rm -f {Q(script)}");
        File.WriteAllLines(script, lines);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"setsid /bin/sh {Q(script)} </dev/null >/dev/null 2>&1 &\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void RunHidden(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            p?.WaitForExit(timeoutMs);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            ClearReadOnly(path);
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // locked or access denied — delayed delete may still succeed
        }
    }

    private static void TryDeleteFile(string path)
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

    private static void ClearReadOnly(string path)
    {
        try
        {
            foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                try { info.Attributes = FileAttributes.Normal; }
                catch { /* skip */ }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool PathExists(string path) =>
        Directory.Exists(path) || File.Exists(path);

    private static string TrimPath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
