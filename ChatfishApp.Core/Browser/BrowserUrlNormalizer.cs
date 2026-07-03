namespace ChatfishApp.Core.Browser;

public static class BrowserUrlNormalizer
{
    public static string Normalize(string input, BrowserSettings settings)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "about:blank";

        input = input.Trim();

        if (input.Equals("https://", StringComparison.OrdinalIgnoreCase)
            || input.Equals("http://", StringComparison.OrdinalIgnoreCase))
            return "";

        if (input.Contains(' ') || input.Contains('\t'))
            return BuildSearchUrl(input, settings);

        if (input.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return "about:blank";

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return input;

        if (input.Contains("://", StringComparison.Ordinal))
            return BuildSearchUrl(input, settings);

        if (IsLocalOrIpHost(input))
        {
            var local = "https://" + input;
            if (Uri.TryCreate(local, UriKind.Absolute, out _))
                return local;
        }

        if (!input.Contains(' ') && input.Contains('.'))
            return $"https://{input}";

        return BuildSearchUrl(input, settings);
    }

    public static string BuildSearchUrl(string query, BrowserSettings settings)
    {
        var encoded = Uri.EscapeDataString(query);
        var template = GetSearchTemplate(settings);
        if (template.Contains("{query}", StringComparison.Ordinal))
            return template.Replace("{query}", encoded, StringComparison.Ordinal);

        return template + encoded;
    }

    public static string GetSearchLandingUrl(BrowserSettings settings) =>
        settings.SearchEngine switch
        {
            BrowserSearchEngineKind.DuckDuckGo => "https://duckduckgo.com/",
            BrowserSearchEngineKind.Bing => "https://www.bing.com/",
            BrowserSearchEngineKind.Google => "https://www.google.com/",
            BrowserSearchEngineKind.Custom when !string.IsNullOrWhiteSpace(settings.CustomSearchUrl) =>
                StripQueryFromSearchTemplate(settings.CustomSearchUrl),
            _ => "https://search.brave.com/"
        };

    public static string? ResolveHomeTarget(BrowserSettings settings)
    {
        return settings.Homepage switch
        {
            BrowserHomepageKind.Bookmarks => null,
            BrowserHomepageKind.CustomUrl when !string.IsNullOrWhiteSpace(settings.CustomHomepageUrl) =>
                Normalize(settings.CustomHomepageUrl, settings),
            _ => GetSearchLandingUrl(settings)
        };
    }

    private static string GetSearchTemplate(BrowserSettings settings)
    {
        if (settings.SearchEngine == BrowserSearchEngineKind.Custom
            && !string.IsNullOrWhiteSpace(settings.CustomSearchUrl))
            return settings.CustomSearchUrl.Trim();

        return settings.SearchEngine switch
        {
            BrowserSearchEngineKind.DuckDuckGo => "https://duckduckgo.com/?q=",
            BrowserSearchEngineKind.Bing => "https://www.bing.com/search?q=",
            BrowserSearchEngineKind.Google => "https://www.google.com/search?q=",
            _ => "https://search.brave.com/search?q="
        };
    }

    private static string StripQueryFromSearchTemplate(string template)
    {
        var trimmed = template.Trim();
        var queryIndex = trimmed.IndexOf("{query}", StringComparison.Ordinal);
        if (queryIndex >= 0)
            return trimmed[..queryIndex];

        var lastEquals = trimmed.LastIndexOf('=');
        if (lastEquals > trimmed.IndexOf("://", StringComparison.Ordinal))
            return trimmed[..(lastEquals + 1)];

        return trimmed;
    }

    private static bool IsLocalOrIpHost(string input)
    {
        var host = input.Split('/')[0].Split(':')[0];
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return System.Net.IPAddress.TryParse(host, out _);
    }
}