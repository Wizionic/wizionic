namespace ChatfishApp.Core.Browser;

public static class BrowserFaviconUrl
{
    public static string Get(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host))
            return "";

        return $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(uri.Host)}&sz=16";
    }
}