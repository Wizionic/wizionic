using ChatfishApp.Core.SmartHome;

namespace ChatfishApp.Shared.Services.Tools;

/// <summary>
/// No-op smart home service for platforms without native HA integration.
/// </summary>
public sealed class NullSmartHomeService : ISmartHomeService
{
    public bool IsConfigured => false;

    public Task<string> TestConnectionAsync(string baseUrl, string token, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> CallServiceAsync(string domain, string service, object serviceData, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> GetEntityStateAsync(string entityId, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> ListEntitiesAsync(string? domain = null, string? search = null, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> BuildDeviceCatalogAsync(CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> ListServicesAsync(string? domain = null, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> ProcessConversationAsync(string text, string? conversationId = null, CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");

    public Task<string> ListLightEntitiesAsync(CancellationToken ct = default) =>
        Task.FromResult("Smart home integration is not available on this platform.");
}
