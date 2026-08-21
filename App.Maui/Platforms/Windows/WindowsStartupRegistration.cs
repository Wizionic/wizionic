using Microsoft.Win32;

namespace App.Maui;

/// <summary>
/// HKCU Run key for Start with Windows. Command is the Velopack 1.2 root stub, not Update.exe.
/// </summary>
internal static class WindowsStartupRegistration
{
    public const string RunValueName = "Wizionic";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool startWithWindows, bool startMinimized, bool isVelopackInstalled)
    {
        if (!startWithWindows)
        {
            Delete();
            return;
        }

        var path = ResolveExecutable(isVelopackInstalled);
        if (path is null)
        {
            Console.WriteLine("[Desktop] stub not found — not writing Run key");
            return;
        }

        var command = startMinimized
            ? $"\"{path}\" --start-minimized"
            : $"\"{path}\"";

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(RunValueName, command);
        Console.WriteLine($"[Desktop] Run key = {command}");
    }

    public static void Delete()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            Console.WriteLine("[Desktop] Run key deleted");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] Run key delete failed: {ex.Message}");
        }
    }

    public static string? ResolveExecutable(bool isVelopackInstalled)
    {
        if (isVelopackInstalled)
        {
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetFileName(baseDir), "current", StringComparison.OrdinalIgnoreCase))
            {
                var stub = Path.GetFullPath(Path.Combine(baseDir, "..", "Wizionic.exe"));
                if (File.Exists(stub))
                    return stub;
            }

            Console.WriteLine("[Desktop] stub not found");
            return null;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            return processPath;

        return null;
    }
}
