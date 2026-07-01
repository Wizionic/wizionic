namespace ChatfishApp.Core.SmartHome;

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
    /// Returns a human-readable list of light entities (entity_id + friendly name).
    /// </summary>
    Task<string> ListLightEntitiesAsync(CancellationToken ct = default);
}