using App.Maui.Services.Linux;

namespace App.Maui;

/// <summary>
/// XDG autostart for the desktop app. Distinct from the Home Server helper
/// (<c>~/.config/autostart/wizionic-homeserver.desktop</c>).
/// </summary>
internal static class LinuxAutostartRegistration
{
	public const string FileName = "com.wizionic.app.desktop";

	public static string FilePath
	{
		get
		{
			var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
			if (string.IsNullOrWhiteSpace(config))
			{
				config = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
					".config");
			}

			return Path.Combine(config, "autostart", FileName);
		}
	}

	public static void Apply(bool startWithSession, bool startMinimized)
	{
		if (!startWithSession)
		{
			Delete();
			return;
		}

		var exec = ResolveExec();
		if (string.IsNullOrEmpty(exec))
		{
			Console.WriteLine("[Desktop] autostart: no Exec path");
			return;
		}

		var execQuoted = QuoteDesktopExec(exec);
		if (startMinimized)
			execQuoted += " --start-minimized";

		var dir = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		var content =
			$"""
			[Desktop Entry]
			Type=Application
			Name={LinuxDesktopIcon.ApplicationName}
			Exec={execQuoted}
			Icon={LinuxDesktopIcon.ApplicationId}
			X-GNOME-Autostart-enabled=true
			StartupNotify=false
			Terminal=false
			Categories=Network;Office;Chat;

			""";

		File.WriteAllText(FilePath, content);
		try
		{
			File.SetUnixFileMode(
				FilePath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
				| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
				| UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		}
		catch
		{
			// chmod is best-effort
		}

		Console.WriteLine($"[Desktop] autostart = {execQuoted}");
	}

	public static void Delete()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				File.Delete(FilePath);
				Console.WriteLine("[Desktop] autostart file deleted");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[Desktop] autostart delete failed: {ex.Message}");
		}
	}

	private static string? ResolveExec()
	{
		var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
		if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage))
			return Path.GetFullPath(appImage);

		return LinuxDesktopIcon.ResolveExecPathPublic();
	}

	private static string QuoteDesktopExec(string exec)
	{
		if (exec.Contains('"', StringComparison.Ordinal))
			exec = exec.Replace("\"", "\\\"", StringComparison.Ordinal);

		return exec.Contains(' ', StringComparison.Ordinal) ? $"\"{exec}\"" : exec;
	}
}
