namespace App.Core.Connectors;

/// <summary>How a connector is backed (remote MCP vs OAuth OpenAPI REST).</summary>
public enum ConnectorKind
{
    Mcp = 0,
    OpenApiOAuth = 1
}

/// <summary>OAuth access/refresh tokens for an installed OpenAPI connector.</summary>
public sealed record OAuthTokenSet(
    string AccessToken,
    string? RefreshToken = null,
    DateTimeOffset? ExpiresAtUtc = null,
    string? TokenType = null,
    string? Scope = null,
    string? AccountLabel = null);

/// <summary>
/// User install state for an OAuth OpenAPI connector.
/// Tokens are held in memory decrypted; persistence may encrypt at rest.
/// </summary>
public sealed record OAuthConnectorInstall(
    string ConnectorId,
    bool Enabled,
    OAuthTokenSet? Tokens = null,
    DateTimeOffset? ConnectedAtUtc = null,
    string? AccountLabel = null);

/// <summary>Static catalog entry for Featured / marketplace UI.</summary>
public sealed record ConnectorCatalogEntry(
    string Id,
    string DisplayName,
    string Description,
    ConnectorKind Kind,
    string IconKey,
    bool Featured = true,
    IReadOnlyList<string>? Scopes = null,
    string? DocsUrl = null,
    /// <summary>OAuth broker provider id (e.g. "google", "github"). Null for MCP.</summary>
    string? OAuthProviderId = null,
    /// <summary>MCP registry server name when Kind is Mcp.</summary>
    string? McpRegistryName = null);

/// <summary>One curated REST operation exposed as an AI tool (OpenAPI-shaped).</summary>
public sealed class CuratedConnectorOperation
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Method { get; set; } = "GET";
    public string UrlTemplate { get; set; } = "";
    public List<CuratedOpParameter> Parameters { get; set; } = new();
    public string? RequestBodyDescription { get; set; }
    public bool RequestBodyJson { get; set; }
}

public sealed class CuratedOpParameter
{
    public string Name { get; set; } = "";
    public string In { get; set; } = "query"; // query | path | header
    public bool Required { get; set; }
    public string? Description { get; set; }
    public string Type { get; set; } = "string";
}

/// <summary>Curated tool pack for one connector id.</summary>
public sealed class CuratedConnectorSpec
{
    public string ConnectorId { get; set; } = "";
    public List<CuratedConnectorOperation> Operations { get; set; } = new();
}
