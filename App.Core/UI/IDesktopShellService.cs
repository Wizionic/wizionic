namespace App.Core.UI;

/// <summary>
/// Desktop window/tray chrome. Windows MAUI is the only real implementation;
/// WASM, host SSR, Linux, and mobile use a no-op (<see cref="IsSupported"/> is false).
/// </summary>
public interface IDesktopShellService
{
    bool IsSupported { get; }
    bool IsHidden { get; }
    bool CloseToTray { get; }
    bool StartWithWindows { get; }
    bool StartMinimized { get; }

    event Action? OnChanged;

    Task SetCloseToTrayAsync(bool enabled, CancellationToken ct = default);
    Task SetStartWithWindowsAsync(bool enabled, CancellationToken ct = default);
    Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default);

    void Show();
    void HideToTray();
    void RequestQuit();
    void PrepareForProcessExit();
}
