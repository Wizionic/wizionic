using ChatfishApp.Core.Ollama;
using ChatfishApp.Core.SmartHome;
using ChatfishApp.Core.Storage;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Browser localStorage implementation of <see cref="IKeyStore"/>.
/// </summary>
public class WasmKeyStore : IKeyStore
{
    private const string KeysStorageKey = "wasm-provider-keys";
    private const string OllamaConfigKey = "wasm-ollama-config";
    private const string McpEnabledKey = "wasm-mcp-enabled-servers";
    private const string McpTokensKey = "wasm-mcp-tokens";
    private const string McpCustomConnectorsKey = "wasm-mcp-custom-connectors";
    private const string LastSelectedModelKey = "wasm-last-selected-model";
    private const string SystemPromptKey = "wasm-system-prompt";
    private const string UserProfileKey = "wasm-user-profile";
    private const string UserMemoriesKey = "wasm-user-memories";
    private const string HomeAssistantConfigKey = "wasm-home-assistant-config";

    private readonly IJSRuntime _js;

    private Dictionary<string, string> _providerKeys = new();
    private string _lastSelectedModel = "";
    private string? _systemPrompt;
    private bool _systemPromptCustomized;
    private UserProfileSettings _userProfile = new();
    private List<UserMemory> _userMemories = new();
    private OllamaConfig _ollamaConfig = new();
    private HashSet<string> _enabledMcpServers = new();
    private Dictionary<string, string> _mcpTokens = new();
    private List<CustomMcpConnector> _customMcpConnectors = new();
    private HomeAssistantConfig _homeAssistantConfig = new();

    public WasmKeyStore(IJSRuntime js) => _js = js;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var keysJson = await _js.InvokeAsync<string>("localStorage.getItem", ct, KeysStorageKey);
        if (!string.IsNullOrEmpty(keysJson))
            _providerKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(keysJson) ?? new();

        var ollamaJson = await _js.InvokeAsync<string>("localStorage.getItem", ct, OllamaConfigKey);
        if (!string.IsNullOrEmpty(ollamaJson))
        {
            var loaded = JsonSerializer.Deserialize<OllamaConfig>(ollamaJson);
            if (loaded != null)
                _ollamaConfig = MigrateOllamaConfig(loaded);
        }

        var mcpJson = await _js.InvokeAsync<string>("localStorage.getItem", ct, McpEnabledKey);
        if (!string.IsNullOrEmpty(mcpJson))
        {
            var names = JsonSerializer.Deserialize<List<string>>(mcpJson);
            if (names != null)
                _enabledMcpServers = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        var tokensJson = await _js.InvokeAsync<string>("localStorage.getItem", ct, McpTokensKey);
        if (!string.IsNullOrEmpty(tokensJson))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(tokensJson);
            if (loaded != null)
                _mcpTokens = loaded;
        }

        var customsJson = await _js.InvokeAsync<string>("localStorage.getItem", ct, McpCustomConnectorsKey);
        if (!string.IsNullOrEmpty(customsJson))
        {
            var loaded = JsonSerializer.Deserialize<List<CustomMcpConnector>>(customsJson);
            if (loaded != null)
                _customMcpConnectors = loaded;
        }

        _lastSelectedModel = await _js.InvokeAsync<string?>("localStorage.getItem", ct, LastSelectedModelKey) ?? "";

        var systemPromptJson = await _js.InvokeAsync<string?>("localStorage.getItem", ct, SystemPromptKey);
        _systemPromptCustomized = systemPromptJson != null;
        _systemPrompt = systemPromptJson;

        var profileJson = await _js.InvokeAsync<string?>("localStorage.getItem", ct, UserProfileKey);
        if (!string.IsNullOrEmpty(profileJson))
        {
            var loaded = JsonSerializer.Deserialize<UserProfileSettings>(profileJson);
            if (loaded != null)
                _userProfile = loaded;
        }

        var memoriesJson = await _js.InvokeAsync<string?>("localStorage.getItem", ct, UserMemoriesKey);
        if (!string.IsNullOrEmpty(memoriesJson))
        {
            var loaded = JsonSerializer.Deserialize<List<UserMemory>>(memoriesJson);
            if (loaded != null)
                _userMemories = loaded;
        }

        var haJson = await _js.InvokeAsync<string?>("localStorage.getItem", ct, HomeAssistantConfigKey);
        if (!string.IsNullOrEmpty(haJson))
        {
            var loaded = JsonSerializer.Deserialize<HomeAssistantConfig>(haJson);
            if (loaded != null)
                _homeAssistantConfig = loaded;
        }
    }

    public string LastSelectedModel => _lastSelectedModel;
    public bool IsSystemPromptCustomized => _systemPromptCustomized;

    public string GetSystemPrompt() =>
        _systemPromptCustomized ? (_systemPrompt ?? "") : KeyStoreDefaults.GetDefaultSystemPrompt();

    public async Task SetSystemPromptAsync(string prompt, CancellationToken ct = default)
    {
        _systemPrompt = prompt ?? "";
        _systemPromptCustomized = true;
        await _js.InvokeVoidAsync("localStorage.setItem", ct, SystemPromptKey, _systemPrompt);
    }

    public async Task ResetSystemPromptAsync(CancellationToken ct = default)
    {
        _systemPrompt = null;
        _systemPromptCustomized = false;
        await _js.InvokeVoidAsync("localStorage.removeItem", ct, SystemPromptKey);
    }

    public UserProfileSettings GetUserProfile() => _userProfile.Clone();

    public async Task SetUserProfileAsync(UserProfileSettings profile, CancellationToken ct = default)
    {
        _userProfile = new UserProfileSettings
        {
            CustomizationEnabled = profile.CustomizationEnabled,
            PreferredName = (profile.PreferredName ?? "").Trim(),
            Occupation = (profile.Occupation ?? "").Trim()
        };
        var json = JsonSerializer.Serialize(_userProfile);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, UserProfileKey, json);
    }

    public IReadOnlyList<UserMemory> GetMemories() =>
        _userMemories.OrderByDescending(m => m.CreatedAtUtc).ToList();

    public async Task<UserMemory> AddMemoryAsync(string text, CancellationToken ct = default)
    {
        var trimmed = (text ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Memory text is required.");

        var memory = new UserMemory(Guid.NewGuid().ToString("N"), trimmed, DateTime.UtcNow);
        _userMemories.Add(memory);
        await SaveMemoriesAsync(ct);
        return memory;
    }

    public async Task UpdateMemoryAsync(string id, string text, CancellationToken ct = default)
    {
        var trimmed = (text ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ArgumentException("Memory text is required.");

        var index = _userMemories.FindIndex(m => m.Id == id);
        if (index < 0)
            throw new InvalidOperationException("Memory not found.");

        _userMemories[index] = _userMemories[index].WithText(trimmed);
        await SaveMemoriesAsync(ct);
    }

    public async Task RemoveMemoryAsync(string id, CancellationToken ct = default)
    {
        _userMemories.RemoveAll(m => m.Id == id);
        await SaveMemoriesAsync(ct);
    }

    public async Task ClearMemoriesAsync(CancellationToken ct = default)
    {
        _userMemories.Clear();
        await SaveMemoriesAsync(ct);
    }

    private async Task SaveMemoriesAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_userMemories);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, UserMemoriesKey, json);
    }

    public string BuildUserContextForPrompt()
    {
        var parts = new List<string>();

        if (_userProfile.CustomizationEnabled)
        {
            var aboutLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(_userProfile.PreferredName))
                aboutLines.Add($"- Call the user: {_userProfile.PreferredName.Trim()}");
            if (!string.IsNullOrWhiteSpace(_userProfile.Occupation))
                aboutLines.Add($"- They do: {_userProfile.Occupation.Trim()}");

            if (aboutLines.Count > 0)
            {
                parts.Add("**About the user:**");
                parts.AddRange(aboutLines);
            }
        }

        if (_userMemories.Count > 0)
        {
            if (parts.Count > 0)
                parts.Add("");

            parts.Add("**User memories (facts Chatfish should remember):**");
            foreach (var memory in _userMemories.OrderByDescending(m => m.CreatedAtUtc))
                parts.Add($"- {memory.Text.Trim()}");
        }

        return string.Join("\n", parts).Trim();
    }

    public async Task SetLastSelectedModelAsync(string modelId, CancellationToken ct = default)
    {
        _lastSelectedModel = (modelId ?? "").Trim();
        if (string.IsNullOrEmpty(_lastSelectedModel))
            await _js.InvokeVoidAsync("localStorage.removeItem", ct, LastSelectedModelKey);
        else
            await _js.InvokeVoidAsync("localStorage.setItem", ct, LastSelectedModelKey, _lastSelectedModel);
    }

    public string GetKey(string providerId) =>
        _providerKeys.GetValueOrDefault(providerId, "");

    public async Task SetKeyAsync(string providerId, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            _providerKeys.Remove(providerId);
        else
            _providerKeys[providerId] = key.Trim();

        var json = JsonSerializer.Serialize(_providerKeys);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, KeysStorageKey, json);
    }

    public async Task SaveAllKeysAsync(string groq, string gemini, string openrouter, CancellationToken ct = default)
    {
        _providerKeys["groq"] = (groq ?? "").Trim();
        _providerKeys["gemini"] = (gemini ?? "").Trim();
        _providerKeys["openrouter"] = (openrouter ?? "").Trim();

        var json = JsonSerializer.Serialize(_providerKeys);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, KeysStorageKey, json);
    }

    public string OllamaBaseUrl => _ollamaConfig.BaseUrl ?? "http://localhost:11434";
    public string OllamaChatEndpoint => OllamaBaseUrl.TrimEnd('/') + "/v1/chat/completions";

    public List<string> OllamaModels =>
        GetModelSettingsMap().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<OllamaModelSettings> OllamaModelSettingsList =>
        GetModelSettingsMap().Values
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public OllamaModelSettings? GetOllamaModelSettings(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        return GetModelSettingsMap().TryGetValue(modelName.Trim(), out var settings)
            ? settings.Clone()
            : null;
    }

    public string? GetVisionProxyModelName() =>
        GetModelSettingsMap().Values
            .FirstOrDefault(m => m.IsVisionProxy && m.SupportsVision)
            ?.Name;

    public OllamaModelSettings GetOrCreateOllamaModelSettings(string modelName)
    {
        modelName = modelName.Trim();
        var map = GetModelSettingsMap();
        if (map.TryGetValue(modelName, out var existing))
            return existing.Clone();

        return OllamaCapabilitiesResolver.CreateDefaultSettings(modelName);
    }

    public async Task SetOllamaBaseUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        _ollamaConfig = _ollamaConfig with { BaseUrl = baseUrl.Trim() };
        await SaveOllamaConfigAsync(ct);
    }

    public async Task SetOllamaModelsAsync(List<string> models, CancellationToken ct = default)
    {
        var map = GetModelSettingsMap();
        var distinct = models.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var next = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in distinct)
        {
            if (map.TryGetValue(name, out var existing))
                next[name] = existing;
            else
                next[name] = OllamaCapabilitiesResolver.CreateDefaultSettings(name);
        }

        _ollamaConfig = _ollamaConfig with { Models = distinct, ModelSettings = next };
        await SaveOllamaConfigAsync(ct);
    }

    public async Task AddOllamaModelAsync(string model, HttpClient? http = null, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model))
            return;

        var map = GetModelSettingsMap();
        if (map.ContainsKey(model))
            return;

        OllamaModelSettings settings;
        if (http != null)
        {
            var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, OllamaBaseUrl, model, ct);
            settings = OllamaCapabilitiesResolver.ResolveSettings(model, live);
        }
        else
        {
            settings = OllamaCapabilitiesResolver.CreateDefaultSettings(model);
        }

        map[model] = settings;
        _ollamaConfig = _ollamaConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveOllamaConfigAsync(ct);
    }

    public async Task RemoveOllamaModelAsync(string model, CancellationToken ct = default)
    {
        var map = GetModelSettingsMap();
        map.Remove(model);
        _ollamaConfig = _ollamaConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveOllamaConfigAsync(ct);
    }

    public async Task SaveOllamaModelSettingsAsync(OllamaModelSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
            return;

        var map = GetModelSettingsMap();
        settings.Name = settings.Name.Trim();
        settings.Label = string.IsNullOrWhiteSpace(settings.Label) ? settings.Name : settings.Label.Trim();
        settings.UserOverrideTools = true;
        settings.UserOverrideVision = true;
        settings.UserOverrideContext = true;

        if (!settings.SupportsVision)
            settings.IsVisionProxy = false;

        if (settings.IsVisionProxy)
        {
            foreach (var key in map.Keys.ToList())
            {
                if (!key.Equals(settings.Name, StringComparison.OrdinalIgnoreCase) && map[key].IsVisionProxy)
                    map[key].IsVisionProxy = false;
            }
        }

        map[settings.Name] = settings;
        _ollamaConfig = _ollamaConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveOllamaConfigAsync(ct);
    }

    private async Task SaveOllamaConfigAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_ollamaConfig);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, OllamaConfigKey, json);
    }

    public async Task RefreshOllamaModelsFromServerAsync(HttpClient http, string? baseUrl = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            _ollamaConfig = _ollamaConfig with { BaseUrl = baseUrl.Trim() };

        try
        {
            var origin = OllamaBaseUrl.TrimEnd('/');
            var tagsUrl = origin + "/api/tags";
            var resp = await http.GetFromJsonAsync<OllamaTagsResponse>(tagsUrl, ct);
            if (resp?.models == null)
                return;

            var existingMap = GetModelSettingsMap();
            var next = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in resp.models.Where(m => !string.IsNullOrWhiteSpace(m.name)).Select(m => m.name!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                existingMap.TryGetValue(tag, out var existing);
                var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, origin, tag, ct);
                next[tag] = OllamaCapabilitiesResolver.ResolveSettings(tag, live, existing);
            }

            _ollamaConfig = _ollamaConfig with
            {
                Models = next.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                ModelSettings = next
            };
            await SaveOllamaConfigAsync(ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to refresh Ollama models from {OllamaBaseUrl}: {ex.Message}", ex);
        }
    }

    private Dictionary<string, OllamaModelSettings> GetModelSettingsMap()
    {
        if (_ollamaConfig.ModelSettings is { Count: > 0 })
            return new Dictionary<string, OllamaModelSettings>(_ollamaConfig.ModelSettings, StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _ollamaConfig.Models ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;

            map[name.Trim()] = OllamaCapabilitiesResolver.CreateDefaultSettings(name.Trim());
        }

        return map;
    }

    private static OllamaConfig MigrateOllamaConfig(OllamaConfig loaded)
    {
        if (loaded.ModelSettings is { Count: > 0 })
            return loaded;

        if (loaded.Models is not { Count: > 0 })
            return loaded with { ModelSettings = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase) };

        var map = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in loaded.Models.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            var trimmed = name.Trim();
            map[trimmed] = OllamaCapabilitiesResolver.CreateDefaultSettings(trimmed);
        }

        return loaded with { ModelSettings = map };
    }

    private class OllamaTagsResponse
    {
        public List<OllamaModel> models { get; set; } = new();
    }

    private class OllamaModel
    {
        public string name { get; set; } = "";
    }

    public IReadOnlySet<string> EnabledMcpServerNames => _enabledMcpServers;

    public bool IsMcpServerEnabled(string name) =>
        !string.IsNullOrWhiteSpace(name) && _enabledMcpServers.Contains(name);

    public async Task SetMcpServerEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (enabled)
            _enabledMcpServers.Add(name);
        else
            _enabledMcpServers.Remove(name);

        await SaveMcpEnabledAsync(ct);
    }

    public async Task SetEnabledMcpServersAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        _enabledMcpServers = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        await SaveMcpEnabledAsync(ct);
    }

    private async Task SaveMcpEnabledAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_enabledMcpServers.ToList());
        await _js.InvokeVoidAsync("localStorage.setItem", ct, McpEnabledKey, json);
    }

    public string GetMcpToken(string serverName) =>
        string.IsNullOrWhiteSpace(serverName) ? "" : _mcpTokens.GetValueOrDefault(serverName, "");

    public async Task SetMcpTokenAsync(string serverName, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return;

        if (string.IsNullOrWhiteSpace(token))
            _mcpTokens.Remove(serverName);
        else
            _mcpTokens[serverName] = token.Trim();

        var json = JsonSerializer.Serialize(_mcpTokens);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, McpTokensKey, json);
    }

    public IReadOnlyDictionary<string, string> GetAllMcpTokens() =>
        new Dictionary<string, string>(_mcpTokens);

    public IReadOnlyList<CustomMcpConnector> GetCustomConnectors() =>
        _customMcpConnectors.ToList();

    public async Task AddCustomConnectorAsync(string name, string serverUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(serverUrl))
            return;

        name = name.Trim();
        serverUrl = serverUrl.Trim();

        _customMcpConnectors.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _customMcpConnectors.Add(new CustomMcpConnector(name, serverUrl));
        await SaveCustomConnectorsAsync(ct);
    }

    public async Task RemoveCustomConnectorAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        int removed = _customMcpConnectors.RemoveAll(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            _enabledMcpServers.Remove(name.Trim());
            _mcpTokens.Remove(name.Trim());

            await SaveCustomConnectorsAsync(ct);
            await SaveMcpEnabledAsync(ct);
            await SaveMcpTokensAsync(ct);
        }
    }

    private async Task SaveCustomConnectorsAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_customMcpConnectors);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, McpCustomConnectorsKey, json);
    }

    private async Task SaveMcpTokensAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_mcpTokens);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, McpTokensKey, json);
    }

    public string HomeAssistantBaseUrl => _homeAssistantConfig.BaseUrl ?? "";
    public string HomeAssistantToken => _homeAssistantConfig.Token ?? "";
    public string HomeAssistantAssistantName =>
        string.IsNullOrWhiteSpace(_homeAssistantConfig.AssistantName)
            ? "Home"
            : _homeAssistantConfig.AssistantName.Trim();
    public string HomeAssistantDeviceSummary => _homeAssistantConfig.CachedDeviceSummary ?? "";
    public DateTime? HomeAssistantDeviceSummaryUpdatedAt => _homeAssistantConfig.DeviceSummaryUpdatedAt;

    public async Task SetHomeAssistantConfigAsync(string baseUrl, string token, string assistantName, CancellationToken ct = default)
    {
        _homeAssistantConfig = new HomeAssistantConfig
        {
            BaseUrl = baseUrl?.Trim().TrimEnd('/') ?? "",
            Token = HomeAssistantCredentials.NormalizeToken(token),
            AssistantName = string.IsNullOrWhiteSpace(assistantName) ? "Home" : assistantName.Trim(),
            CachedDeviceSummary = _homeAssistantConfig.CachedDeviceSummary,
            DeviceSummaryUpdatedAt = _homeAssistantConfig.DeviceSummaryUpdatedAt
        };
        var json = JsonSerializer.Serialize(_homeAssistantConfig);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, HomeAssistantConfigKey, json);
    }

    public async Task UpdateHomeAssistantDeviceSummaryAsync(string summary, CancellationToken ct = default)
    {
        _homeAssistantConfig.CachedDeviceSummary = summary ?? "";
        _homeAssistantConfig.DeviceSummaryUpdatedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_homeAssistantConfig);
        await _js.InvokeVoidAsync("localStorage.setItem", ct, HomeAssistantConfigKey, json);
    }
}