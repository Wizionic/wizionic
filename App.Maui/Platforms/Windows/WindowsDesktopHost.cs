using App.Core.Setup;
using App.Core.Sync;
using App.Core.UI;
using App.Core.Update;
using App.Maui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace App.Maui;

/// <summary>
/// Windows close-to-tray host. Attach is internal (not on <see cref="IDesktopShellService"/>).
/// Close-to-tray defaults ON until SQLite prefs load.
/// </summary>
public sealed class WindowsDesktopHost : IDesktopShellService, IDisposable
{
    public const string CloseToTrayKey = "app-close-to-tray";
    public const string StartWithWindowsKey = "app-start-with-windows";
    public const string StartMinimizedKey = "app-start-minimized";
    public const string TrayHintShownKey = "app-tray-hint-shown";

    private const string BalloonText = "Wizionic is still running. Right-click the tray icon to Quit.";

    private readonly WorkflowDueHost _due;
    private readonly ISyncService _sync;
    private readonly SqliteSettingsDatabase _db;
    private readonly ISetupWizardHost _setup;
    private readonly IServiceProvider _services;
    private readonly object _gate = new();

    private Window? _mauiWindow;
    private AppWindow? _appWindow;
    private Microsoft.UI.Xaml.Window? _nativeWindow;
    private readonly List<TrackedWindow> _windows = new();
    private DispatcherQueue? _dispatcher;
    private WindowsTrayIcon? _tray;
    private bool _quitRequested;
    private bool _prepared;
    private bool _attached;
    private bool _balloonShown;
    private bool _hintPersisted;
    private bool _disposed;

    public WindowsDesktopHost(
        WorkflowDueHost due,
        ISyncService sync,
        SqliteSettingsDatabase db,
        ISetupWizardHost setup,
        IServiceProvider services)
    {
        _due = due;
        _sync = sync;
        _db = db;
        _setup = setup;
        _services = services;
        _sync.OnChanged += OnSyncChanged;
    }

    public bool IsSupported => true;
    public bool IsHidden { get; private set; }
    public bool CloseToTray { get; private set; } = true;
    public bool StartWithWindows { get; private set; }
    public bool StartMinimized { get; private set; } = true;
    public bool CanHideToTray => true;
    public bool IsQuitRequested => _quitRequested;

    public event Action? OnChanged;

    internal void Attach(Window window, AppWindow appWindow)
    {
        bool first;
        lock (_gate)
        {
            if (_disposed)
                return;
            first = !_attached;
            _attached = true;
        }

        var native = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        Track(window, appWindow, native);
        appWindow.Closing += OnClosing;
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();

        if (!first)
        {
            Console.WriteLine("[Desktop] additional window attached");
            return;
        }

        _mauiWindow = window;
        _appWindow = appWindow;
        _nativeWindow = native;
        SubscribePowerResume();

        var restoreHidden = TrayRestoreFlag.ConsumeHidden();
        if ((HasStartMinimizedArg() || restoreHidden) && !_setup.ShouldAutoShow)
        {
            try
            {
                _appWindow.Hide();
                IsHidden = true;
                Console.WriteLine(restoreHidden
                    ? "[Desktop] tray-restore: hidden before activate"
                    : "[Desktop] start-minimized: hidden before activate");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Desktop] start-minimized hide failed: {ex.Message}");
            }
        }

        var hwnd = _nativeWindow is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(_nativeWindow);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("[Desktop] Attach: no HWND yet");
            return;
        }

        BindTray(hwnd);
        WindowsSingleInstance.StartWaitLoop(OnSecondLaunch, RequestQuit);
        Console.WriteLine("[Desktop] tray attached");
        _ = LoadPrefsAsync();
    }

    public void Show()
    {
        InvokeOnUi(ShowCore);
    }

    public void HideToTray()
    {
        InvokeOnUi(HideToTrayCore);
    }

    public void OpenNewWindow()
    {
        InvokeOnUi(OpenNewWindowCore);
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

    public async Task SetCloseToTrayAsync(bool enabled, CancellationToken ct = default)
    {
        CloseToTray = enabled;
        await _db.SetStringAsync(CloseToTrayKey, enabled ? "1" : "0", ct);
        OnChanged?.Invoke();
    }

    public async Task SetStartWithWindowsAsync(bool enabled, CancellationToken ct = default)
    {
        StartWithWindows = enabled;
        await _db.SetStringAsync(StartWithWindowsKey, enabled ? "1" : "0", ct);
        ApplyRunKey();
        OnChanged?.Invoke();
    }

    public async Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default)
    {
        StartMinimized = enabled;
        await _db.SetStringAsync(StartMinimizedKey, enabled ? "1" : "0", ct);
        if (StartWithWindows)
            ApplyRunKey();
        OnChanged?.Invoke();
    }

    public Task AcknowledgeTrayHintAsync(CancellationToken ct = default)
        => PersistHintAsync(ct);

    /// <summary>Sleep/lock resume: due tick + hub refresh. Does not unhide the window.</summary>
    public void OnPowerResume()
    {
        Console.WriteLine("[Desktop] power resume");
        _ = TickAfterShowAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _sync.OnChanged -= OnSyncChanged; }
        catch { /* ignore */ }
        PrepareForProcessExitCore();
    }

    private async Task LoadPrefsAsync()
    {
        try
        {
            var close = await _db.GetStringAsync(CloseToTrayKey);
            if (close == "0")
                CloseToTray = false;
            else if (close == "1")
                CloseToTray = true;

            StartWithWindows = await _db.GetStringAsync(StartWithWindowsKey) == "1";

            var minimized = await _db.GetStringAsync(StartMinimizedKey);
            if (minimized == "0")
                StartMinimized = false;
            else if (minimized == "1" || minimized is null)
                StartMinimized = true;

            _hintPersisted = await _db.GetStringAsync(TrayHintShownKey) == "1";
            if (_hintPersisted)
                _balloonShown = true;

            OnChanged?.Invoke();
            Console.WriteLine(
                $"[Desktop] prefs closeToTray={CloseToTray} startWithWindows={StartWithWindows} startMinimized={StartMinimized}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] prefs load failed: {ex.Message}");
        }
    }

    private async Task PersistHintAsync(CancellationToken ct = default)
    {
        if (_hintPersisted)
            return;
        _hintPersisted = true;
        _balloonShown = true;
        try
        {
            await _db.SetStringAsync(TrayHintShownKey, "1", ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] hint persist failed: {ex.Message}");
            _hintPersisted = false;
        }
    }

    private void ApplyRunKey()
    {
        var installed = _services.GetService<IUpdateService>()?.IsVelopackInstalled ?? false;
        WindowsStartupRegistration.Apply(StartWithWindows, StartMinimized, installed);
    }

    private void SubscribePowerResume()
    {
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] PowerModeChanged subscribe failed: {ex.Message}");
        }
    }

    private void UnsubscribePowerResume()
    {
        try
        {
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch { /* ignore */ }
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume)
            return;
        OnPowerResume();
    }

    private static bool HasStartMinimizedArg()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.Equals("--start-minimized", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void OnSecondLaunch()
    {
        if (IsHidden)
            Show();
        else
            OpenNewWindow();
    }

    private void OpenNewWindowCore()
    {
        if (_quitRequested)
            return;

        if (IsHidden)
        {
            ShowCore();
            return;
        }

        if (Application.Current is MauiShell shell)
        {
            shell.OpenAdditionalWindow();
            Console.WriteLine("[Desktop] opened additional window");
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitRequested)
            return;

        if (MauiWindowCount() > 1)
        {
            if (ReferenceEquals(sender, _appWindow))
                RebindTrayAwayFrom(sender);
            Untrack(sender);
            try { sender.Closing -= OnClosing; }
            catch { /* ignore */ }
            return;
        }

        if (!CloseToTray)
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
                _ = PersistHintAsync();
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

        try { WindowsSingleInstance.StopWaitLoop(); }
        catch (Exception ex) { Console.WriteLine($"[Desktop] stop wait loop: {ex.Message}"); }

        UnsubscribePowerResume();

        foreach (var tracked in _windows.ToArray())
        {
            try { tracked.App.Closing -= OnClosing; }
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

    private sealed class TrackedWindow
    {
        public required Window Maui { get; init; }
        public required AppWindow App { get; init; }
        public Microsoft.UI.Xaml.Window? Native { get; init; }
    }

    private void Track(Window window, AppWindow appWindow, Microsoft.UI.Xaml.Window? native)
    {
        if (_windows.Any(w => ReferenceEquals(w.App, appWindow)))
            return;
        _windows.Add(new TrackedWindow { Maui = window, App = appWindow, Native = native });
    }

    private void Untrack(AppWindow appWindow)
        => _windows.RemoveAll(w => ReferenceEquals(w.App, appWindow));

    private static int MauiWindowCount()
        => Application.Current?.Windows.Count ?? 1;

    private void BindTray(IntPtr hwnd)
    {
        try { _tray?.Dispose(); }
        catch { /* ignore */ }
        _tray = new WindowsTrayIcon();
        _tray.Attach(hwnd, Show, RequestQuit, OpenNewWindow);
        _tray.SetTooltip(TooltipText());
    }

    private void RebindTrayAwayFrom(AppWindow closing)
    {
        var next = _windows.FirstOrDefault(w => !ReferenceEquals(w.App, closing));
        if (next is null)
            return;

        _mauiWindow = next.Maui;
        _appWindow = next.App;
        _nativeWindow = next.Native;
        var hwnd = next.Native is null
            ? IntPtr.Zero
            : WinRT.Interop.WindowNative.GetWindowHandle(next.Native);
        if (hwnd == IntPtr.Zero)
            return;

        BindTray(hwnd);
        Console.WriteLine("[Desktop] tray rebound to remaining window");
    }

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
