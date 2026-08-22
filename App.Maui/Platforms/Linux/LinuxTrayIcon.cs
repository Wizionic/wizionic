using App.Maui.Services.Linux;
using Tmds.DBus;

namespace App.Maui;

/// <summary>
/// StatusNotifierItem + dbusmenu on the session bus. Probe the watcher before hide-to-tray.
/// </summary>
internal sealed class LinuxTrayIcon : IDisposable
{
	private const int MenuShowId = 1;
	private const int MenuSepId = 2;
	private const int MenuQuitId = 3;

	private readonly object _gate = new();
	private Connection? _connection;
	private StatusNotifierItemObject? _item;
	private DbusMenuObject? _menu;
	private IDisposable? _nameWatch;
	private string? _serviceName;
	private Action? _onShow;
	private Action? _onQuit;
	private Action<bool>? _onRegistered;
	private string _tooltip = "Wizionic";
	private bool _started;
	private bool _disposed;
	private bool _registered;

	public bool IsRegistered
	{
		get { lock (_gate) return _registered; }
	}

	public void Start(Action onShow, Action onQuit, Action<bool> onRegistered)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		lock (_gate)
		{
			if (_started)
				return;
			_started = true;
		}

		_onShow = onShow;
		_onQuit = onQuit;
		_onRegistered = onRegistered;
		_ = RunAsync();
	}

	public void SetTooltip(string tooltip)
	{
		_tooltip = string.IsNullOrWhiteSpace(tooltip) ? "Wizionic" : tooltip;
		_item?.SetTooltip(_tooltip);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_ = UnregisterAsync();
	}

	private async Task RunAsync()
	{
		try
		{
			_connection = new Connection(Address.Session);
			await _connection.ConnectAsync().ConfigureAwait(false);

			// Tmds proxies must implement public interfaces (internal → TypeLoadException).
			try
			{
				var dbus = _connection.CreateProxy<IFreedesktopDBus>(
					LinuxStatusNotifierNames.DBusService,
					LinuxStatusNotifierNames.DBusPath);
				_nameWatch = await dbus.WatchNameOwnerChangedAsync(OnNameOwnerChanged)
					.ConfigureAwait(false);
				var hasWatcher = await dbus.NameHasOwnerAsync(LinuxStatusNotifierNames.WatcherService)
					.ConfigureAwait(false);
				if (!hasWatcher)
					Console.WriteLine("[Tray] NameHasOwner=false for StatusNotifierWatcher (still trying register)");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[Tray] watcher probe failed (still trying register): {ex.GetType().Name}: {ex.Message}");
			}

			await RegisterItemAsync().ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Tray] start failed: {ex}");
			NotifyRegistered(false);
		}
	}

	private void OnNameOwnerChanged((string Name, string OldOwner, string NewOwner) e)
	{
		if (!string.Equals(e.Name, LinuxStatusNotifierNames.WatcherService, StringComparison.Ordinal))
			return;

		if (string.IsNullOrEmpty(e.NewOwner))
		{
			Console.WriteLine("[Tray] StatusNotifier watcher lost");
			NotifyRegistered(false);
			return;
		}

		Console.WriteLine("[Tray] StatusNotifier watcher restarted — re-registering");
		_ = RegisterItemAsync();
	}

	private async Task RegisterItemAsync()
	{
		if (_disposed || _connection is null)
			return;

		try
		{
			_item ??= new StatusNotifierItemObject(OnActivate);
			_item.SetTooltip(_tooltip);
			_menu ??= new DbusMenuObject(OnActivate, OnQuit);

			if (_serviceName is null)
			{
				_serviceName = $"org.wizionic.StatusNotifierItem-{Environment.ProcessId}";
				await _connection.RegisterServiceAsync(_serviceName).ConfigureAwait(false);
				await _connection.RegisterObjectAsync(_item).ConfigureAwait(false);
				await _connection.RegisterObjectAsync(_menu).ConfigureAwait(false);
			}

			var watcher = _connection.CreateProxy<IStatusNotifierWatcher>(
				LinuxStatusNotifierNames.WatcherService,
				LinuxStatusNotifierNames.WatcherPath);
			await watcher.RegisterStatusNotifierItemAsync(_serviceName).ConfigureAwait(false);
			Console.WriteLine($"[Tray] registered {_serviceName}");
			NotifyRegistered(true);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Tray] register failed: {ex}");
			NotifyRegistered(false);
		}
	}

	private async Task UnregisterAsync()
	{
		try { _nameWatch?.Dispose(); }
		catch { /* ignore */ }
		_nameWatch = null;

		try
		{
			if (_connection is not null && !string.IsNullOrEmpty(_serviceName))
				await _connection.UnregisterServiceAsync(_serviceName).ConfigureAwait(false);
		}
		catch { /* ignore */ }

		try { _connection?.Dispose(); }
		catch { /* ignore */ }
		_connection = null;
		NotifyRegistered(false);
		Console.WriteLine("[Tray] unexported");
	}

	private void OnActivate() => _onShow?.Invoke();

	private void OnQuit() => _onQuit?.Invoke();

	private void NotifyRegistered(bool value)
	{
		lock (_gate)
			_registered = value;
		try { _onRegistered?.Invoke(value); }
		catch (Exception ex) { Console.WriteLine($"[Tray] onRegistered: {ex.Message}"); }
	}

	private sealed class StatusNotifierItemObject : IStatusNotifierItem
	{
		private readonly Action _onActivate;
		private readonly object _gate = new();
		private string _tooltip = "Wizionic";
		private Action? _newTitle;
		private Action? _newIcon;
		private Action? _newToolTip;
		private Action<string>? _newStatus;

		public ObjectPath ObjectPath { get; } = new(LinuxStatusNotifierNames.ItemPath);

		public StatusNotifierItemObject(Action onActivate)
		{
			_onActivate = onActivate;
		}

		public void SetTooltip(string tooltip)
		{
			lock (_gate)
				_tooltip = tooltip;
			try { _newToolTip?.Invoke(); }
			catch { /* ignore */ }
			try { _newStatus?.Invoke("Active"); }
			catch { /* ignore */ }
		}

		public Task ContextMenuAsync(int x, int y) => Task.CompletedTask;

		public Task ActivateAsync(int x, int y)
		{
			_onActivate();
			return Task.CompletedTask;
		}

		public Task SecondaryActivateAsync(int x, int y)
		{
			_onActivate();
			return Task.CompletedTask;
		}

		public Task ScrollAsync(int delta, string orientation) => Task.CompletedTask;

		public Task<object> GetAsync(string prop)
		{
			var all = BuildProps();
			object value = prop switch
			{
				nameof(StatusNotifierItemProperties.Category) => all.Category,
				nameof(StatusNotifierItemProperties.Id) => all.Id,
				nameof(StatusNotifierItemProperties.Title) => all.Title,
				nameof(StatusNotifierItemProperties.Status) => all.Status,
				nameof(StatusNotifierItemProperties.WindowId) => all.WindowId,
				nameof(StatusNotifierItemProperties.IconName) => all.IconName,
				nameof(StatusNotifierItemProperties.IconPixmap) => all.IconPixmap,
				nameof(StatusNotifierItemProperties.OverlayIconName) => all.OverlayIconName,
				nameof(StatusNotifierItemProperties.OverlayIconPixmap) => all.OverlayIconPixmap,
				nameof(StatusNotifierItemProperties.AttentionIconName) => all.AttentionIconName,
				nameof(StatusNotifierItemProperties.AttentionIconPixmap) => all.AttentionIconPixmap,
				nameof(StatusNotifierItemProperties.AttentionMovieName) => all.AttentionMovieName,
				nameof(StatusNotifierItemProperties.IconThemePath) => all.IconThemePath,
				nameof(StatusNotifierItemProperties.Menu) => all.Menu,
				nameof(StatusNotifierItemProperties.ItemIsMenu) => all.ItemIsMenu,
				nameof(StatusNotifierItemProperties.ToolTip) => all.ToolTip,
				_ => ""
			};
			return Task.FromResult(value);
		}

		public Task<StatusNotifierItemProperties> GetAllAsync() => Task.FromResult(BuildProps());

		public Task SetAsync(string prop, object val) => Task.CompletedTask;

		public Task<IDisposable> WatchNewTitleAsync(Action handler)
			=> Watch(ref _newTitle, handler);

		public Task<IDisposable> WatchNewIconAsync(Action handler)
			=> Watch(ref _newIcon, handler);

		public Task<IDisposable> WatchNewToolTipAsync(Action handler)
			=> Watch(ref _newToolTip, handler);

		public Task<IDisposable> WatchNewStatusAsync(Action<string> handler)
			=> Watch(ref _newStatus, handler);

		private StatusNotifierItemProperties BuildProps()
		{
			string tip;
			lock (_gate)
				tip = _tooltip;

			var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			var themePath = string.IsNullOrEmpty(home)
				? ""
				: Path.Combine(home, ".local", "share", "icons");

			return new StatusNotifierItemProperties
			{
				Category = "ApplicationStatus",
				Id = LinuxDesktopIcon.ApplicationId,
				Title = LinuxDesktopIcon.ApplicationName,
				Status = "Active",
				IconName = LinuxDesktopIcon.ApplicationId,
				IconThemePath = themePath,
				Menu = new ObjectPath(LinuxStatusNotifierNames.MenuPath),
				ItemIsMenu = false,
				ToolTip = ("", [], LinuxDesktopIcon.ApplicationName, tip)
			};
		}

		private static Task<IDisposable> Watch(ref Action? evt, Action handler)
		{
			evt += handler;
			var field = evt;
			return Task.FromResult<IDisposable>(new ActionDisposable(() => { /* best-effort */ _ = field; }));
		}

		private static Task<IDisposable> Watch(ref Action<string>? evt, Action<string> handler)
		{
			evt += handler;
			var field = evt;
			return Task.FromResult<IDisposable>(new ActionDisposable(() => { _ = field; }));
		}
	}

	private sealed class DbusMenuObject : IDbusMenu
	{
		private readonly Action _onShow;
		private readonly Action _onQuit;
		private Action<(uint, int)>? _layoutUpdated;

		public ObjectPath ObjectPath { get; } = new(LinuxStatusNotifierNames.MenuPath);

		public DbusMenuObject(Action onShow, Action onQuit)
		{
			_onShow = onShow;
			_onQuit = onQuit;
		}

		public Task<(uint, (int, IDictionary<string, object>, object[]))> GetLayoutAsync(
			int parentId, int recursionDepth, string[] propertyNames)
		{
			object[] children =
			[
				LayoutItem(MenuShowId, LabelProps("Show Wizionic")),
				LayoutItem(MenuSepId, SepProps()),
				LayoutItem(MenuQuitId, LabelProps("Quit"))
			];

			IDictionary<string, object> root = new Dictionary<string, object>
			{
				["children-display"] = "submenu"
			};

			return Task.FromResult((1u, (0, root, children)));
		}

		public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames)
		{
			var list = new List<(int, IDictionary<string, object>)>();
			foreach (var id in ids)
			{
				var props = id switch
				{
					MenuShowId => LabelProps("Show Wizionic"),
					MenuSepId => SepProps(),
					MenuQuitId => LabelProps("Quit"),
					0 => new Dictionary<string, object> { ["children-display"] = "submenu" },
					_ => new Dictionary<string, object>()
				};
				list.Add((id, props));
			}

			return Task.FromResult(list.ToArray());
		}

		public Task<object> GetPropertyAsync(int id, string name)
		{
			var props = id switch
			{
				MenuShowId => LabelProps("Show Wizionic"),
				MenuSepId => SepProps(),
				MenuQuitId => LabelProps("Quit"),
				_ => new Dictionary<string, object>()
			};
			return Task.FromResult(props.TryGetValue(name, out var v) ? v : (object)"");
		}

		public Task EventAsync(int id, string eventId, object data, uint timestamp)
		{
			if (string.Equals(eventId, "clicked", StringComparison.OrdinalIgnoreCase))
			{
				if (id == MenuShowId)
					_onShow();
				else if (id == MenuQuitId)
					_onQuit();
			}

			return Task.CompletedTask;
		}

		public Task<int[]> EventGroupAsync((int, string, object, uint)[] events)
		{
			foreach (var e in events)
				_ = EventAsync(e.Item1, e.Item2, e.Item3, e.Item4);
			return Task.FromResult(Array.Empty<int>());
		}

		public Task<bool> AboutToShowAsync(int id) => Task.FromResult(false);

		public Task<(int[], int[])> AboutToShowGroupAsync(int[] ids)
			=> Task.FromResult((Array.Empty<int>(), Array.Empty<int>()));

		public Task<object> GetAsync(string prop)
		{
			object value = prop switch
			{
				nameof(DbusMenuProperties.Version) => 4u,
				nameof(DbusMenuProperties.TextDirection) => "ltr",
				nameof(DbusMenuProperties.Status) => "normal",
				nameof(DbusMenuProperties.IconThemePath) => Array.Empty<string>(),
				_ => ""
			};
			return Task.FromResult(value);
		}

		public Task<DbusMenuProperties> GetAllAsync() => Task.FromResult(new DbusMenuProperties());

		public Task SetAsync(string prop, object val) => Task.CompletedTask;

		public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint, int)> handler)
		{
			_layoutUpdated += handler;
			return Task.FromResult<IDisposable>(new ActionDisposable(() => { }));
		}

		private static object LayoutItem(int id, IDictionary<string, object> props)
			=> (id, props, Array.Empty<object>());

		private static IDictionary<string, object> LabelProps(string label)
			=> new Dictionary<string, object>
			{
				["label"] = label,
				["enabled"] = true,
				["visible"] = true
			};

		private static IDictionary<string, object> SepProps()
			=> new Dictionary<string, object>
			{
				["type"] = "separator",
				["visible"] = true
			};
	}
}
