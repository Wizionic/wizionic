namespace App.Core.SmartHome;

/// <summary>
/// Platform-specific smart home integration (e.g. Home Assistant on MAUI).
/// </summary>
public interface ISmartHomeService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Test credentials without requiring them to be saved first.
    /// </summary>
    Task<string> TestConnectionAsync(string baseUrl, string token, CancellationToken ct = default);

    Task<string> CallServiceAsync(
        string domain,
        string service,
        object serviceData,
        CancellationToken ct = default);

    Task<string> GetEntityStateAsync(string entityId, CancellationToken ct = default);

    /// <summary>
    /// List entities from GET /api/states, optionally filtered by domain and/or search text
    /// (matches entity_id or friendly_name). When domain is null, returns controllable domains only.
    /// </summary>
    Task<string> ListEntitiesAsync(string? domain = null, string? search = null, CancellationToken ct = default);

    /// <summary>
    /// Compact multi-domain catalog for settings cache / system prompt (grouped by domain).
    /// </summary>
    Task<string> BuildDeviceCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// List available services from GET /api/services, optionally filtered by domain.
    /// </summary>
    Task<string> ListServicesAsync(string? domain = null, CancellationToken ct = default);

    /// <summary>
    /// Process natural language via HA Assist (POST /api/conversation/process).
    /// Secondary path; prefer ListEntities + CallService for precise control.
    /// </summary>
    Task<string> ProcessConversationAsync(string text, string? conversationId = null, CancellationToken ct = default);

    /// <summary>
    /// Returns a human-readable list of light entities (entity_id + friendly name).
    /// Prefer <see cref="ListEntitiesAsync"/> with domain "light" for new code.
    /// </summary>
    Task<string> ListLightEntitiesAsync(CancellationToken ct = default);
}
