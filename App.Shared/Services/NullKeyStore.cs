using App.Core.Storage;
using App.Core.Tools;

namespace App.Shared.Services;

/// <summary>
/// No-op <see cref="IKeyStore"/> for the ASP.NET host / SSR pipeline.
/// Real settings live in WASM (localStorage) or MAUI (SQLite). The host only
/// needs this so shared layout components (e.g. SetupWizard) can resolve DI.
/// </summary>
public sealed class NullKeyStore : IKeyStore
{
    public static readonly NullKeyStore Instance = new();

    private NullKeyStore() { }

    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public string LastSelectedModel => "";
    public bool IsSystemPromptCustomized => false;

    public string GetSystemPrompt() => KeyStoreDefaults.GetDefaultSystemPrompt();
    public Task SetSystemPromptAsync(string prompt, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResetSystemPromptAsync(CancellationToken ct = default) => Task.CompletedTask;

    public UserProfileSettings GetUserProfile() => new();
    public Task SetUserProfileAsync(UserProfileSettings profile, CancellationToken ct = default) => Task.CompletedTask;
    public IReadOnlyList<UserMemory> GetMemories() => Array.Empty<UserMemory>();
    public Task<UserMemory> AddMemoryAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(new UserMemory(Guid.NewGuid().ToString("N"), (text ?? "").Trim(), DateTime.UtcNow));
    public Task UpdateMemoryAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveMemoryAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task ClearMemoriesAsync(CancellationToken ct = default) => Task.CompletedTask;
    public string BuildUserContextForPrompt() => "";

    public Task SetLastSelectedModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

    public ToolRoutingMode ToolRoutingMode => ToolRoutingMode.Rules;
    public string? ToolRoutingModelId => null;
    public Task SetToolRoutingAsync(ToolRoutingMode mode, string? modelId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public string GetKey(string providerId) => "";
    public Task SetKeyAsync(string providerId, string key, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveAllKeysAsync(string groq, string gemini, string openrouter, CancellationToken ct = default) =>
        Task.CompletedTask;

    public string OllamaBaseUrl => "http://localhost:11434";
    public string OllamaChatEndpoint => OllamaBaseUrl.TrimEnd('/') + "/v1/chat/completions";
    public List<string> OllamaModels => new();
    public IReadOnlyList<OllamaModelSettings> OllamaModelSettingsList => Array.Empty<OllamaModelSettings>();
    public OllamaModelSettings? GetOllamaModelSettings(string modelName) => null;
    public string? GetVisionProxyModelName() => null;
    public OllamaModelSettings GetOrCreateOllamaModelSettings(string modelName) =>
        new() { Name = modelName ?? "", Label = modelName ?? "" };
    public Task SetOllamaBaseUrlAsync(string baseUrl, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOllamaModelsAsync(List<string> models, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddOllamaModelAsync(string model, HttpClient? http = null, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task RemoveOllamaModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveOllamaModelSettingsAsync(OllamaModelSettings settings, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task RefreshOllamaModelsFromServerAsync(HttpClient http, string? baseUrl = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public string LemonadeBaseUrl => "http://localhost:13305";
    public string? LemonadeApiKey => null;
    public string LemonadeChatEndpoint => LemonadeBaseUrl.TrimEnd('/') + "/v1/chat/completions";
    public List<string> LemonadeModels => new();
    public IReadOnlyList<LemonadeModelSettings> LemonadeModelSettingsList => Array.Empty<LemonadeModelSettings>();
    public LemonadeModelSettings? GetLemonadeModelSettings(string modelName) => null;
    public LemonadeModelSettings GetOrCreateLemonadeModelSettings(string modelName) =>
        new() { Name = modelName ?? "", Label = modelName ?? "" };
    public string? LemonadeDefaultImageModel => null;
    public string? LemonadeDefaultEditModel => null;
    public string? LemonadeDefaultTtsModel => null;
    public string? LemonadeDefaultSttModel => null;
    public string? LemonadeDefaultVoice => null;
    public Task SetLemonadeBaseUrlAsync(string baseUrl, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetLemonadeApiKeyAsync(string? apiKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetLemonadeModalityDefaultsAsync(
        string? imageModel = null,
        string? editModel = null,
        string? ttsModel = null,
        string? sttModel = null,
        string? voice = null,
        CancellationToken ct = default) => Task.CompletedTask;
    public Task AddLemonadeModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveLemonadeModelAsync(string model, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveLemonadeModelSettingsAsync(LemonadeModelSettings settings, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task RefreshLemonadeModelsFromServerAsync(
        HttpClient http, string? baseUrl = null, string? apiKey = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public IReadOnlySet<string> EnabledMcpServerNames => new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool IsMcpServerEnabled(string name) => false;
    public string GetMcpToken(string serverName) => "";
    public IReadOnlyDictionary<string, string> GetAllMcpTokens() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<CustomMcpConnector> GetCustomConnectors() => Array.Empty<CustomMcpConnector>();
    public Task SetMcpServerEnabledAsync(string name, bool enabled, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task SetEnabledMcpServersAsync(IEnumerable<string> names, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task SetMcpTokenAsync(string serverName, string token, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task AddCustomConnectorAsync(string name, string serverUrl, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task RemoveCustomConnectorAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<App.Core.Connectors.OAuthConnectorInstall> GetOAuthConnectors() =>
        Array.Empty<App.Core.Connectors.OAuthConnectorInstall>();
    public App.Core.Connectors.OAuthConnectorInstall? GetOAuthConnector(string connectorId) => null;
    public App.Core.Connectors.OAuthTokenSet? GetOAuthTokens(string connectorId) => null;
    public Task UpsertOAuthConnectorAsync(App.Core.Connectors.OAuthConnectorInstall install, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task SetOAuthConnectorEnabledAsync(string connectorId, bool enabled, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task RemoveOAuthConnectorAsync(string connectorId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ReplaceOAuthConnectorsAsync(
        IEnumerable<App.Core.Connectors.OAuthConnectorInstall> installs, CancellationToken ct = default) =>
        Task.CompletedTask;

    public string HomeAssistantBaseUrl => "";
    public string HomeAssistantToken => "";
    public string HomeAssistantAssistantName => "";
    public string HomeAssistantDeviceSummary => "";
    public DateTime? HomeAssistantDeviceSummaryUpdatedAt => null;
    public Task SetHomeAssistantConfigAsync(
        string baseUrl, string token, string assistantName, CancellationToken ct = default) =>
        Task.CompletedTask;
    public Task UpdateHomeAssistantDeviceSummaryAsync(string summary, CancellationToken ct = default) =>
        Task.CompletedTask;
}
