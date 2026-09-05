namespace App.Maui.Services;

/// <summary>
/// Resolves a writable app data directory on all targets.
/// Windows unpackaged must not use <see cref="FileSystem.AppDataDirectory"/>: that path
/// includes PublisherDisplayName and Identity Name, so a Store-manifest change moves
/// SQLite and looks like a logout / empty library. Velopack install root is
/// %LocalAppData%\Wizionic; userdata lives in a sibling folder that Velopack does not replace.
/// </summary>
internal static class MauiAppData
{
	private const string DbFileName = "wizionic_local.db";
	private static readonly object Gate = new();
	private static string? _directory;

	public static string Directory
	{
		get
		{
			if (_directory is not null)
				return _directory;
			lock (Gate)
			{
				if (_directory is not null)
					return _directory;
				_directory = Resolve();
				return _directory;
			}
		}
	}

	private static string Resolve()
	{
#if WINDOWS
		if (App.Maui.WindowsPackageInfo.IsPackaged)
		{
			var packaged = FileSystem.AppDataDirectory;
			System.IO.Directory.CreateDirectory(packaged);
			return packaged;
		}

		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var stable = Path.Combine(local, "Wizionic", "userdata");
		System.IO.Directory.CreateDirectory(stable);
		TryMigrateUnpackagedWindows(stable);
		return stable;
#elif ANDROID || IOS || MACCATALYST
		return FileSystem.AppDataDirectory;
#else
		var dir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Wizionic");
		System.IO.Directory.CreateDirectory(dir);
		return dir;
#endif
	}

#if WINDOWS
	private static void TryMigrateUnpackagedWindows(string targetDir)
	{
		try
		{
			var targetDb = Path.Combine(targetDir, DbFileName);
			var targetLen = System.IO.File.Exists(targetDb) ? new FileInfo(targetDb).Length : 0L;

			string? bestDir = null;
			var bestLen = targetLen;
			foreach (var dir in UnpackagedLegacyDirectories())
			{
				if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
					continue;
				if (PathsEqual(dir, targetDir))
					continue;
				var db = Path.Combine(dir, DbFileName);
				if (!System.IO.File.Exists(db))
					continue;
				var len = new FileInfo(db).Length;
				if (len > bestLen)
				{
					bestLen = len;
					bestDir = dir;
				}
			}

			if (bestDir is null)
				return;

			Console.WriteLine($"[MauiAppData] Migrating {bestLen} bytes from {bestDir} -> {targetDir}");
			CopyDataFiles(bestDir, targetDir);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[MauiAppData] Migration skipped: {ex.Message}");
		}
	}

	private static List<string> UnpackagedLegacyDirectories()
	{
		var dirs = new List<string>();
		var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		string[] publishers = ["User Name", "Wizionic"];
		string[] packages = ["com.wizionic.app", "Wizionic.Wizionic", "Wizionic", "maui-package-name-placeholder"];
		foreach (var root in new[] { local, roaming })
		{
			foreach (var publisher in publishers)
			{
				foreach (var package in packages)
					dirs.Add(Path.Combine(root, publisher, package, "Data"));
			}
		}

		try
		{
			dirs.Add(FileSystem.AppDataDirectory);
		}
		catch
		{
			// FileSystem may throw before MAUI is fully initialized.
		}

		return dirs;
	}

	private static void CopyDataFiles(string sourceDir, string destDir)
	{
		System.IO.Directory.CreateDirectory(destDir);
		foreach (var file in System.IO.Directory.GetFiles(sourceDir))
		{
			var name = Path.GetFileName(file);
			var dest = Path.Combine(destDir, name);
			if (System.IO.File.Exists(dest) && new FileInfo(dest).Length >= new FileInfo(file).Length)
				continue;
			System.IO.File.Copy(file, dest, overwrite: true);
		}
	}

	private static bool PathsEqual(string a, string b)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
				StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
		}
	}
#endif
}
