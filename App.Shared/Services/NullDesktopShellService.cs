using App.Core.UI;

namespace App.Shared.Services;

public sealed class NullDesktopShellService : IDesktopShellService
{
    public static readonly NullDesktopShellService Instance = new();

    private NullDesktopShellService() { }

    public bool IsSupported => false;
    public bool IsHidden => false;
    public bool CloseToTray => false;
    public bool StartWithWindows => false;
    public bool StartMinimized => false;
    public bool CanHideToTray => false;

    public event Action? OnChanged
    {
        add { }
        remove { }
    }

    public event Action? OnForegrounded
    {
        add { }
        remove { }
    }

    public Task SetCloseToTrayAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetStartWithWindowsAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task AcknowledgeTrayHintAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void Show() { }
    public void HideToTray() { }
    public void OpenNewWindow() { }
    public void RequestQuit() { }
    public void PrepareForProcessExit() { }
}
