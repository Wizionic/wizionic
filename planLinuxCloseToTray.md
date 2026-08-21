# Linux GirCore close-to-tray (PR 6)

| Field | Value |
|-------|--------|
| **Date** | 2026-03-22 |
| **Machine** | Implement on a **Linux** host (`net10.0` / `LINUX_DESKTOP`). Do not add SNI/Ayatana packages from Windows. |
| **Depends on** | Windows train PRs 1–5 already on `feature/systray` (due host, `IDesktopShellService`, C# sync start, Settings Desktop card, `TrayRestoreFlag`). |
| **Related** | `planWindowsCloseToTray.md` §12, `ARCHITECTURE.md` “Windows desktop agent (tray)” |

This is an implementation plan only. Same product as Windows: **close hides; Quit exits**; sync + workflows keep running while the process is alive.

---

## What is already done (do not redo)

On Linux `CreateLinuxServiceProvider()` (`App.Maui/MauiProgram.cs`):

- `WorkflowDueHost.Start()` — 8s / 1min due loop, not Blazor.
- `StartMauiSyncAsync` — hub connect without first WebView paint.
- `IDesktopShellService` → `NullDesktopShellService` (`IsSupported = false`) so Settings **Desktop** is hidden.
- `MauiAppRestartService` / `MauiUpdateService` already call `PrepareForProcessExit()` and write `tray-restore.flag` when `IsHidden`.
- SQLite prefs keys already defined by Windows: `app-close-to-tray` (default 1), `app-start-with-windows` (0), `app-start-minimized` (1), `app-tray-hint-shown`.
- `SqliteSettingsDatabase` on Linux uses `~/.local/share/Wizionic` (`MauiAppData.Directory`).

**Do not** use `Shell_NotifyIcon`, HWND subclass, named mutex, or HKCU Run.

---

## Current Linux close path (the bug)

[`App.Maui/Platforms/Linux/Program.cs`](App.Maui/Platforms/Linux/Program.cs):

```
Main → Adw.Application.New("com.wizionic.app", FlagsNone)
     → RunWithSynchronizationContext
     → OnActivate always:
           Adw.ApplicationWindow.New
           CreateLinuxServiceProvider()   // DI, due host, sync
           BlazorWebView + LinuxBrowserHost
           Present()
     → no OnCloseRequest
     → HeaderBar close / WM delete DESTROYS the window
     → Gio.Application shuts down (OnShutdown frees GCHandles)
     → process dies
```

Two extra bugs you must fix in the same PR:

1. **Second activate builds a second app.** D-Bus unique name `com.wizionic.app` already routes a second launch into the running process’s `OnActivate`, but that handler **always** `New()`s a window **and** calls `CreateLinuxServiceProvider()` again (second SQLite writers, second hub). If `_window` exists, **only** `Present()` / unhide.
2. **Hiding the last Gtk window quits Gio.Application.** After cancel-close + `SetVisible(false)`, call `_application.Hold()` (once). `Release()` on real Quit.

GirCore packages (already in `App.Maui.csproj` for `net10.0`): `GirCore.Adw-1` / `Gtk-4.0` / `WebKit-6.0` **0.7.0-preview.2**. No StatusNotifier package today.

---

## Product rules (match Windows)

| Gesture | Close-to-tray ON (default) | OFF or no SNI watcher |
|---------|----------------------------|------------------------|
| HeaderBar close / WM delete | Hide; process + WebKit + due host + sync stay | Process exit |
| Tray **Show Wizionic** / second launcher click | `SetVisible(true)` + `Present()` | n/a |
| Tray **Quit** / Settings **Quit Wizionic** | Real exit | Real exit |
| Launcher click (no `--start-minimized`) | Always show | Always show |
| Session autostart with start-minimized | Hide after create (unless setup wizard `ShouldAutoShow`) | n/a |

No Quit confirm dialog. Tooltip/menu: **Show / Quit** only; `Wizionic — Connected` / `Offline` — no note titles.

**GNOME without AppIndicator:** there is often **no** `org.kde.StatusNotifierWatcher`. Then **do not** swallow close. Log `[Tray] no StatusNotifier watcher` and let close quit. KDE Plasma and Ubuntu (GNOME + AppIndicator) should show the icon.

---

## Recommended implementation order

Ship as one PR on Linux, but land internally in this order so you can test each slice.

### Slice A — Second activate must not rebuild the world

**File:** `App.Maui/Platforms/Linux/Program.cs`

```csharp
private static void OnActivate(Gio.Application sender, EventArgs args)
{
    if (_window is not null)
    {
        _serviceProvider?.GetService<IDesktopShellService>()?.Show();
        _window.SetVisible(true);
        _window.Present();
        return;
    }
    // existing first-time construction (CreateLinuxServiceProvider once)
}
```

Manual: launch app, from a terminal start a second `Wizionic` / AppImage → **one** process (`pgrep -a Wizionic`), existing window focused. No second WebKit, no SQLite lock errors.

### Slice B — `LinuxDesktopHost` + prefs + Settings card

**New:** `App.Maui/Platforms/Linux/LinuxDesktopHost.cs`  
**Register** in `RegisterAppServices`:

```csharp
#if WINDOWS
    services.AddSingleton<WindowsDesktopHost>();
    services.AddSingleton<IDesktopShellService>(sp => sp.GetRequiredService<WindowsDesktopHost>());
#elif LINUX_DESKTOP
    services.AddSingleton<LinuxDesktopHost>();
    services.AddSingleton<IDesktopShellService>(sp => sp.GetRequiredService<LinuxDesktopHost>());
#else
    services.AddSingleton<IDesktopShellService>(_ => NullDesktopShellService.Instance);
#endif
```

Mirror `WindowsDesktopHost` **without** Win32:

- `IsSupported => true`
- Inject `WorkflowDueHost`, `ISyncService`, `SqliteSettingsDatabase`, `ISetupWizardHost`, `IServiceProvider`
- Same keys / defaults / async `LoadPrefsAsync` (CloseToTray default **true** until SQLite returns)
- `AcknowledgeTrayHintAsync` → `app-tray-hint-shown=1`
- `Show()` / `HideToTray()` / `RequestQuit()` / `PrepareForProcessExit()` marshaled onto the **GLib main loop** (`GLib.Functions.IdleAdd` or GirCore dispatcher already captured by `AddBlazorWebView`). Do not touch GTK from a threadpool thread.
- `PrepareForProcessExit`: drop tray, `WorkflowDueHost.Stop()`, **do not** `Application.Quit()` (restart/update path). `RequestQuit` does that after `ISyncService.DisposeAsync`.
- Consume `TrayRestoreFlag.ConsumeHidden()` on first window (same as Windows).

**Attach from `Program.OnActivate` after `Present()` (or before, if hiding):**

```csharp
_serviceProvider.GetRequiredService<LinuxDesktopHost>()
    .Attach(_application, _window);
```

Keep `Attach` **off** `IDesktopShellService` (Windows already did this).

**Settings** (`App.Shared/Components/SettingsPage.razor`): card already appears when `IsSupported`. Relabel for Linux:

- “Start with Windows” → **Start with session**
- Hint: “Launch at login so this PC can stay online…”
- “Start minimized at logon” → “Only used with Start with session. The app launcher always shows the window.”

Use `OperatingSystem.IsLinux()` (or a small `AutostartLabel` on the interface). Do **not** sync these prefs (`SettingsSyncCategory`).

### Slice C — Close-request + `Hold()`

On the `Adw.ApplicationWindow` (GTK4 `Gtk.Window`):

```csharp
_window.OnCloseRequest += (_, _) =>
{
    var host = _serviceProvider?.GetService<IDesktopShellService>();
    if (host is { CloseToTray: true } && host is LinuxDesktopHost linux
        && linux.CanHideToTray) // watcher present
    {
        linux.HideToTray();
        return true; // stop destroy
    }
    return false; // default destroy → app exit
};
```

`HideToTray()`:

1. `_window.SetVisible(false)` — do **not** `Destroy()`.
2. If not already held: `_application.Hold()`.
3. Leave `BlazorWebView`, `LinuxBrowserHost` overlays, DI alive.
4. Optional notification/balloon: Linux has no `NIF_INFO`. Skip or one-shot `Gio.Notification` via `Adw.Application.SendNotification`. Opening Settings Desktop still counts as seeing the hint.

`RequestQuit()`:

1. `_quitRequested = true`
2. `PrepareForProcessExit()` (unexport SNI, `Stop()` due host)
3. `_application.Release()` if Hold was taken
4. `_window.Destroy()` / `_application.Quit()`

`OnShutdown` already frees GCHandles — keep that as the last step of real quit.

**CanHideToTray:** `false` until SNI registration succeeds. If watcher missing, CloseToTray toggle can stay in Settings but close still quits (or disable the toggle and show “Install the AppIndicator extension on GNOME”). Prefer: **toggle works only when watcher exists**; otherwise log and close=quit so users are not stuck with no UI and no tray.

### Slice D — StatusNotifierItem

GTK4 has no tray widget. libappindicator is GTK3. Implement **StatusNotifierItem** on the session bus.

**New files:**

| File | Role |
|------|------|
| `App.Maui/Platforms/Linux/LinuxTrayIcon.cs` | Export item + register with watcher |
| Optional `LinuxStatusNotifier.cs` | D-Bus XML / proxies |

**Watcher probe (do this first, before exporting):**

```
org.kde.StatusNotifierWatcher
object path /StatusNotifierWatcher
method RegisterStatusNotifierItem(string service)
```

`NameHasOwner` on the session bus. If false → no tray, `CanHideToTray = false`.

**Item** (`org.kde.StatusNotifierItem`):

- `Id` = `com.wizionic.app`
- `Title` = `Wizionic`
- `Status` = `Active`
- `IconName` = `com.wizionic.app` (already installed by `LinuxDesktopIcon` into hicolor) **or** `IconPixmap` from `LinuxDesktopIcon.ResolveIconPathPublic()`
- `Category` = `ApplicationStatus`
- `ItemIsMenu` = false
- `Activate(x,y)` → Show
- `ContextMenu(x,y)` → Show/Quit menu
- `ToolTip` → Connected/Offline from `ISyncService.IsConnected` (subscribe `OnChanged`, marshal to GLib)

**Menu:** DBus menu (`com.canonical.dbusmenu`) is the SNI way; a tiny GTK popover at pointer is acceptable for v1 if DBus menu is too much. Keep two items: **Show Wizionic**, **Quit**.

**Library choice:**

1. **Prefer GirCore `Gio.DBusConnection`** (already linked). `Gio.Bus.OwnName` / `Gio.DBusConnection.GetSync(Gio.BusType.Session, …)`.
2. If the 0.7 preview bindings are too thin for exporting objects, add **`Tmds.DBus`** (Linux `net10.0` only). Do not add Ayatana GTK3 packages.

Keep the SNI unique name stable, e.g. `org.wizionic.StatusNotifierItem-` + pid.

**Icon lifetime:** export after `LinuxDesktopIcon.Apply(_window)` so the theme name exists. Re-register if the watcher restarts (`NameOwnerChanged` on `org.kde.StatusNotifierWatcher`) — equivalent of Windows `TaskbarCreated`.

### Slice E — XDG autostart (not HKCU, not homeserver)

**Do not** write `HomeserverPaths.LinuxAutostartDesktopPath` (`~/.config/autostart/wizionic-homeserver.desktop`). That is the Home Server unit helper.

**App path:** `~/.config/autostart/com.wizionic.app.desktop`

Make `LinuxDesktopIcon.ResolveExecPath()` **public** (today it is `private`). Exec rules:

| Launch | `Exec=` |
|--------|---------|
| AppImage | `$APPIMAGE` (env), quoted. Never the `/tmp/.mount_*` path. |
| Installed apphost | `…/Wizionic` from `ResolveExecPath()` |
| `dotnet run` | `dotnet /full/path/Wizionic.dll` as today |

When **Start minimized** is on, append ` --start-minimized`.

```
[Desktop Entry]
Type=Application
Name=Wizionic
Exec=<quoted-exec> [--start-minimized]
Icon=com.wizionic.app
X-GNOME-Autostart-enabled=true
StartupNotify=false
```

Enabling writes; disabling **deletes** the file. Uninstall / Velopack: best-effort delete in a Linux uninstall hook if one exists; otherwise document “turn off Start with session before uninstall.”

Parse `Environment.GetCommandLineArgs()` for `--start-minimized` / `--tray` in `Attach`. If set **and** `!ISetupWizardHost.ShouldAutoShow`, hide before/immediately after first `Present()`. One frame of flash is OK. Launcher without the flag always `Present()`s.

`SetStartWithWindowsAsync` on Linux = write/delete this autostart file (`IUpdateService.IsVelopackInstalled` is **not** required; use `APPIMAGE` / `ResolveExecPath`).

### Slice F — Docs

- `ARCHITECTURE.md`: short **Linux desktop agent (tray)** note next to the Windows subsection (Hold, SNI, XDG autostart, GNOME fallback).
- `docs/user/settings.md` Desktop section: Windows **and** Linux; copy to `App.Shared/wwwroot/help/`.
- `docs/user/troubleshooting.md`: GNOME “no tray icon” → AppIndicator extension **or** close really quits.
- Help catalog `settings-desktop` already exists.

---

## D-Bus sketch (StatusNotifierItem)

```
Session bus
  org.kde.StatusNotifierWatcher
    /StatusNotifierWatcher
      RegisterStatusNotifierItem("org.wizionic.StatusNotifierItem-<pid>")

  org.wizionic.StatusNotifierItem-<pid>
    /StatusNotifierItem
      org.kde.StatusNotifierItem
        Activate(i,i)
        ContextMenu(i,i)
        properties: Id, Title, Status, IconName, ToolTip, ItemIsMenu, Menu
      org.freedesktop.DBus.Properties
```

Probe before hide-to-tray:

```bash
gdbus call --session \
  --dest org.kde.StatusNotifierWatcher \
  --object-path /StatusNotifierWatcher \
  --method org.freedesktop.DBus.Peer.Ping
```

If this fails on the machine, hide-to-tray must not swallow close.

---

## Files to touch

| File | Change |
|------|--------|
| `App.Maui/Platforms/Linux/Program.cs` | Guard `OnActivate`; `OnCloseRequest`; `Hold`/`Release`; `LinuxDesktopHost.Attach` |
| `App.Maui/Platforms/Linux/LinuxDesktopHost.cs` | **New** — `IDesktopShellService` |
| `App.Maui/Platforms/Linux/LinuxTrayIcon.cs` | **New** — SNI |
| `App.Maui/Platforms/Linux/LinuxAutostartRegistration.cs` | **New** — XDG autostart file |
| `App.Maui/Services/Linux/LinuxDesktopIcon.cs` | Public `ResolveExecPath()` |
| `App.Maui/MauiProgram.cs` | `#elif LINUX_DESKTOP` register host |
| `App.Shared/Components/SettingsPage.razor` | Linux autostart labels |
| `ARCHITECTURE.md`, `docs/user/settings.md`, `troubleshooting.md`, `wwwroot/help/*` | Linux tray docs |
| `App.Maui.csproj` | Only if you add `Tmds.DBus` (`net10.0` only) |

No EF migrations. No Windows files.

---

## Pitfalls

| Risk | Mitigation |
|------|------------|
| Second `OnActivate` rebuilds DI | Slice A first; never call `CreateLinuxServiceProvider()` twice |
| Hide last window → Gio exits | `Hold()` on first successful tray hide; `Release()` on Quit |
| `OnCloseRequest` return value inverted | GTK4: **return true** = handle and **stop** destroy |
| GNOME stock has no watcher | Probe watcher; if missing, close = quit |
| GTK calls off main thread | IdleAdd / GirCore dispatcher for all window/tray mutations |
| AppImage Exec points at fuse mount | Use `Environment.GetEnvironmentVariable("APPIMAGE")` |
| Autostart overwrites homeserver entry | Different filename: `com.wizionic.app.desktop` vs `wizionic-homeserver.desktop` |
| `LinuxBrowserHost` overlays after hide | Do not destroy the window; WebKit views stay children |
| TrayRestoreFlag on Linux | `MauiAppData.Directory` already XDG; consume in `Attach` |
| Browser tools while hidden | Same as Windows: `IBrowserContext` stays window-coupled; do not auto-open the browser panel |

---

## Manual test plan (Linux)

Build: `dotnet build App.Maui/App.Maui.csproj -f net10.0` (on Linux). Or the AppImage pack script.

### A. Close / restore / quit (KDE or Ubuntu+AppIndicator)

1. Sign in, Sync connected.
2. Click window close → window gone, **tray icon present**, `pgrep -a Wizionic` still one process.
3. Left-click tray / Show → window focused; WebKit chat still loaded (not a blank restart).
4. Close again, right-click **Quit** → process gone, icon gone, other device sees this peer offline.
5. Settings → Desktop appears. Disable close-to-tray → close **exits**.
6. Quit Wizionic button exits.

### B. Single Gio application

1. App visible, start a second binary → one process, window presented.
2. Hide to tray, second launch → window shown.
3. Confirm a single `Wizionic` (or AppImage) PID.

### C. No SNI watcher (Fedora/stock GNOME VM)

1. `gdbus … StatusNotifierWatcher … Ping` fails.
2. Close **must quit** (no invisible stuck process).
3. Log line `[Tray] no StatusNotifier watcher`.

### D. Workflows + sync while hidden

1. `once` workflow ~2 min out, skill without Browser.
2. Hide, wait past due minute, Show → run log success.
3. Second device syncs a note while this one is hidden → note present after Show.

### E. Autostart

1. Enable Start with session + start minimized.
2. File exists: `~/.config/autostart/com.wizionic.app.desktop`. `Exec=` is AppImage or apphost, optional `--start-minimized`. **Not** `wizionic-homeserver.desktop`.
3. Log out/in (or `gtk-launch com.wizionic.app` after copying Exec) → tray, no lasting window (one frame OK).
4. Application menu / `.local/share/applications/com.wizionic.app.desktop` click **shows** the window.
5. Disable Start with session → autostart file **deleted**.
6. Incomplete onboarding: window shows despite `--start-minimized`.

### F. Restart / AppImage update

1. Hide, change login server → Restart → should return to tray (`tray-restore.flag`).
2. Uninstall/remove AppImage: autostart file should not launch a missing binary (delete on toggle off; best-effort on uninstall).

---

## Suggested commit title

`Linux desktop: close to StatusNotifierItem tray with Show/Quit`

---

## Out of scope

- Windows P/Invoke / mutex / HKCU
- Headless systemd user service as the workflow runner
- Cron missed-minute backfill
- Ayatana GTK3 indicator
- Changing WASM/PWA background behavior
