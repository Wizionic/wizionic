namespace App.Core.Browser;

public static class PwaManifestHelper
{
    public static string ResolveStartUrl(PwaManifest manifest, string pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(manifest.StartUrl))
        {
            var baseUrl = !string.IsNullOrWhiteSpace(manifest.SourceUrl)
                ? manifest.SourceUrl
                : pageUrl;
            var resolved = ResolveUrl(baseUrl, manifest.StartUrl);
            if (IsWebUrl(resolved))
                return resolved;
        }

        return pageUrl;
    }

    public static string? ResolveOptionalStartUrl(string? startUrl, string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(startUrl))
            return null;

        var resolved = ResolveUrl(manifestUrl, startUrl);
        return IsWebUrl(resolved) ? resolved : null;
    }

    public static string? PickBestIcon(PwaManifest manifest)
    {
        if (manifest.Icons.Count == 0)
            return null;

        static int SizeScore(string? sizes)
        {
            if (string.IsNullOrWhiteSpace(sizes))
                return 0;
            return sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Split('x')[0], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();
        }

        return manifest.Icons
            .OrderByDescending(i => SizeScore(i.Sizes))
            .First()
            .Src;
    }

    public static OpenTarget SuggestOpenTarget(PwaManifest manifest)
    {
        var display = manifest.Display?.Trim().ToLowerInvariant();
        return display is "standalone" or "fullscreen"
            ? OpenTarget.MainBrowser
            : OpenTarget.SidePanel;
    }

    /// <summary>
    /// Resolve a manifest-relative URL (start_url, icon src, etc.) against a base URL.
    /// </summary>
    /// <remarks>
    /// Must not use <c>Uri.TryCreate(relative, UriKind.Absolute)</c> alone for paths starting
    /// with <c>/</c>: on Unix, .NET treats those as absolute file URIs (<c>file:///</c>),
    /// which breaks PWA start_url values like <c>"/"</c>.
    /// </remarks>
    public static string ResolveUrl(string baseUrl, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return relative;

        relative = relative.Trim();

        // Explicit schemes only (http:, https:, data:, …). Root-relative paths like "/"
        // and scheme-relative "//host/path" must resolve against the manifest/page base.
        if (HasExplicitScheme(relative))
        {
            if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
                return absolute.ToString();
            return relative;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || !IsWebScheme(baseUri.Scheme))
            return relative;

        try
        {
            // Manifest members (start_url, icons) resolve relative to the manifest URL.
            return new Uri(baseUri, relative).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return relative;
        }
    }

    /// <summary>
    /// Fix apps pinned before the Linux file:/// start_url bug was fixed.
    /// Uses a valid icon/page origin when start_url was stored as file:///.
    /// </summary>
    public static SidebarApp HealPinnedApp(SidebarApp app)
    {
        var startUrl = app.StartUrl;
        var iconUrl = app.IconUrl;

        if (IsBrokenFileUrl(startUrl))
        {
            if (TryGetWebOrigin(iconUrl, out var origin))
                startUrl = origin;
        }

        if (IsBrokenFileUrl(iconUrl) && IsWebUrl(startUrl))
        {
            // file:///apple-touch-icon.png → https://origin/apple-touch-icon.png
            try
            {
                var path = new Uri(iconUrl!).AbsolutePath;
                if (!string.IsNullOrEmpty(path) && path != "/")
                    iconUrl = new Uri(new Uri(startUrl), path).AbsoluteUri;
            }
            catch (UriFormatException)
            {
                // leave icon as-is
            }
        }

        if (startUrl == app.StartUrl && iconUrl == app.IconUrl)
            return app;

        return app with { StartUrl = startUrl, IconUrl = iconUrl };
    }

    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWebScheme(uri.Scheme);

    private static bool IsWebScheme(string scheme) =>
        scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool IsBrokenFileUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetWebOrigin(string? url, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsWebScheme(uri.Scheme))
            return false;

        origin = $"{uri.Scheme}://{uri.Authority}/";
        return true;
    }

    /// <summary>
    /// True when <paramref name="value"/> begins with an RFC-style scheme (letters + ':').
    /// Root-relative ("/x") and scheme-relative ("//host") return false.
    /// </summary>
    private static bool HasExplicitScheme(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
            return false;

        var colon = value.IndexOf(':');
        if (colon <= 0)
            return false;

        for (var i = 0; i < colon; i++)
        {
            if (!char.IsAsciiLetter(value[i]))
                return false;
        }

        return true;
    }
}
