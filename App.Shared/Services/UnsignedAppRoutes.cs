using Microsoft.AspNetCore.Components;

namespace App.Shared.Services;

/// <summary>
/// Paths that may render without a signed-in session. Everything else is login-only.
/// </summary>
public static class UnsignedAppRoutes
{
    public static string LoginPath => AppEnvironment.IsMaui ? "/login" : "/";

    public static bool IsAllowed(NavigationManager nav)
    {
        string path;
        try
        {
            path = nav.ToBaseRelativePath(nav.Uri);
        }
        catch
        {
            return false;
        }

        return IsAllowedPath(path);
    }

    public static bool IsAllowedPath(string relativeUri)
    {
        var path = relativeUri ?? "";
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0)
            path = path[..q];
        path = path.Trim('/');

        if (string.IsNullOrEmpty(path) && !AppEnvironment.IsMaui)
            return true;

        return path.Equals("account", StringComparison.OrdinalIgnoreCase)
            || path.Equals("login", StringComparison.OrdinalIgnoreCase);
    }
}
