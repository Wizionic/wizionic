namespace ChatfishApp.Core.Storage;

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

    string GetKey(string providerId);
    Task SetKeyAsync(string providerId, string key, CancellationToken ct = default);
    Task SaveAllKeysAsync(string groq, string gemini, string openrouter, CancellationToken ct = default);

    string OllamaBaseUrl { get; }
    string OllamaChatEndpoint { get; }
    List<string> OllamaModels { get; }
    IReadOnlyList<OllamaModelSettings> OllamaModelSettingsList { get; }
    OllamaModelSettings? GetOllamaModelSettings(string modelName);
    string? GetVisionProxyModelName();
    OllamaModelSettings GetOrCreateOllamaModelSettings(string modelName);
    Task SetOllamaBaseUrlAsync(string baseUrl, CancellationToken ct = default);
    Task SetOllamaModelsAsync(List<string> models, CancellationToken ct = default);
    Task AddOllamaModelAsync(string model, HttpClient? http = null, CancellationToken ct = default);
    Task RemoveOllamaModelAsync(string model, CancellationToken ct = default);
    Task SaveOllamaModelSettingsAsync(OllamaModelSettings settings, CancellationToken ct = default);
    Task RefreshOllamaModelsFromServerAsync(HttpClient http, string? baseUrl = null, CancellationToken ct = default);

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

    string HomeAssistantBaseUrl { get; }
    string HomeAssistantToken { get; }
    Task SetHomeAssistantConfigAsync(string baseUrl, string token, CancellationToken ct = default);
}