namespace App.Core.Storage;

/// <summary>
/// Refreshes the MCP tool cache after the user changes Tools page selections (WASM only for now).
/// </summary>
public interface IMcpToolRefresher
{
    Task RefreshFromKeyStoreAsync(CancellationToken ct = default);
}