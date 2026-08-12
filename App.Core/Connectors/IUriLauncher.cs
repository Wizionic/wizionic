namespace App.Core.Connectors;

/// <summary>
/// Opens a URI outside the Blazor router (system browser / external navigation).
/// Used for OAuth start so MAUI does not treat /api/... as an in-app route.
/// </summary>
public interface IUriLauncher
{
    Task OpenAsync(string uri, CancellationToken ct = default);
}
