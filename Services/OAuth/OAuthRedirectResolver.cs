namespace App.Services.OAuth;

/// <summary>
/// Picks which registered callback to send to the provider.
/// Production stays on the configured HTTPS URI; a local Home Server
/// (or <c>dotnet run</c>) uses loopback so the same OAuth app can list both.
/// </summary>
public static class OAuthRedirectResolver
{
    /// <summary>Home Server default and common local host ports.</summary>
    private static readonly int[] LoopbackPorts = [5150, 5136, 7156];

    public static string Resolve(string provider, string? requestOrigin, string configuredRedirectUri)
    {
        configuredRedirectUri = (configuredRedirectUri ?? "").Trim();
        if (string.IsNullOrWhiteSpace(provider))
            return configuredRedirectUri;

        var candidate = BuildCallback(requestOrigin, provider);
        if (candidate is not null && IsAllowed(candidate, provider, configuredRedirectUri))
            return candidate;

        return configuredRedirectUri;
    }

    public static string? BuildCallback(string? requestOrigin, string provider)
    {
        if (string.IsNullOrWhiteSpace(requestOrigin) || string.IsNullOrWhiteSpace(provider))
            return null;
        if (!Uri.TryCreate(requestOrigin.Trim().TrimEnd('/'), UriKind.Absolute, out var origin))
            return null;
        if (origin.Scheme != Uri.UriSchemeHttp && origin.Scheme != Uri.UriSchemeHttps)
            return null;

        return $"{origin.Scheme}://{origin.Authority}/api/oauth/{provider.Trim().ToLowerInvariant()}/callback";
    }

    public static bool IsAllowed(string redirectUri, string provider, string configuredRedirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
            return false;

        if (SameUri(redirectUri, configuredRedirectUri))
            return true;

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var expectedPath = $"/api/oauth/{provider.Trim().ToLowerInvariant()}/callback";
        if (!string.Equals(uri.AbsolutePath.TrimEnd('/'), expectedPath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsLoopbackHost(uri.Host))
            return false;

        var port = uri.IsDefaultPort
            ? (uri.Scheme == Uri.UriSchemeHttps ? 443 : 80)
            : uri.Port;
        return LoopbackPorts.Contains(port);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("[::1]", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool SameUri(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
}
