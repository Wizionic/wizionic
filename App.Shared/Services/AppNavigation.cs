using App.Core.UI;
using Microsoft.AspNetCore.Components;

namespace App.Shared.Services;

/// <summary>Holds the live Blazor <see cref="NavigationManager"/> for singleton services.</summary>
public sealed class AppNavigation : IAppNavigation
{
    private NavigationManager? _nav;

    public bool IsAttached => _nav is not null;

    public string Uri => _nav?.Uri ?? "";

    public void Attach(NavigationManager nav)
    {
        _nav = nav ?? throw new ArgumentNullException(nameof(nav));
    }

    public void Detach(NavigationManager nav)
    {
        if (ReferenceEquals(_nav, nav))
            _nav = null;
    }

    public void NavigateTo(string uri, bool forceLoad = false, bool replace = false)
    {
        if (_nav is null || string.IsNullOrWhiteSpace(uri))
            return;
        _nav.NavigateTo(uri, forceLoad, replace);
    }

    public bool IsPath(string path)
    {
        if (_nav is null || string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            var current = _nav.ToAbsoluteUri(_nav.Uri).AbsolutePath.TrimEnd('/');
            var want = path.TrimEnd('/');
            if (string.IsNullOrEmpty(want))
                want = "/";
            if (string.IsNullOrEmpty(current))
                current = "/";
            return current.Equals(want, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
