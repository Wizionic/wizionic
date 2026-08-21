namespace App.Maui;

internal static class WindowsIconPath
{
    public static string? Resolve()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "appicon.ico"),
            Path.Combine(baseDir, "Resources", "AppIcon", "appicon.ico"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Resources", "AppIcon", "appicon.ico")),
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                    return path;
            }
            catch
            {
                // skip
            }
        }

        return null;
    }
}
