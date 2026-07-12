using System.Collections.Concurrent;

namespace ChatfishApp.Core.Browser;

/// <summary>
/// Resolves favicon URLs for bookmark UI.
/// Prefer page-captured icons (see cache); third-party CDNs often return a generic globe
/// with HTTP 200, so they never trigger img onerror.
/// </summary>
public static class BrowserFaviconUrl
{
    private static readonly ConcurrentDictionary<string, string> HostIconCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember a favicon discovered while browsing (host → absolute or data URL).</summary>
    public static void Remember(string pageUrl, string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return;
        if (!TryGetHost(pageUrl, out var host))
            return;
        if (!IsUsableIconUrl(iconUrl))
            return;

        HostIconCache[host] = iconUrl.Trim();
    }

    public static string? GetCached(string pageUrl)
    {
        if (!TryGetHost(pageUrl, out var host))
            return null;
        return HostIconCache.TryGetValue(host, out var icon) ? icon : null;
    }

    /// <summary>
    /// Best URL for an &lt;img src&gt;. Prefer bookmark-stored / cached page icon, then site paths.
    /// Avoid Google as sole source — it often returns a blank generic icon with status 200.
    /// </summary>
    public static string Get(string url, string? storedIconUrl = null)
    {
        if (IsUsableIconUrl(storedIconUrl))
            return storedIconUrl!.Trim();

        var cached = GetCached(url);
        if (IsUsableIconUrl(cached))
            return cached!;

        // Prefer the site's own assets (same origin as Edge would use after navigation).
        var siteIcon = GetSiteIcon32(url);
        if (!string.IsNullOrEmpty(siteIcon))
            return siteIcon;

        var rootIco = GetSiteRootIco(url);
        if (!string.IsNullOrEmpty(rootIco))
            return rootIco;

        return GetDuckDuckGo(url);
    }

    public static string GetGoogle(string url)
    {
        if (!TryGetHost(url, out var host))
            return "";
        return $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=32";
    }

    public static string GetDuckDuckGo(string url)
    {
        if (!TryGetHost(url, out var host))
            return "";
        return $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(host)}.ico";
    }

    public static string GetSiteRootIco(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return "";
        return $"{uri.Scheme}://{uri.Authority}/favicon.ico";
    }

    public static string GetSiteIcon32(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return "";
        return $"{uri.Scheme}://{uri.Authority}/images/icon32.png";
    }

    public static bool IsUsableIconUrl(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl))
            return false;
        var u = iconUrl.Trim();
        if (u.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;
        return Uri.TryCreate(u, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool TryGetHost(string url, out string host)
    {
        host = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        host = uri.Host;
        return true;
    }
}
