using App.Core.Setup;
using App.Core.Sync;
using App.Core.UI;
using App.Core.Update;
using App.Maui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

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
    public const string WindowBoundsKey = DesktopWindowBounds.SettingsKey;

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
    private bool _boundsReady;
    private bool _applyingBounds;
    private bool _boundsHooked;
    private int _restoredX;
    private int _restoredY;
    private int _restoredW;
    private int _restoredH;
    private CancellationTokenSource? _saveBoundsCts;

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
    public event Action? OnForegrounded;

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

            var savedBounds = DesktopWindowBounds.Parse(await _db.GetStringAsync(WindowBoundsKey));
            InvokeOnUi(() => ApplyAndHookWindowBounds(savedBounds));

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

        RaiseForegrounded();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (ReferenceEquals(sender, _appWindow))
            PersistWindowBoundsNow();

        if (_quitRequested)
            return;

        // Use our tracked list, not Application.Windows: during Closing the MAUI
        // collection may already have dropped this window, which would look like
        // "last window" and hide-to-tray instead of closing an extra view.
        if (_windows.Count > 1)
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
        RaiseForegrounded();
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

        PersistWindowBoundsNow();

        if (_appWindow is not null && _boundsHooked)
        {
            try { _appWindow.Changed -= OnAppWindowChanged; }
            catch { /* ignore */ }
            _boundsHooked = false;
        }

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

    private void RaiseForegrounded()
    {
        try { OnForegrounded?.Invoke(); }
        catch (Exception ex) { Console.WriteLine($"[Desktop] OnForegrounded: {ex.Message}"); }
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

    private void ApplyAndHookWindowBounds(DesktopWindowBounds? saved)
    {
        var appWindow = _appWindow;
        if (appWindow is null)
            return;

        _applyingBounds = true;
        try
        {
            if (saved is null)
            {
                MaximizePrimaryWindow(appWindow);
                Console.WriteLine("[Desktop] first launch: maximized");
            }
            else if (saved.Maximized)
            {
                if (saved.HasRestoredSize)
                    MoveResizeClamped(appWindow, saved);
                MaximizePrimaryWindow(appWindow);
                RememberRestored(saved);
                Console.WriteLine("[Desktop] restored maximized");
            }
            else if (saved.HasRestoredSize)
            {
                MoveResizeClamped(appWindow, saved);
                RememberRestored(saved);
                Console.WriteLine($"[Desktop] restored {saved.W}x{saved.H} at {saved.X},{saved.Y}");
            }
            else
            {
                MaximizePrimaryWindow(appWindow);
                Console.WriteLine("[Desktop] no usable saved size: maximized");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] apply window bounds failed: {ex.Message}");
        }
        finally
        {
            _applyingBounds = false;
            _boundsReady = true;
        }

        if (_boundsHooked)
            return;
        try
        {
            appWindow.Changed += OnAppWindowChanged;
            _boundsHooked = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] window Changed hook failed: {ex.Message}");
        }
    }

    private static void MaximizePrimaryWindow(AppWindow appWindow)
    {
        if (appWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        else
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
    }

    private void RememberRestored(DesktopWindowBounds saved)
    {
        _restoredX = saved.X;
        _restoredY = saved.Y;
        _restoredW = saved.W;
        _restoredH = saved.H;
    }

    private static void MoveResizeClamped(AppWindow appWindow, DesktopWindowBounds saved)
    {
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest)
                      ?? DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display?.WorkArea ?? new RectInt32(0, 0, saved.W, saved.H);

        var w = Math.Clamp(saved.W, DesktopWindowBounds.MinWidth, Math.Max(DesktopWindowBounds.MinWidth, work.Width));
        var h = Math.Clamp(saved.H, DesktopWindowBounds.MinHeight, Math.Max(DesktopWindowBounds.MinHeight, work.Height));
        var x = saved.X;
        var y = saved.Y;
        if (x + 80 < work.X || x >= work.X + work.Width - 80)
            x = work.X;
        if (y + 80 < work.Y || y >= work.Y + work.Height - 80)
            y = work.Y;
        x = Math.Clamp(x, work.X, work.X + Math.Max(0, work.Width - w));
        y = Math.Clamp(y, work.Y, work.Y + Math.Max(0, work.Height - h));
        appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange)
            return;
        if (!_boundsReady || _applyingBounds)
            return;
        if (!ReferenceEquals(sender, _appWindow))
            return;
        SchedulePersistWindowBounds();
    }

    private void SchedulePersistWindowBounds()
    {
        _saveBoundsCts?.Cancel();
        _saveBoundsCts = new CancellationTokenSource();
        var token = _saveBoundsCts.Token;
        _ = PersistWindowBoundsSoonAsync(token);
    }

    private async Task PersistWindowBoundsSoonAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PersistWindowBoundsNow();
    }

    private void PersistWindowBoundsNow()
    {
        if (!_boundsReady || _applyingBounds)
            return;
        var appWindow = _appWindow;
        if (appWindow is null)
            return;

        try
        {
            var maximized = false;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                if (presenter.State == OverlappedPresenterState.Minimized)
                    return;
                maximized = presenter.State == OverlappedPresenterState.Maximized;
            }

            if (!maximized)
            {
                var pos = appWindow.Position;
                var size = appWindow.Size;
                if (size.Width >= DesktopWindowBounds.MinWidth && size.Height >= DesktopWindowBounds.MinHeight)
                {
                    _restoredX = pos.X;
                    _restoredY = pos.Y;
                    _restoredW = size.Width;
                    _restoredH = size.Height;
                }
            }

            var bounds = new DesktopWindowBounds
            {
                X = _restoredW > 0 ? _restoredX : appWindow.Position.X,
                Y = _restoredH > 0 ? _restoredY : appWindow.Position.Y,
                W = _restoredW > 0 ? _restoredW : appWindow.Size.Width,
                H = _restoredH > 0 ? _restoredH : appWindow.Size.Height,
                Maximized = maximized
            };
            _ = _db.SetStringAsync(WindowBoundsKey, bounds.ToJson());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] persist window bounds failed: {ex.Message}");
        }
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
