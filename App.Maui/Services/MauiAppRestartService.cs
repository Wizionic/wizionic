using System.Diagnostics;
using App.Core.UI;

namespace App.Maui.Services;

/// <summary>Relaunch this desktop process (Windows exe or Linux AppImage) and exit.</summary>
public sealed class MauiAppRestartService : IAppRestartService
{
    public bool CanRestart => true;

    public void Restart()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("APPIMAGE");
            if (string.IsNullOrWhiteSpace(path))
                path = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
                });
            }
            else
            {
                Console.WriteLine("[Restart] Could not resolve executable path.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Restart] Launch failed: {ex.Message}");
        }

        Environment.Exit(0);
    }
}
