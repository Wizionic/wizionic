#if LINUX_DESKTOP
using System.Runtime.Versioning;

namespace App.Maui.Services.Linux;

/// <summary>
/// Installs the Wizionic icon into the user icon theme + a .desktop entry so the
/// GNOME/KDE launch bar can show it, and applies it to the active GTK window.
/// </summary>
/// <remarks>
/// MauiIcon does not apply on plain net10.0 Linux (no MAUI window stack). GTK4
/// windows only expose <c>icon-name</c>; the shell resolves that name via the
/// freedesktop icon theme / matching .desktop file for app-id <c>com.wizionic.app</c>.
/// </remarks>
[UnsupportedOSPlatform("windows")]
[UnsupportedOSPlatform("OSX")]
internal static class LinuxDesktopIcon
{
	public const string ApplicationId = "com.wizionic.app";
	public const string ApplicationName = "Wizionic";

	/// <summary>
	/// Ensure user-local icon + desktop entry exist, then set the window icon.
	/// Safe to call on every startup (idempotent).
	/// </summary>
	public static void Apply(Gtk.Window window)
	{
		try
		{
			var iconPath = ResolveIconPath();
			if (iconPath is null)
			{
				Console.WriteLine("[Desktop] no app icon found next to binary (appicon.png / app.png)");
				return;
			}

			EnsureUserIconTheme(iconPath);
			EnsureDesktopEntry(iconPath);

			// Named icon (shell looks this up from hicolor / desktop file).
			Gtk.Window.SetDefaultIconName(ApplicationId);
			window.SetIconName(ApplicationId);

			// Also push a texture onto the toplevel so X11/Wayland get an icon even
			// before icon-cache refresh / without a desktop-file match.
			TrySetToplevelIcon(window, iconPath);

			Console.WriteLine($"[Desktop] window icon set from {iconPath}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] icon setup failed: {ex.Message}");
		}
	}

	private static string? ResolveIconPath()
	{
		var baseDir = AppContext.BaseDirectory;
		var candidates = new[]
		{
			// Full-res mascot copied by the Linux csproj item (preferred).
			Path.Combine(baseDir, "app-appicon.png"),
			Path.Combine(baseDir, "app.png"),
			// Source-tree fallback when running from bin/Debug/net10.0
			Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Resources", "AppIcon", "app.png")),
			// Last resort: MAUI-generated stub (often tiny / blank-looking).
			Path.Combine(baseDir, "appicon.png"),
		};

		string? best = null;
		long bestSize = -1;
		foreach (var path in candidates)
		{
			try
			{
				if (!File.Exists(path))
					continue;
				var len = new FileInfo(path).Length;
				// Prefer the largest PNG — the MauiIcon pipeline emits a ~1KB stub.
				if (len > bestSize)
				{
					best = path;
					bestSize = len;
				}
			}
			catch
			{
				// skip unreadable
			}
		}

		return best;
	}

	private static void EnsureUserIconTheme(string sourceIconPath)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home))
			return;

		// Install under several sizes so shell scalers always find something.
		// Source is 512×512 (or 256 appicon.png); desktop/icon themes scale as needed.
		var sizes = new[] { "512x512", "256x256", "128x128", "64x64", "48x48", "32x32" };
		foreach (var size in sizes)
		{
			var dir = Path.Combine(home, ".local", "share", "icons", "hicolor", size, "apps");
			Directory.CreateDirectory(dir);
			var dest = Path.Combine(dir, ApplicationId + ".png");
			CopyIfNewer(sourceIconPath, dest);
		}

		// Also as "Wizionic.png" for any manual Icon=Wizionic desktop entries.
		var apps512 = Path.Combine(home, ".local", "share", "icons", "hicolor", "512x512", "apps");
		CopyIfNewer(sourceIconPath, Path.Combine(apps512, ApplicationName + ".png"));

		// Best-effort icon cache refresh (ignore if gtk-update-icon-cache missing).
		try
		{
			var hicolor = Path.Combine(home, ".local", "share", "icons", "hicolor");
			var psi = new System.Diagnostics.ProcessStartInfo
			{
				FileName = "gtk-update-icon-cache",
				Arguments = $"-f -t \"{hicolor}\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using var proc = System.Diagnostics.Process.Start(psi);
			proc?.WaitForExit(2000);
		}
		catch
		{
			// optional tool
		}
	}

	private static void EnsureDesktopEntry(string sourceIconPath)
	{
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home))
			return;

		var appsDir = Path.Combine(home, ".local", "share", "applications");
		Directory.CreateDirectory(appsDir);
		var desktopPath = Path.Combine(appsDir, ApplicationId + ".desktop");

		var exec = ResolveExecPath();
		if (string.IsNullOrEmpty(exec))
			return;

		// Quote for desktop-entry Exec key (spaces etc.).
		var execQuoted = exec.Contains(' ', StringComparison.Ordinal)
			? $"\"{exec.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
			: exec;

		var content =
			$"""
			[Desktop Entry]
			Type=Application
			Version=1.0
			Name={ApplicationName}
			Comment=Privacy-first AI chat hub
			Exec={execQuoted}
			Icon={ApplicationId}
			Terminal=false
			Categories=Network;Office;Chat;
			StartupNotify=true
			StartupWMClass={ApplicationId}
			X-GNOME-UsesNotifications=true

			""";

		// Rewrite when missing or Exec path changed (debug builds move around).
		var shouldWrite = !File.Exists(desktopPath);
		if (!shouldWrite)
		{
			try
			{
				var existing = File.ReadAllText(desktopPath);
				shouldWrite = !existing.Contains(exec, StringComparison.Ordinal)
					|| !existing.Contains($"Icon={ApplicationId}", StringComparison.Ordinal);
			}
			catch
			{
				shouldWrite = true;
			}
		}

		if (shouldWrite)
		{
			File.WriteAllText(desktopPath, content);
			try
			{
				// Make executable — some launchers require +x on .desktop files.
				File.SetUnixFileMode(
					desktopPath,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
					| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
					| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
			}
			catch
			{
				// ignore chmod failures
			}

			Console.WriteLine($"[Desktop] wrote {desktopPath}");
		}

		// Keep the source icon path referenced for debugging only.
		_ = sourceIconPath;
	}

	private static string? ResolveExecPath()
	{
		var baseDir = AppContext.BaseDirectory;
		var apphost = Path.Combine(baseDir, "Wizionic");
		if (File.Exists(apphost))
			return Path.GetFullPath(apphost);

		var processPath = Environment.ProcessPath;
		if (!string.IsNullOrEmpty(processPath) && File.Exists(processPath))
		{
			// When launched via `dotnet Wizionic.dll`, ProcessPath is the host — prefer dll invocation.
			var fileName = Path.GetFileName(processPath);
			if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
				|| fileName.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
			{
				var dll = Path.Combine(baseDir, "Wizionic.dll");
				if (File.Exists(dll))
					return $"{processPath} {Path.GetFullPath(dll)}";
			}

			return Path.GetFullPath(processPath);
		}

		var dllOnly = Path.Combine(baseDir, "Wizionic.dll");
		return File.Exists(dllOnly) ? Path.GetFullPath(dllOnly) : null;
	}

	private static void TrySetToplevelIcon(Gtk.Window window, string iconPath)
	{
		try
		{
			var texture = Gdk.Texture.NewFromFilename(iconPath);
			if (texture is null)
				return;

			// Keep the texture rooted for the lifetime of the process so the
			// compositor can keep reading the pixel buffer.
			GC.KeepAlive(texture);
			_iconTextureRoot = texture;

			void ApplyToSurface()
			{
				try
				{
					// Gtk.Native.GetSurface() — available after realize.
					var surface = ((Gtk.Native)window).GetSurface();
					if (surface is not Gdk.Toplevel toplevel)
						return;

					// gdk_toplevel_set_icon_list takes a GList of GdkTexture*.
					var list = GLib.List.Append(new GLib.List(), texture.Handle.DangerousGetHandle());
					toplevel.SetIconList(list);
					Console.WriteLine("[Desktop] Gdk.Toplevel icon list set");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[Desktop] toplevel icon list failed: {ex.Message}");
				}
			}

			if (window.GetRealized())
				ApplyToSurface();
			else
				window.OnRealize += (_, _) => ApplyToSurface();
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] texture icon failed: {ex.Message}");
		}
	}

	// Root the texture so the GC does not free compositor-facing pixels.
	private static Gdk.Texture? _iconTextureRoot;

	private static void CopyIfNewer(string source, string dest)
	{
		try
		{
			if (File.Exists(dest))
			{
				var srcInfo = new FileInfo(source);
				var dstInfo = new FileInfo(dest);
				if (dstInfo.Length == srcInfo.Length && dstInfo.LastWriteTimeUtc >= srcInfo.LastWriteTimeUtc)
					return;
			}

			File.Copy(source, dest, overwrite: true);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] copy icon {dest} failed: {ex.Message}");
		}
	}
}
#endif
