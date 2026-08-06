namespace App.Maui.Services;

/// <summary>
/// Resolves a writable app data directory on all targets.
/// On Linux desktop (plain net10.0) MAUI Essentials <see cref="FileSystem"/> is a portable stub;
/// use XDG LocalApplicationData instead.
/// </summary>
internal static class MauiAppData
{
	public static string Directory
	{
		get
		{
#if WINDOWS || ANDROID || IOS || MACCATALYST
			return FileSystem.AppDataDirectory;
#else
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Wizionic");
			System.IO.Directory.CreateDirectory(dir);
			return dir;
#endif
		}
	}
}
