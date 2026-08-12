namespace App.Core.Connectors;

/// <summary>
/// Rebuilds AI tools for enabled OAuth OpenAPI connectors after Tools page or sync changes.
/// </summary>
public interface IOpenApiConnectorRefresher
{
    Task RefreshFromKeyStoreAsync(CancellationToken ct = default);
}
