using App.Core.Connectors;
using App.Core.Tools;

namespace App.Core.Storage;

public interface IKeyStore
{
    Task LoadAsync(CancellationToken ct = default);

    string LastSelectedModel { get; }
    bool IsSystemPromptCustomized { get; }
    string GetSystemPrompt();
    Task SetSystemPromptAsync(string prompt, CancellationToken ct = default);
    Task ResetSystemPromptAsync(CancellationToken ct = default);

    UserProfileSettings GetUserProfile();
    Task SetUserProfileAsync(UserProfileSettings profile, CancellationToken ct = default);
    IReadOnlyList<UserMemory> GetMemories();
    Task<UserMemory> AddMemoryAsync(string text, CancellationToken ct = default);
    Task UpdateMemoryAsync(string id, string text, CancellationToken ct = default);
    Task RemoveMemoryAsync(string id, CancellationToken ct = default);
    Task ClearMemoriesAsync(CancellationToken ct = default);
    string BuildUserContextForPrompt();

    Task SetLastSelectedModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Max new tokens for a chat reply (thinking + visible text). Default 16384.
    /// Not a model context-window setting.
    /// </summary>
    int MaxOutputTokens { get; }
    Task SetMaxOutputTokensAsync(int value, CancellationToken ct = default);

    /// <summary>How chat turns choose tool modules (Rules / Ai / Hybrid).</summary>
    ToolRoutingMode ToolRoutingMode { get; }
    /// <summary>Catalog model id for AI routing (e.g. lemonade/Qwen…); empty = Rules only.</summary>
    string? ToolRoutingModelId { get; }
    Task SetToolRoutingAsync(ToolRoutingMode mode, string? modelId, CancellationToken ct = default);

    /// <summary>Chat-eligible model used to answer Ask in Help. Empty = browse only.</summary>
    string? HelpAnswerModelId { get; }
    /// <summary>Optional embeddings model used to build the local help vector index.</summary>
    string? HelpEmbedModelId { get; }
    Task SetHelpModelsAsync(string? answerModelId, string? embedModelId, CancellationToken ct = default);

    IReadOnlyList<ModelProfile> ModelProfiles { get; }
    string? ActiveModelProfileId { get; }
    ModelProfile? GetModelProfile(string id);
    Task UpsertModelProfileAsync(ModelProfile profile, CancellationToken ct = default);
    Task RemoveModelProfileAsync(string id, CancellationToken ct = default);
    Task SetActiveModelProfileIdAsync(string? id, CancellationToken ct = default);
    Task ReplaceModelProfilesAsync(IEnumerable<ModelProfile> profiles, string? activeId, CancellationToken ct = default);

    IReadOnlyList<CloudProviderConfig> CloudProviders { get; }
    CloudProviderConfig? GetCloudProvider(string id);
    Task UpsertCloudProviderAsync(CloudProviderConfig provider, CancellationToken ct = default);
    Task RemoveCloudProviderAsync(string id, CancellationToken ct = default);
    Task ReplaceCloudProvidersAsync(IEnumerable<CloudProviderConfig> providers, CancellationToken ct = default);
    Task SaveCloudModelSettingsAsync(string providerId, CloudModelSettings settings, CancellationToken ct = default);
    Task SetCloudModalityDefaultsAsync(
        string providerId,
        string? imageModel = null,
        string? editModel = null,
        string? ttsModel = null,
        string? sttModel = null,
        string? voice = null,
        CancellationToken ct = default);
    Task RefreshCloudProviderModelsAsync(string providerId, HttpClient http, CancellationToken ct = default);

    string OllamaBaseUrl { get; }
    string OllamaChatEndpoint { get; }
    List<string> OllamaModels { get; }
    IReadOnlyList<OllamaModelSettings> OllamaModelSettingsList { get; }
    OllamaModelSettings? GetOllamaModelSettings(string modelName);
    /// <summary>
    /// Active vision-proxy model id, fully qualified as <c>ollama/{name}</c> or <c>lemonade/{name}</c>,
    /// or null when none is configured.
    /// </summary>
    string? GetVisionProxyModelName();
    OllamaModelSettings GetOrCreateOllamaModelSettings(string modelName);
    Task SetOllamaBaseUrlAsync(string baseUrl, CancellationToken ct = default);
    Task SetOllamaModelsAsync(List<string> models, CancellationToken ct = default);
    Task AddOllamaModelAsync(string model, HttpClient? http = null, CancellationToken ct = default);
    Task RemoveOllamaModelAsync(string model, CancellationToken ct = default);
    Task SaveOllamaModelSettingsAsync(OllamaModelSettings settings, CancellationToken ct = default);
    Task RefreshOllamaModelsFromServerAsync(HttpClient http, string? baseUrl = null, CancellationToken ct = default);

    // --- Lemonade (parallel local AI server; independent of Ollama) ---
    string LemonadeBaseUrl { get; }
    string? LemonadeApiKey { get; }
    string LemonadeChatEndpoint { get; }
    List<string> LemonadeModels { get; }
    IReadOnlyList<LemonadeModelSettings> LemonadeModelSettingsList { get; }
    LemonadeModelSettings? GetLemonadeModelSettings(string modelName);
    LemonadeModelSettings GetOrCreateLemonadeModelSettings(string modelName);
    string? LemonadeDefaultImageModel { get; }
    string? LemonadeDefaultEditModel { get; }
    string? LemonadeDefaultTtsModel { get; }
    string? LemonadeDefaultSttModel { get; }
    string? LemonadeDefaultVoice { get; }
    Task SetLemonadeBaseUrlAsync(string baseUrl, CancellationToken ct = default);
    Task SetLemonadeApiKeyAsync(string? apiKey, CancellationToken ct = default);
    Task SetLemonadeModalityDefaultsAsync(
        string? imageModel = null,
        string? editModel = null,
        string? ttsModel = null,
        string? sttModel = null,
        string? voice = null,
        CancellationToken ct = default);
    Task AddLemonadeModelAsync(string model, CancellationToken ct = default);
    Task RemoveLemonadeModelAsync(string model, CancellationToken ct = default);
    Task SaveLemonadeModelSettingsAsync(LemonadeModelSettings settings, CancellationToken ct = default);
    Task RefreshLemonadeModelsFromServerAsync(HttpClient http, string? baseUrl = null, string? apiKey = null, CancellationToken ct = default);

    IReadOnlySet<string> EnabledMcpServerNames { get; }
    bool IsMcpServerEnabled(string name);
    string GetMcpToken(string serverName);
    IReadOnlyDictionary<string, string> GetAllMcpTokens();
    IReadOnlyList<CustomMcpConnector> GetCustomConnectors();
    Task SetMcpServerEnabledAsync(string name, bool enabled, CancellationToken ct = default);
    Task SetEnabledMcpServersAsync(IEnumerable<string> names, CancellationToken ct = default);
    Task SetMcpTokenAsync(string serverName, string token, CancellationToken ct = default);
    Task AddCustomConnectorAsync(string name, string serverUrl, CancellationToken ct = default);
    Task RemoveCustomConnectorAsync(string name, CancellationToken ct = default);

    // --- OAuth OpenAPI connectors ---
    IReadOnlyList<OAuthConnectorInstall> GetOAuthConnectors();
    OAuthConnectorInstall? GetOAuthConnector(string connectorId);
    OAuthTokenSet? GetOAuthTokens(string connectorId);
    Task UpsertOAuthConnectorAsync(OAuthConnectorInstall install, CancellationToken ct = default);
    Task SetOAuthConnectorEnabledAsync(string connectorId, bool enabled, CancellationToken ct = default);
    Task RemoveOAuthConnectorAsync(string connectorId, CancellationToken ct = default);
    /// <summary>Replace the full OAuth connector list (used by settings sync apply).</summary>
    Task ReplaceOAuthConnectorsAsync(IEnumerable<OAuthConnectorInstall> installs, CancellationToken ct = default);

    string HomeAssistantBaseUrl { get; }
    string HomeAssistantToken { get; }
    /// <summary>Same as <see cref="AssistantName"/>; kept for existing HA routing call sites.</summary>
    string HomeAssistantAssistantName { get; }
    string HomeAssistantDeviceSummary { get; }
    DateTime? HomeAssistantDeviceSummaryUpdatedAt { get; }
    Task SetHomeAssistantConfigAsync(string baseUrl, string token, string assistantName, CancellationToken ct = default);
    Task UpdateHomeAssistantDeviceSummaryAsync(string summary, CancellationToken ct = default);

    /// <summary>Voice-mode wake word and HA chat address. Independent of Home Assistant install.</summary>
    string AssistantName { get; }
    Task SetAssistantNameAsync(string name, CancellationToken ct = default);
}
