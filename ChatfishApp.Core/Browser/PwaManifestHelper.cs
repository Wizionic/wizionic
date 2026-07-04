namespace ChatfishApp.Core.Browser;

public static class PwaManifestHelper
{
    public static string ResolveStartUrl(PwaManifest manifest, string pageUrl)
    {
        if (!string.IsNullOrWhiteSpace(manifest.StartUrl))
            return ResolveUrl(manifest.SourceUrl, manifest.StartUrl);

        return pageUrl;
    }

    public static string? ResolveOptionalStartUrl(string? startUrl, string manifestUrl)
    {
        if (string.IsNullOrWhiteSpace(startUrl))
            return null;

        return ResolveUrl(manifestUrl, startUrl);
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

    public static string ResolveUrl(string baseUrl, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return relative;

        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
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
}