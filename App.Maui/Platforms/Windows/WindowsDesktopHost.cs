using App.Core.Sync;
using App.Core.UI;
using App.Maui.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace App.Maui;

/// <summary>
/// Windows close-to-tray host. Attach is internal (not on <see cref="IDesktopShellService"/>).
/// Close-to-tray is hard-coded ON until Settings persistence (PR 4).
/// </summary>
public sealed class WindowsDesktopHost : IDesktopShellService, IDisposable
{
    private const string BalloonText = "Wizionic is still running. Right-click the tray icon to Quit.";

    private readonly WorkflowDueHost _due;
    private readonly ISyncService _sync;
    private readonly object _gate = new();

    private Window? _mauiWindow;
    private AppWindow? _appWindow;
    private Microsoft.UI.Xaml.Window? _nativeWindow;
    private DispatcherQueue? _dispatcher;
    private WindowsTrayIcon? _tray;
    private bool _quitRequested;
    private bool _prepared;
    private bool _attached;
    private bool _balloonShown;
    private bool _disposed;

    public WindowsDesktopHost(WorkflowDueHost due, ISyncService sync)
    {
        _due = due;
        _sync = sync;
        _sync.OnChanged += OnSyncChanged;
    }

    public bool IsSupported => true;
    public bool IsHidden { get; private set; }
    public bool CloseToTray { get; private set; } = true;
    public bool StartWithWindows => false;
    public bool StartMinimized => false;
    public bool IsQuitRequested => _quitRequested;

    public event Action? OnChanged;

    internal void Attach(Window window, AppWindow appWindow)
    {
        lock (_gate)
        {
            if (_attached || _disposed)
                return;
            _attached = true;
        }

        _mauiWindow = window;
        _appWindow = appWindow;
        _nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appWindow.Closing += OnClosing;

        var hwnd = _nativeWindow is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(_nativeWindow);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("[Desktop] Attach: no HWND yet");
            return;
        }

        _tray = new WindowsTrayIcon();
        _tray.Attach(hwnd, Show, RequestQuit);
        _tray.SetTooltip(TooltipText());
        Console.WriteLine("[Desktop] tray attached");
    }

    public void Show()
    {
        InvokeOnUi(ShowCore);
    }

    public void HideToTray()
    {
        InvokeOnUi(HideToTrayCore);
    }

    public void RequestQuit()
    {
        if (_quitRequested)
            return;
        _quitRequested = true;

        // Never NIM_DELETE / RemoveWindowSubclass from inside the tray WndProc.
        void go()
        {
            PrepareForProcessExitCore();
            _ = FinishQuitAsync();
        }

        if (_dispatcher is not null && _dispatcher.TryEnqueue(go))
            return;
        go();
    }

    public void PrepareForProcessExit()
    {
        InvokeOnUi(PrepareForProcessExitCore);
    }

    public Task SetCloseToTrayAsync(bool enabled, CancellationToken ct = default)
    {
        CloseToTray = enabled;
        OnChanged?.Invoke();
        return Task.CompletedTask;
    }

    public Task SetStartWithWindowsAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    public Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _sync.OnChanged -= OnSyncChanged; }
        catch { /* ignore */ }
        PrepareForProcessExitCore();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitRequested || !CloseToTray)
            return;

        args.Cancel = true;
        HideToTrayCore();
    }

    private void HideToTrayCore()
    {
        if (_quitRequested)
            return;

        try
        {
            _appWindow?.Hide();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] Hide failed: {ex.Message}");
            return;
        }

        IsHidden = true;
        OnChanged?.Invoke();
        _tray?.SetTooltip(TooltipText());

        if (!_balloonShown && _tray is not null)
        {
            if (_tray.ShowBalloon("Wizionic", BalloonText))
                _balloonShown = true;
        }

        Console.WriteLine("[Desktop] hidden to tray");
    }

    private void ShowCore()
    {
        if (_quitRequested)
            return;

        try
        {
            _appWindow?.Show();
            _nativeWindow?.Activate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] Show failed: {ex.Message}");
        }

        IsHidden = false;
        OnChanged?.Invoke();
        _tray?.SetTooltip(TooltipText());
        _ = TickAfterShowAsync();
        Console.WriteLine("[Desktop] shown");
    }

    private async Task TickAfterShowAsync()
    {
        try { await _due.TickNowAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Desktop] TickNow failed: {ex.Message}"); }

        try { await _sync.RefreshAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Desktop] RefreshAsync failed: {ex.Message}"); }
    }

    private void PrepareForProcessExitCore()
    {
        if (_prepared)
            return;
        _prepared = true;

        if (_appWindow is not null)
        {
            try { _appWindow.Closing -= OnClosing; }
            catch { /* ignore */ }
        }

        try { _sync.OnChanged -= OnSyncChanged; }
        catch { /* ignore */ }

        try { _tray?.Dispose(); }
        catch (Exception ex) { Console.WriteLine($"[Tray] dispose: {ex.Message}"); }
        _tray = null;

        try { _due.Stop(); }
        catch (Exception ex) { Console.WriteLine($"[WorkflowDue] Stop: {ex.Message}"); }

        Console.WriteLine("[Desktop] prepared for process exit");
    }

    private async Task FinishQuitAsync()
    {
        try
        {
            await _sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MauiSync] dispose on quit: {ex.Message}");
        }

        InvokeOnUi(() =>
        {
            try { Microsoft.Maui.Controls.Application.Current?.Quit(); }
            catch (Exception ex) { Console.WriteLine($"[Desktop] Quit: {ex.Message}"); }

            try { _appWindow?.Destroy(); }
            catch { /* already closing */ }

            Environment.Exit(0);
        });
    }

    private void OnSyncChanged()
    {
        InvokeOnUi(() => _tray?.SetTooltip(TooltipText()));
    }

    private string TooltipText()
        => _sync.IsConnected ? "Wizionic — Connected" : "Wizionic — Offline";

    private void InvokeOnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? error = null;
        if (!dispatcher.TryEnqueue(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            }))
        {
            action();
            return;
        }

        if (!done.Wait(TimeSpan.FromSeconds(2)))
            Console.WriteLine("[Desktop] UI marshal timed out");
        if (error is not null)
            Console.WriteLine($"[Desktop] UI action: {error.Message}");
    }
}
