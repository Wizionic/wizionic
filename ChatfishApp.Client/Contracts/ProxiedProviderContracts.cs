using System.Text.Json.Serialization;

namespace ChatfishApp.Contracts;

/// <summary>
/// DTOs shared between the server AI proxy endpoints and the WASM client.
/// </summary>
public static class ProxiedProviderContracts
{
    public sealed class ProxiedProviderDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "OpenAICompatible";

        [JsonPropertyName("models")]
        public List<ProxiedModelDto> Models { get; set; } = new();
    }

    public sealed class ProxiedModelDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "🤖";

        [JsonPropertyName("supportsTools")]
        public bool SupportsTools { get; set; } = true;

        [JsonPropertyName("supportsVision")]
        public bool SupportsVision { get; set; }
    }

    public sealed record ProxyChatRequest(
        string ProviderId,
        string Model,
        List<Dictionary<string, object?>> Messages,
        List<Dictionary<string, object?>>? Tools = null,
        object? ToolChoice = null);

    public sealed class ProxyProvidersResponse
    {
        [JsonPropertyName("providers")]
        public List<ProxiedProviderDto> Providers { get; set; } = new();
    }
}