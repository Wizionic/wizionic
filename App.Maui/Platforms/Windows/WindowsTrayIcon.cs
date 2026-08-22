using System.Runtime.InteropServices;

namespace App.Maui;

/// <summary>
/// Win32 tray icon on the existing WinUI HWND (subclass so TaskbarCreated broadcasts arrive).
/// </summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _iconOwned;
    private bool _added;
    private bool _version4;
    private bool _useGuid = true;
    private uint _taskbarCreated;
    private NativeMethods.SUBCLASSPROC? _proc;
    private Action? _onShow;
    private Action? _onQuit;
    private Action? _onNewWindow;
    private string _tooltip = "Wizionic";
    private bool _disposed;

    public void Attach(IntPtr hwnd, Action onShow, Action onQuit, Action? onNewWindow = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hwnd = hwnd;
        _onShow = onShow;
        _onQuit = onQuit;
        _onNewWindow = onNewWindow;
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _hIcon = LoadTrayIcon();
        _proc = WndProc;
        if (!NativeMethods.SetWindowSubclass(hwnd, _proc, NativeMethods.TraySubclassId, 0))
            Console.WriteLine($"[Tray] SetWindowSubclass failed (err={Marshal.GetLastWin32Error()})");
        AddIcon();
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip.Length <= 127 ? tooltip : tooltip[..127];
        if (!_added)
            return;
        var data = BuildData(NativeMethods.NIF_TIP);
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <returns>true if Shell_NotifyIcon accepted the balloon (Focus Assist may still hide it).</returns>
    public bool ShowBalloon(string title, string text)
    {
        if (!_added)
            return false;

        var data = BuildData(NativeMethods.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NativeMethods.NIIF_INFO | NativeMethods.NIIF_NOSOUND;
        var ok = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        Console.WriteLine(ok ? "[Tray] balloon requested" : "[Tray] balloon NIM_MODIFY failed");
        return ok;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = BuildData(0);
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_proc is not null && _hwnd != IntPtr.Zero)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _proc, NativeMethods.TraySubclassId);
            _proc = null;
        }

        if (_iconOwned && _hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
            _iconOwned = false;
        }

        _hwnd = IntPtr.Zero;
        _onShow = null;
        _onQuit = null;
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _useGuid = true;
        var data = BuildData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
            {
                _useGuid = false;
                data = BuildData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP);
                if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data))
                {
                    Console.WriteLine("[Tray] NIM_ADD failed");
                    return;
                }
            }
        }

        _added = true;
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        _version4 = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref data);
        Console.WriteLine(_version4
            ? "[Tray] NIM_ADD (NOTIFYICON_VERSION_4)"
            : "[Tray] NIM_ADD (classic callback messages)");
    }

    private NativeMethods.NOTIFYICONDATAW BuildData(uint extraFlags)
    {
        var flags = extraFlags;
        if (_useGuid)
            flags |= NativeMethods.NIF_GUID;

        return new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = _useGuid ? 0u : 1u,
            uFlags = flags,
            uCallbackMessage = NativeMethods.WM_TRAY,
            hIcon = _hIcon,
            szTip = _tooltip ?? string.Empty,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = _useGuid ? NativeMethods.TrayGuid : Guid.Empty,
        };
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        try
        {
            if (_taskbarCreated != 0 && uMsg == _taskbarCreated)
            {
                Console.WriteLine("[Tray] TaskbarCreated — re-adding icon");
                _added = false;
                AddIcon();
                return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
            }

            if (uMsg == NativeMethods.WM_TRAY)
            {
                var eventMsg = _version4
                    ? (uint)(lParam.ToInt64() & 0xFFFF)
                    : unchecked((uint)lParam.ToInt64());

                if (_version4)
                {
                    if (eventMsg is NativeMethods.NIN_SELECT or NativeMethods.NIN_KEYSELECT)
                        _onShow?.Invoke();
                    else if (eventMsg == NativeMethods.WM_CONTEXTMENU)
                        ShowContextMenu();
                }
                else
                {
                    if (eventMsg == NativeMethods.WM_LBUTTONUP)
                        _onShow?.Invoke();
                    else if (eventMsg == NativeMethods.WM_RBUTTONUP)
                        ShowContextMenu();
                }

                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Tray] WndProc: {ex.Message}");
        }

        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.ID_SHOW, "Show Wizionic");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.ID_NEW, "New window");
            NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (UIntPtr)NativeMethods.ID_QUIT, "Quit");

            NativeMethods.GetCursorPos(out var pt);
            NativeMethods.SetForegroundWindow(_hwnd);
            var cmd = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD,
                pt.X,
                pt.Y,
                0,
                _hwnd,
                IntPtr.Zero);
            NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == NativeMethods.ID_SHOW)
                _onShow?.Invoke();
            else if (cmd == NativeMethods.ID_NEW)
                _onNewWindow?.Invoke();
            else if (cmd == NativeMethods.ID_QUIT)
                _onQuit?.Invoke();
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private IntPtr LoadTrayIcon()
    {
        var path = WindowsIconPath.Resolve();
        if (path is not null)
        {
            var handle = NativeMethods.LoadImage(
                IntPtr.Zero,
                path,
                NativeMethods.IMAGE_ICON,
                0,
                0,
                NativeMethods.LR_LOADFROMFILE | NativeMethods.LR_DEFAULTSIZE);
            if (handle != IntPtr.Zero)
            {
                _iconOwned = true;
                return handle;
            }
        }

        var stock = NativeMethods.LoadIcon(IntPtr.Zero, (IntPtr)NativeMethods.IDI_APPLICATION);
        _iconOwned = false;
        return stock;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
