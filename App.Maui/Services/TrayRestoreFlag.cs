namespace App.Maui.Services;

/// <summary>
/// Survives Velopack / login-server restart so a hidden window returns to the tray.
/// Consumed once on the next process start.
/// </summary>
internal static class TrayRestoreFlag
{
    public const string FileName = "tray-restore.flag";

    public static string FilePath => Path.Combine(MauiAppData.Directory, FileName);

    public static void WriteHidden()
    {
        try
        {
            Directory.CreateDirectory(MauiAppData.Directory);
            File.WriteAllText(FilePath, "hidden");
            Console.WriteLine("[Desktop] wrote tray-restore.flag=hidden");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] tray-restore.flag write failed: {ex.Message}");
        }
    }

    /// <returns>true if the previous process asked to come back hidden.</returns>
    public static bool ConsumeHidden()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
                return false;

            var text = File.ReadAllText(path).Trim();
            try { File.Delete(path); }
            catch { /* next launch will retry */ }

            var hidden = string.Equals(text, "hidden", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"[Desktop] consumed tray-restore.flag hidden={hidden}");
            return hidden;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Desktop] tray-restore.flag read failed: {ex.Message}");
            return false;
        }
    }
}
