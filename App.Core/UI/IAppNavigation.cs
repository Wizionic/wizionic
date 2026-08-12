namespace App.Core.UI;

/// <summary>
/// App-wide navigation (singleton-safe). Blazor <c>NavigationManager</c> is attached
/// once by a root bootstrap component so background services (e.g. OAuth) can route.
/// </summary>
public interface IAppNavigation
{
    /// <summary>Current absolute URI, or empty if not attached.</summary>
    string Uri { get; }

    bool IsAttached { get; }

    void NavigateTo(string uri, bool forceLoad = false, bool replace = false);

    bool IsPath(string path);
}
