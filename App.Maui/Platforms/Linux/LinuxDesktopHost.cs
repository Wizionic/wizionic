using App.Core.Setup;
using App.Core.Sync;
using App.Core.UI;
using App.Maui.Services;

namespace App.Maui;

/// <summary>
/// Linux close-to-tray host. Attach is internal (not on <see cref="IDesktopShellService"/>).
/// Close-to-tray defaults ON until SQLite prefs load. Hide requires a StatusNotifier watcher.
/// </summary>
public sealed class LinuxDesktopHost : IDesktopShellService, IDisposable
{
	public const string CloseToTrayKey = "app-close-to-tray";
	public const string StartWithWindowsKey = "app-start-with-windows";
	public const string StartMinimizedKey = "app-start-minimized";
	public const string TrayHintShownKey = "app-tray-hint-shown";

	private const string HintBody = "Wizionic is still running. Right-click the tray icon to Quit.";

	private readonly WorkflowDueHost _due;
	private readonly ISyncService _sync;
	private readonly SqliteSettingsDatabase _db;
	private readonly ISetupWizardHost _setup;
	private readonly object _gate = new();

	private Adw.Application? _application;
	private Adw.ApplicationWindow? _window;
	private SynchronizationContext? _glibContext;
	private LinuxTrayIcon? _tray;
	private bool _closeHooked;
	private bool _quitRequested;
	private bool _prepared;
	private bool _attached;
	private bool _held;
	private bool _hintPersisted;
	private bool _hintShown;
	private bool _disposed;
	private bool _canHideToTray;
	private bool _restoreHidden;

	public LinuxDesktopHost(
		WorkflowDueHost due,
		ISyncService sync,
		SqliteSettingsDatabase db,
		ISetupWizardHost setup)
	{
		_due = due;
		_sync = sync;
		_db = db;
		_setup = setup;
		_sync.OnChanged += OnSyncChanged;
	}

	public bool IsSupported => true;
	public bool IsHidden { get; private set; }
	public bool CloseToTray { get; private set; } = true;
	public bool StartWithWindows { get; private set; }
	public bool StartMinimized { get; private set; } = true;
	public bool CanHideToTray
	{
		get { lock (_gate) return _canHideToTray; }
		private set
		{
			lock (_gate)
				_canHideToTray = value;
			OnChanged?.Invoke();
		}
	}

	public bool IsQuitRequested => _quitRequested;

	public event Action? OnChanged;

	internal void Attach(Adw.Application application, Adw.ApplicationWindow window)
	{
		lock (_gate)
		{
			if (_attached || _disposed)
				return;
			_attached = true;
		}

		_application = application;
		_window = window;
		_glibContext = SynchronizationContext.Current;

		_window.OnCloseRequest += OnCloseRequest;
		_closeHooked = true;

		_restoreHidden = TrayRestoreFlag.ConsumeHidden();

		_tray = new LinuxTrayIcon();
		_tray.Start(Show, RequestQuit, OnTrayRegistered);
		_tray.SetTooltip(TooltipText());
		Console.WriteLine("[Desktop] tray starting");
		_ = LoadPrefsAsync();
	}

	public void Show() => InvokeOnUi(ShowCore);

	public void HideToTray() => InvokeOnUi(HideToTrayCore);

	public void RequestQuit()
	{
		if (_quitRequested)
			return;
		_quitRequested = true;

		void go()
		{
			PrepareForProcessExitCore();
			_ = FinishQuitAsync();
		}

		InvokeOnUi(go);
	}

	public void PrepareForProcessExit() => InvokeOnUi(PrepareForProcessExitCore);

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
		LinuxAutostartRegistration.Apply(StartWithWindows, StartMinimized);
		OnChanged?.Invoke();
	}

	public async Task SetStartMinimizedAsync(bool enabled, CancellationToken ct = default)
	{
		StartMinimized = enabled;
		await _db.SetStringAsync(StartMinimizedKey, enabled ? "1" : "0", ct);
		if (StartWithWindows)
			LinuxAutostartRegistration.Apply(StartWithWindows, StartMinimized);
		OnChanged?.Invoke();
	}

	public Task AcknowledgeTrayHintAsync(CancellationToken ct = default)
		=> PersistHintAsync(ct);

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		try { _sync.OnChanged -= OnSyncChanged; }
		catch { /* ignore */ }
		PrepareForProcessExitCore();
	}

	internal void ReleaseHoldIfNeeded()
	{
		if (!_held || _application is null)
			return;
		try { _application.Release(); }
		catch (Exception ex) { Console.WriteLine($"[Desktop] Release: {ex.Message}"); }
		_held = false;
	}

	private bool OnCloseRequest(Gtk.Window sender, EventArgs args)
	{
		if (_quitRequested || !CloseToTray || !CanHideToTray)
		{
			ReleaseHoldIfNeeded();
			return false;
		}

		HideToTrayCore();
		return true;
	}

	private void OnTrayRegistered(bool registered)
	{
		InvokeOnUi(() =>
		{
			CanHideToTray = registered;
			if (!registered)
				Console.WriteLine("[Tray] no StatusNotifier watcher");

			var hide = (HasStartMinimizedArg() || _restoreHidden) && !_setup.ShouldAutoShow;
			if (hide && registered)
			{
				HideToTrayCore();
				Console.WriteLine(_restoreHidden
					? "[Desktop] tray-restore: hidden"
					: "[Desktop] start-minimized: hidden");
			}
		});
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
				_hintShown = true;

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
		_hintShown = true;
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

	private void HideToTrayCore()
	{
		if (_quitRequested || _window is null)
			return;

		if (!CanHideToTray)
		{
			Console.WriteLine("[Desktop] HideToTray skipped — no tray watcher");
			return;
		}

		try
		{
			_window.SetVisible(false);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] Hide failed: {ex.Message}");
			return;
		}

		HoldOnce();
		IsHidden = true;
		OnChanged?.Invoke();
		_tray?.SetTooltip(TooltipText());
		MaybeSendHint();
		Console.WriteLine("[Desktop] hidden to tray");
	}

	private void ShowCore()
	{
		if (_quitRequested || _window is null)
			return;

		try
		{
			_window.SetVisible(true);
			_window.Present();
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

	private void HoldOnce()
	{
		if (_held || _application is null)
			return;
		try
		{
			_application.Hold();
			_held = true;
			Console.WriteLine("[Desktop] Gio.Application.Hold");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] Hold failed: {ex.Message}");
		}
	}

	private void MaybeSendHint()
	{
		if (_hintShown || _application is null)
			return;

		try
		{
			var n = Gio.Notification.New("Wizionic");
			n.SetBody(HintBody);
			_application.SendNotification("wizionic-tray-hint", n);
			_hintShown = true;
			_ = PersistHintAsync();
			Console.WriteLine("[Desktop] tray hint notification sent");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] tray hint failed: {ex.Message}");
		}
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

		if (_window is not null && _closeHooked)
		{
			try { _window.OnCloseRequest -= OnCloseRequest; }
			catch { /* ignore */ }
			_closeHooked = false;
		}

		try { _sync.OnChanged -= OnSyncChanged; }
		catch { /* ignore */ }

		try { _tray?.Dispose(); }
		catch (Exception ex) { Console.WriteLine($"[Tray] dispose: {ex.Message}"); }
		_tray = null;

		try { _due.Stop(); }
		catch (Exception ex) { Console.WriteLine($"[WorkflowDue] Stop: {ex.Message}"); }

		ReleaseHoldIfNeeded();
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
			try { _window?.Destroy(); }
			catch { /* already closing */ }

			try { _application?.Quit(); }
			catch (Exception ex) { Console.WriteLine($"[Desktop] Quit: {ex.Message}"); }
		});
	}

	private void OnSyncChanged() => InvokeOnUi(() => _tray?.SetTooltip(TooltipText()));

	private string TooltipText()
		=> _sync.IsConnected ? "Wizionic — Connected" : "Wizionic — Offline";

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

	private void InvokeOnUi(Action action)
	{
		if (_glibContext is not null && SynchronizationContext.Current == _glibContext)
		{
			action();
			return;
		}

		// Never block the D-Bus thread waiting for GLib (tray Quit/Show).
		GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
		{
			try { action(); }
			catch (Exception ex) { Console.WriteLine($"[Desktop] UI action: {ex.Message}"); }
			return false;
		});
	}
}
