namespace App.Core.UI;

/// <summary>
/// Desktop window/tray chrome. Windows MAUI and Linux GirCore implement this;
/// WASM, host SSR, and mobile use a no-op (<see cref="IsSupported"/> is false).
/// </summary>
public interface IDesktopShellService
{
    bool IsSupported { get; }
    bool IsHidden { get; }
    bool CloseToTray { get; }
    bool StartWithWindows { get; }
    bool StartMinimized { get; }

    /// <summary>
    /// True when a tray icon can actually be shown. Linux is false until a
    /// StatusNotifier watcher is present; close must not hide in that case.
    /// </summary>
    bool CanHideToTray { get; }

    event Action? OnChanged;

    Task SetCloseToTrayAsync(bool enabled, CancellationToken ct = default);
    Task SetStartWithWindowsAsync(bool enabled, CancellationToken ct = default);
    Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default);
    Task AcknowledgeTrayHintAsync(CancellationToken ct = default);

    void Show();
    void HideToTray();
    void RequestQuit();
    void PrepareForProcessExit();
}
