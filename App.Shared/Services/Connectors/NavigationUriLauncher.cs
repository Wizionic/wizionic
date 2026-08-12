using App.Core.Connectors;
using Microsoft.AspNetCore.Components;

namespace App.Shared.Services.Connectors;

/// <summary>WASM / in-browser: full page navigation (forceLoad) for OAuth.</summary>
public sealed class NavigationUriLauncher : IUriLauncher
{
    private readonly NavigationManager _nav;

    public NavigationUriLauncher(NavigationManager nav) => _nav = nav;

    public Task OpenAsync(string uri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return Task.CompletedTask;

        // Absolute or relative — forceLoad leaves the Blazor SPA for the host OAuth endpoints.
        _nav.NavigateTo(uri, forceLoad: true);
        return Task.CompletedTask;
    }
}
