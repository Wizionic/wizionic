using Tmds.DBus;

namespace App.Maui;

internal static class LinuxStatusNotifierNames
{
	public const string WatcherService = "org.kde.StatusNotifierWatcher";
	public const string WatcherPath = "/StatusNotifierWatcher";
	public const string ItemInterface = "org.kde.StatusNotifierItem";
	public const string ItemPath = "/StatusNotifierItem";
	public const string MenuInterface = "com.canonical.dbusmenu";
	public const string MenuPath = "/MenuBar";
	public const string DBusService = "org.freedesktop.DBus";
	public const string DBusPath = "/org/freedesktop/DBus";
}

[DBusInterface(LinuxStatusNotifierNames.WatcherService)]
public interface IStatusNotifierWatcher : IDBusObject
{
	Task RegisterStatusNotifierItemAsync(string service);
}

[DBusInterface(LinuxStatusNotifierNames.DBusService)]
public interface IFreedesktopDBus : IDBusObject
{
	Task<bool> NameHasOwnerAsync(string name);
	Task<IDisposable> WatchNameOwnerChangedAsync(Action<(string, string, string)> handler);
}

[DBusInterface(LinuxStatusNotifierNames.ItemInterface)]
public interface IStatusNotifierItem : IDBusObject
{
	Task ContextMenuAsync(int x, int y);
	Task ActivateAsync(int x, int y);
	Task SecondaryActivateAsync(int x, int y);
	Task ScrollAsync(int delta, string orientation);
	Task<object> GetAsync(string prop);
	Task<StatusNotifierItemProperties> GetAllAsync();
	Task SetAsync(string prop, object val);
	Task<IDisposable> WatchNewTitleAsync(Action handler);
	Task<IDisposable> WatchNewIconAsync(Action handler);
	Task<IDisposable> WatchNewToolTipAsync(Action handler);
	Task<IDisposable> WatchNewStatusAsync(Action<string> handler);
}

[Dictionary]
public class StatusNotifierItemProperties
{
	public string Category = "ApplicationStatus";
	public string Id = "com.wizionic.app";
	public string Title = "Wizionic";
	public string Status = "Active";
	public int WindowId = 0;
	public string IconName = "com.wizionic.app";
	public (int, int, byte[])[] IconPixmap = [];
	public string OverlayIconName = "";
	public (int, int, byte[])[] OverlayIconPixmap = [];
	public string AttentionIconName = "";
	public (int, int, byte[])[] AttentionIconPixmap = [];
	public string AttentionMovieName = "";
	public string IconThemePath = "";
	public ObjectPath Menu = new(LinuxStatusNotifierNames.MenuPath);
	public bool ItemIsMenu;
	public (string, (int, int, byte[])[], string, string) ToolTip = ("", [], "Wizionic", "Wizionic");
}

[DBusInterface(LinuxStatusNotifierNames.MenuInterface)]
public interface IDbusMenu : IDBusObject
{
	Task<(uint, (int, IDictionary<string, object>, object[]))> GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames);
	Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] ids, string[] propertyNames);
	Task<object> GetPropertyAsync(int id, string name);
	Task EventAsync(int id, string eventId, object data, uint timestamp);
	Task<int[]> EventGroupAsync((int, string, object, uint)[] events);
	Task<bool> AboutToShowAsync(int id);
	Task<(int[], int[])> AboutToShowGroupAsync(int[] ids);
	Task<object> GetAsync(string prop);
	Task<DbusMenuProperties> GetAllAsync();
	Task SetAsync(string prop, object val);
	Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint, int)> handler);
}

[Dictionary]
public class DbusMenuProperties
{
	public uint Version = 4;
	public string TextDirection = "ltr";
	public string Status = "normal";
	public string[] IconThemePath = [];
}

internal sealed class ActionDisposable : IDisposable
{
	private Action? _dispose;

	public ActionDisposable(Action dispose) => _dispose = dispose;

	public void Dispose()
	{
		var d = Interlocked.Exchange(ref _dispose, null);
		d?.Invoke();
	}
}
