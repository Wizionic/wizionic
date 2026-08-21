using System.Diagnostics;
using App.Core.UI;
using Microsoft.Extensions.DependencyInjection;

namespace App.Maui.Services;

/// <summary>Relaunch this desktop process (Windows exe or Linux AppImage) and exit.</summary>
public sealed class MauiAppRestartService : IAppRestartService
{
    private readonly IServiceProvider _services;

    public MauiAppRestartService(IServiceProvider services)
    {
        _services = services;
    }

    public bool CanRestart => true;

    public void Restart()
    {
        try
        {
            var shell = _services.GetService<IDesktopShellService>();
            if (shell is { IsHidden: true })
                TrayRestoreFlag.WriteHidden();
            shell?.PrepareForProcessExit();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Restart] PrepareForProcessExit failed: {ex.Message}");
        }

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
