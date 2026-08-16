using App.Core.Auth;
using App.Core.Connectors;
using App.Core.Lemonade;
using App.Core.Ollama;
using App.Core.SmartHome;
using App.Core.Storage;
using App.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Text.Json;

namespace App.Client.Services;

/// <summary>
/// Browser localStorage implementation of <see cref="IKeyStore"/>.
/// Settings are stored under a per-user (or guest) prefix so multi-account
/// use on the same browser does not share Ollama keys, MCP config, etc.
/// </summary>
public class WasmKeyStore : IKeyStore
{
    private const string KeysStorageKey = "wasm-provider-keys";
    private const string OllamaConfigKey = "wasm-ollama-config";
    private const string LemonadeConfigKey = "wasm-lemonade-config";
    private const string McpEnabledKey = "wasm-mcp-enabled-servers";
    private const string McpTokensKey = "wasm-mcp-tokens";
    private const string McpCustomConnectorsKey = "wasm-mcp-custom-connectors";
    private const string OAuthConnectorsKey = "wasm-oauth-connectors";
    private const string LastSelectedModelKey = "wasm-last-selected-model";
    private const string ToolRoutingKey = "wasm-tool-routing";
    private const string HelpModelsKey = "wasm-help-models";
    private const string SystemPromptKey = "wasm-system-prompt";
    private const string UserProfileKey = "wasm-user-profile";
    private const string UserMemoriesKey = "wasm-user-memories";
    private const string HomeAssistantConfigKey = "wasm-home-assistant-config";
    private const string EncPrefix = "enc:";

    private readonly IJSRuntime _js;
    private readonly IAuthService _auth;
    private readonly IServiceScopeFactory _scopeFactory;

    private Dictionary<string, string> _providerKeys = new();
    private string _lastSelectedModel = "";
    private ToolRoutingMode _toolRoutingMode = ToolRoutingMode.Rules;
    private string? _toolRoutingModelId;
    private string? _helpAnswerModelId;
    private string? _helpEmbedModelId;
    private string? _systemPrompt;
    private bool _systemPromptCustomized;
    private UserProfileSettings _userProfile = new();
    private List<UserMemory> _userMemories = new();
    private OllamaConfig _ollamaConfig = new();
    private LemonadeConfig _lemonadeConfig = new();
    private HashSet<string> _enabledMcpServers = new();
    private Dictionary<string, string> _mcpTokens = new();
    private List<CustomMcpConnector> _customMcpConnectors = new();
    private List<OAuthConnectorInstall> _oauthConnectors = new();
    private HomeAssistantConfig _homeAssistantConfig = new();

    public WasmKeyStore(IJSRuntime js, IAuthService auth, IServiceScopeFactory scopeFactory)
    {
        _js = js;
        _auth = auth;
        _scopeFactory = scopeFactory;
        _auth.OnChanged += () => _ = LoadAsync();
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        ResetInMemoryState();

        var keysJson = await GetItemAsync(KeysStorageKey, ct);
        if (!string.IsNullOrEmpty(keysJson))
            _providerKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(keysJson) ?? new();

        var ollamaJson = await GetItemAsync(OllamaConfigKey, ct);
        if (!string.IsNullOrEmpty(ollamaJson))
        {
            var loaded = JsonSerializer.Deserialize<OllamaConfig>(ollamaJson);
            if (loaded != null)
                _ollamaConfig = MigrateOllamaConfig(loaded);
        }

        var lemonadeJson = await GetItemAsync(LemonadeConfigKey, ct);
        if (!string.IsNullOrEmpty(lemonadeJson))
        {
            var loaded = JsonSerializer.Deserialize<LemonadeConfig>(lemonadeJson);
            if (loaded != null)
                _lemonadeConfig = MigrateLemonadeConfig(loaded);
        }

        var mcpJson = await GetItemAsync(McpEnabledKey, ct);
        if (!string.IsNullOrEmpty(mcpJson))
        {
            var names = JsonSerializer.Deserialize<List<string>>(mcpJson);
            if (names != null)
                _enabledMcpServers = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        var tokensJson = await GetItemAsync(McpTokensKey, ct);
        if (!string.IsNullOrEmpty(tokensJson))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(tokensJson);
            if (loaded != null)
                _mcpTokens = loaded;
        }

        var customsJson = await GetItemAsync(McpCustomConnectorsKey, ct);
        if (!string.IsNullOrEmpty(customsJson))
        {
            var loaded = JsonSerializer.Deserialize<List<CustomMcpConnector>>(customsJson);
            if (loaded != null)
                _customMcpConnectors = loaded;
        }

        var oauthJson = await GetItemAsync(OAuthConnectorsKey, ct);
        if (!string.IsNullOrEmpty(oauthJson))
            _oauthConnectors = await DeserializeOAuthConnectorsAsync(oauthJson, ct);

        _lastSelectedModel = await GetItemAsync(LastSelectedModelKey, ct) ?? "";

        var helpModelsJson = await GetItemAsync(HelpModelsKey, ct);
        if (!string.IsNullOrEmpty(helpModelsJson))
        {
            try
            {
                var hm = JsonSerializer.Deserialize<HelpModelsConfig>(helpModelsJson);
                if (hm != null)
                {
                    _helpAnswerModelId = string.IsNullOrWhiteSpace(hm.AnswerModelId) ? null : hm.AnswerModelId.Trim();
                    _helpEmbedModelId = string.IsNullOrWhiteSpace(hm.EmbedModelId) ? null : hm.EmbedModelId.Trim();
                }
            }
            catch { /* keep defaults */ }
        }

        var toolRoutingJson = await GetItemAsync(ToolRoutingKey, ct);
        if (!string.IsNullOrEmpty(toolRoutingJson))
        {
            try
            {
                var tr = JsonSerializer.Deserialize<ToolRoutingConfig>(toolRoutingJson);
                if (tr != null)
                {
                    _toolRoutingMode = Enum.IsDefined(typeof(ToolRoutingMode), tr.Mode)
                        ? (ToolRoutingMode)tr.Mode
                        : ToolRoutingMode.Rules;
                    _toolRoutingModelId = string.IsNullOrWhiteSpace(tr.ModelId) ? null : tr.ModelId.Trim();
                }
            }
            catch { /* keep defaults */ }
        }

        var systemPromptJson = await GetItemAsync(SystemPromptKey, ct);
        _systemPromptCustomized = systemPromptJson != null;
        _systemPrompt = systemPromptJson;

        var profileJson = await GetItemAsync(UserProfileKey, ct);
        if (!string.IsNullOrEmpty(profileJson))
        {
            var loaded = JsonSerializer.Deserialize<UserProfileSettings>(profileJson);
            if (loaded != null)
                _userProfile = loaded;
        }

        var memoriesJson = await GetItemAsync(UserMemoriesKey, ct);
        if (!string.IsNullOrEmpty(memoriesJson))
        {
            var loaded = JsonSerializer.Deserialize<List<UserMemory>>(memoriesJson);
            if (loaded != null)
                _userMemories = loaded;
        }

        var haJson = await GetItemAsync(HomeAssistantConfigKey, ct);
        if (!string.IsNullOrEmpty(haJson))
        {
            var loaded = JsonSerializer.Deserialize<HomeAssistantConfig>(haJson);
            if (loaded != null)
                _homeAssistantConfig = loaded;
        }
    }

    private void ResetInMemoryState()
    {
        _providerKeys = new();
        _lastSelectedModel = "";
        _helpAnswerModelId = null;
        _helpEmbedModelId = null;
        _systemPrompt = null;
        _systemPromptCustomized = false;
        _userProfile = new();
        _userMemories = new();
        _ollamaConfig = new();
        _lemonadeConfig = new();
        _enabledMcpServers = new();
        _mcpTokens = new();
        _customMcpConnectors = new();
        _oauthConnectors = new();
        _homeAssistantConfig = new();
    }

    private string Prefixed(string baseKey) => StorageNamespace.PrefixedKey(_auth, baseKey);

    /// <summary>
    /// Read namespaced key; if missing, seed once from legacy unprefixed key so existing
    /// single-user installs keep settings after multi-user isolation is enabled.
    /// </summary>
    private async Task<string?> GetItemAsync(string baseKey, CancellationToken ct = default)
    {
        var nk = Prefixed(baseKey);
        var value = await _js.InvokeAsync<string?>("localStorage.getItem", ct, nk);
        if (value != null)
            return value;

        var legacy = await _js.InvokeAsync<string?>("localStorage.getItem", ct, baseKey);
        if (legacy != null)
        {
            await _js.InvokeVoidAsync("localStorage.setItem", ct, nk, legacy);
            return legacy;
        }

        return null;
    }

    private Task SetItemAsync(string baseKey, string value, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("localStorage.setItem", ct, Prefixed(baseKey), value).AsTask();

    private Task RemoveItemAsync(string baseKey, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("localStorage.removeItem", ct, Prefixed(baseKey)).AsTask();

    public string LastSelectedModel => _lastSelectedModel;
    public bool IsSystemPromptCustomized => _systemPromptCustomized;

    public string GetSystemPrompt() =>
        _systemPromptCustomized ? (_systemPrompt ?? "") : KeyStoreDefaults.GetDefaultSystemPrompt();

    public async Task SetSystemPromptAsync(string prompt, CancellationToken ct = default)
    {
        _systemPrompt = prompt ?? "";
        _systemPromptCustomized = true;
        await SetItemAsync(SystemPromptKey, _systemPrompt, ct);
    }

    public async Task ResetSystemPromptAsync(CancellationToken ct = default)
    {
        _systemPrompt = null;
        _systemPromptCustomized = false;
        await RemoveItemAsync(SystemPromptKey, ct);
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
        await SetItemAsync(UserProfileKey, json, ct);
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
        await SetItemAsync(UserMemoriesKey, json, ct);
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

            parts.Add("**User memories (facts Wizionic should remember):**");
            foreach (var memory in _userMemories.OrderByDescending(m => m.CreatedAtUtc))
                parts.Add($"- {memory.Text.Trim()}");
        }

        return string.Join("\n", parts).Trim();
    }

    public async Task SetLastSelectedModelAsync(string modelId, CancellationToken ct = default)
    {
        _lastSelectedModel = (modelId ?? "").Trim();
        if (string.IsNullOrEmpty(_lastSelectedModel))
            await RemoveItemAsync(LastSelectedModelKey, ct);
        else
            await SetItemAsync(LastSelectedModelKey, _lastSelectedModel, ct);
    }

    public ToolRoutingMode ToolRoutingMode => _toolRoutingMode;
    public string? ToolRoutingModelId => _toolRoutingModelId;

    public async Task SetToolRoutingAsync(ToolRoutingMode mode, string? modelId, CancellationToken ct = default)
    {
        _toolRoutingMode = mode;
        _toolRoutingModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        var json = JsonSerializer.Serialize(new ToolRoutingConfig
        {
            Mode = (int)_toolRoutingMode,
            ModelId = _toolRoutingModelId
        });
        await SetItemAsync(ToolRoutingKey, json, ct);
    }

    private sealed class ToolRoutingConfig
    {
        public int Mode { get; set; }
        public string? ModelId { get; set; }
    }

    public string? HelpAnswerModelId => _helpAnswerModelId;
    public string? HelpEmbedModelId => _helpEmbedModelId;

    public async Task SetHelpModelsAsync(string? answerModelId, string? embedModelId, CancellationToken ct = default)
    {
        _helpAnswerModelId = string.IsNullOrWhiteSpace(answerModelId) ? null : answerModelId.Trim();
        _helpEmbedModelId = string.IsNullOrWhiteSpace(embedModelId) ? null : embedModelId.Trim();
        var json = JsonSerializer.Serialize(new HelpModelsConfig
        {
            AnswerModelId = _helpAnswerModelId,
            EmbedModelId = _helpEmbedModelId
        });
        await SetItemAsync(HelpModelsKey, json, ct);
    }

    private sealed class HelpModelsConfig
    {
        public string? AnswerModelId { get; set; }
        public string? EmbedModelId { get; set; }
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
        await SetItemAsync(KeysStorageKey, json, ct);
    }

    public async Task SaveAllKeysAsync(string groq, string gemini, string openrouter, CancellationToken ct = default)
    {
        _providerKeys["groq"] = (groq ?? "").Trim();
        _providerKeys["gemini"] = (gemini ?? "").Trim();
        _providerKeys["openrouter"] = (openrouter ?? "").Trim();

        var json = JsonSerializer.Serialize(_providerKeys);
        await SetItemAsync(KeysStorageKey, json, ct);
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

    public string? GetVisionProxyModelName()
    {
        var ollama = GetModelSettingsMap().Values
            .FirstOrDefault(m => m.IsVisionProxy && m.SupportsVision);
        if (ollama != null)
            return "ollama/" + ollama.Name;

        var lemonade = GetLemonadeModelSettingsMap().Values
            .FirstOrDefault(m => m.IsVisionProxy && m.SupportsVision);
        if (lemonade != null)
            return "lemonade/" + lemonade.Name;

        return null;
    }

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

            // Single global vision proxy: clear Lemonade proxies too.
            await ClearLemonadeVisionProxiesAsync(ct);
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
        await SetItemAsync(OllamaConfigKey, json, ct);
    }

    public async Task RefreshOllamaModelsFromServerAsync(HttpClient http, string? baseUrl = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            _ollamaConfig = _ollamaConfig with { BaseUrl = baseUrl.Trim() };

        try
        {
            var origin = OllamaBaseUrl.TrimEnd('/');
            // /api/tags for real Ollama; /v1/models when this URL is Lemonade (or OpenAI-compatible).
            var modelNames = await OllamaCapabilitiesResolver.ListModelNamesAsync(http, origin, ct);
            if (modelNames.Count == 0)
                throw new InvalidOperationException(
                    "No models returned. Is the server running? For Lemonade, use port 13305 and ensure /v1/models is reachable.");

            // Prefer /api/tags with sizes; fall back to name-only list.
            var installed = await OllamaCapabilitiesResolver.ListInstalledModelsAsync(http, origin, ct);
            var existingMap = GetModelSettingsMap();
            var next = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);

            if (installed.Count > 0)
            {
                foreach (var tag in installed)
                {
                    existingMap.TryGetValue(tag.Name, out var existing);
                    var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, origin, tag.Name, ct);
                    next[tag.Name] = OllamaCapabilitiesResolver.ResolveSettings(tag.Name, live, existing, tag.SizeBytes);
                }
            }
            else
            {
                foreach (var tag in modelNames.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    existingMap.TryGetValue(tag, out var existing);
                    var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, origin, tag, ct);
                    next[tag] = OllamaCapabilitiesResolver.ResolveSettings(tag, live, existing);
                }
            }

            // Replace list entirely — do not keep models from a previous Base URL.
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

    // --- Lemonade ---

    public string LemonadeBaseUrl =>
        LemonadeModelCatalogResolver.NormalizeBaseUrl(_lemonadeConfig.BaseUrl);

    public string? LemonadeApiKey =>
        string.IsNullOrWhiteSpace(_lemonadeConfig.ApiKey) ? null : _lemonadeConfig.ApiKey.Trim();

    public string LemonadeChatEndpoint =>
        LemonadeBaseUrl.TrimEnd('/') + "/v1/chat/completions";

    public List<string> LemonadeModels =>
        GetLemonadeModelSettingsMap().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<LemonadeModelSettings> LemonadeModelSettingsList =>
        GetLemonadeModelSettingsMap().Values
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public LemonadeModelSettings? GetLemonadeModelSettings(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return null;

        return GetLemonadeModelSettingsMap().TryGetValue(modelName.Trim(), out var settings)
            ? settings.Clone()
            : null;
    }

    public LemonadeModelSettings GetOrCreateLemonadeModelSettings(string modelName)
    {
        modelName = modelName.Trim();
        var map = GetLemonadeModelSettingsMap();
        if (map.TryGetValue(modelName, out var existing))
            return existing.Clone();

        return LemonadeModelCatalogResolver.CreateDefaultSettings(modelName);
    }

    public string? LemonadeDefaultImageModel => _lemonadeConfig.DefaultImageModel;
    public string? LemonadeDefaultEditModel => _lemonadeConfig.DefaultEditModel;
    public string? LemonadeDefaultTtsModel => _lemonadeConfig.DefaultTtsModel;
    public string? LemonadeDefaultSttModel => _lemonadeConfig.DefaultSttModel;
    public string? LemonadeDefaultVoice => _lemonadeConfig.DefaultVoice;

    public async Task SetLemonadeBaseUrlAsync(string baseUrl, CancellationToken ct = default)
    {
        _lemonadeConfig = _lemonadeConfig with
        {
            BaseUrl = LemonadeModelCatalogResolver.NormalizeBaseUrl(baseUrl)
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task SetLemonadeApiKeyAsync(string? apiKey, CancellationToken ct = default)
    {
        _lemonadeConfig = _lemonadeConfig with
        {
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim()
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task SetLemonadeModalityDefaultsAsync(
        string? imageModel = null,
        string? editModel = null,
        string? ttsModel = null,
        string? sttModel = null,
        string? voice = null,
        CancellationToken ct = default)
    {
        // Null or whitespace clears that default slot (UI always passes current selections).
        _lemonadeConfig = _lemonadeConfig with
        {
            DefaultImageModel = string.IsNullOrWhiteSpace(imageModel) ? null : imageModel.Trim(),
            DefaultEditModel = string.IsNullOrWhiteSpace(editModel) ? null : editModel.Trim(),
            DefaultTtsModel = string.IsNullOrWhiteSpace(ttsModel) ? null : ttsModel.Trim(),
            DefaultSttModel = string.IsNullOrWhiteSpace(sttModel) ? null : sttModel.Trim(),
            DefaultVoice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim()
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task AddLemonadeModelAsync(string model, CancellationToken ct = default)
    {
        model = (model ?? "").Trim();
        if (string.IsNullOrWhiteSpace(model))
            return;

        var map = GetLemonadeModelSettingsMap();
        if (map.ContainsKey(model))
            return;

        map[model] = LemonadeModelCatalogResolver.CreateDefaultSettings(model);
        _lemonadeConfig = _lemonadeConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task RemoveLemonadeModelAsync(string model, CancellationToken ct = default)
    {
        var map = GetLemonadeModelSettingsMap();
        map.Remove(model);
        _lemonadeConfig = _lemonadeConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map,
            DefaultImageModel = string.Equals(_lemonadeConfig.DefaultImageModel, model, StringComparison.OrdinalIgnoreCase)
                ? null : _lemonadeConfig.DefaultImageModel,
            DefaultEditModel = string.Equals(_lemonadeConfig.DefaultEditModel, model, StringComparison.OrdinalIgnoreCase)
                ? null : _lemonadeConfig.DefaultEditModel,
            DefaultTtsModel = string.Equals(_lemonadeConfig.DefaultTtsModel, model, StringComparison.OrdinalIgnoreCase)
                ? null : _lemonadeConfig.DefaultTtsModel,
            DefaultSttModel = string.Equals(_lemonadeConfig.DefaultSttModel, model, StringComparison.OrdinalIgnoreCase)
                ? null : _lemonadeConfig.DefaultSttModel
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task SaveLemonadeModelSettingsAsync(LemonadeModelSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Name))
            return;

        var map = GetLemonadeModelSettingsMap();
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

            await ClearOllamaVisionProxiesAsync(ct);
        }

        map[settings.Name] = settings;
        _lemonadeConfig = _lemonadeConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveLemonadeConfigAsync(ct);
    }

    public async Task RefreshLemonadeModelsFromServerAsync(
        HttpClient http,
        string? baseUrl = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _lemonadeConfig = _lemonadeConfig with
            {
                BaseUrl = LemonadeModelCatalogResolver.NormalizeBaseUrl(baseUrl)
            };
        }

        if (apiKey != null)
        {
            _lemonadeConfig = _lemonadeConfig with
            {
                ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim()
            };
        }

        try
        {
            var discovered = await LemonadeModelCatalogResolver.FetchModelsAsync(
                http, LemonadeBaseUrl, LemonadeApiKey, showAll: false, ct);

            var existingMap = GetLemonadeModelSettingsMap();
            var next = new Dictionary<string, LemonadeModelSettings>(StringComparer.OrdinalIgnoreCase);

            // Replace the list with what this server reports (do not keep models from a previous Base URL).
            // User overrides for models that still exist are preserved via ResolveSettings.
            foreach (var model in discovered)
            {
                existingMap.TryGetValue(model.Name, out var existing);
                next[model.Name] = LemonadeModelCatalogResolver.ResolveSettings(model, existing);
            }

            var list = next.Values.ToList();
            _lemonadeConfig = _lemonadeConfig with
            {
                Models = next.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                ModelSettings = next,
                DefaultImageModel = LemonadeModelCatalogResolver.PickDefault(
                    _lemonadeConfig.DefaultImageModel, list, m => m.IsImage),
                DefaultEditModel = LemonadeModelCatalogResolver.PickDefault(
                    _lemonadeConfig.DefaultEditModel, list, m => m.IsEdit),
                DefaultTtsModel = LemonadeModelCatalogResolver.PickDefault(
                    _lemonadeConfig.DefaultTtsModel, list, m => m.IsTts),
                DefaultSttModel = LemonadeModelCatalogResolver.PickDefault(
                    _lemonadeConfig.DefaultSttModel, list, m => m.IsTranscription),
                DefaultVoice = string.IsNullOrWhiteSpace(_lemonadeConfig.DefaultVoice)
                    ? "shimmer"
                    : _lemonadeConfig.DefaultVoice
            };
            await SaveLemonadeConfigAsync(ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to refresh Lemonade models from {LemonadeBaseUrl}: {ex.Message}", ex);
        }
    }

    private async Task SaveLemonadeConfigAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_lemonadeConfig);
        await SetItemAsync(LemonadeConfigKey, json, ct);
    }

    private Dictionary<string, LemonadeModelSettings> GetLemonadeModelSettingsMap()
    {
        if (_lemonadeConfig.ModelSettings is { Count: > 0 })
            return new Dictionary<string, LemonadeModelSettings>(_lemonadeConfig.ModelSettings, StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, LemonadeModelSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _lemonadeConfig.Models ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            map[name.Trim()] = LemonadeModelCatalogResolver.CreateDefaultSettings(name.Trim());
        }

        return map;
    }

    private static LemonadeConfig MigrateLemonadeConfig(LemonadeConfig loaded)
    {
        var baseUrl = LemonadeModelCatalogResolver.NormalizeBaseUrl(loaded.BaseUrl);
        if (loaded.ModelSettings is { Count: > 0 })
            return loaded with { BaseUrl = baseUrl };

        if (loaded.Models is not { Count: > 0 })
            return loaded with
            {
                BaseUrl = baseUrl,
                ModelSettings = new Dictionary<string, LemonadeModelSettings>(StringComparer.OrdinalIgnoreCase)
            };

        var map = new Dictionary<string, LemonadeModelSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in loaded.Models.Where(m => !string.IsNullOrWhiteSpace(m)))
        {
            var trimmed = name.Trim();
            map[trimmed] = LemonadeModelCatalogResolver.CreateDefaultSettings(trimmed);
        }

        return loaded with { BaseUrl = baseUrl, ModelSettings = map };
    }

    private async Task ClearLemonadeVisionProxiesAsync(CancellationToken ct)
    {
        var map = GetLemonadeModelSettingsMap();
        var changed = false;
        foreach (var key in map.Keys.ToList())
        {
            if (map[key].IsVisionProxy)
            {
                map[key].IsVisionProxy = false;
                changed = true;
            }
        }

        if (!changed)
            return;

        _lemonadeConfig = _lemonadeConfig with { ModelSettings = map };
        await SaveLemonadeConfigAsync(ct);
    }

    private async Task ClearOllamaVisionProxiesAsync(CancellationToken ct)
    {
        var map = GetModelSettingsMap();
        var changed = false;
        foreach (var key in map.Keys.ToList())
        {
            if (map[key].IsVisionProxy)
            {
                map[key].IsVisionProxy = false;
                changed = true;
            }
        }

        if (!changed)
            return;

        _ollamaConfig = _ollamaConfig with { ModelSettings = map };
        await SaveOllamaConfigAsync(ct);
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
        await SetItemAsync(McpEnabledKey, json, ct);
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
        await SetItemAsync(McpTokensKey, json, ct);
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
        await SetItemAsync(McpCustomConnectorsKey, json, ct);
    }

    private async Task SaveMcpTokensAsync(CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(_mcpTokens);
        await SetItemAsync(McpTokensKey, json, ct);
    }

    public IReadOnlyList<OAuthConnectorInstall> GetOAuthConnectors() =>
        _oauthConnectors.OrderBy(c => c.ConnectorId, StringComparer.OrdinalIgnoreCase).ToList();

    public OAuthConnectorInstall? GetOAuthConnector(string connectorId)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return null;
        return _oauthConnectors.FirstOrDefault(c =>
            c.ConnectorId.Equals(connectorId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public OAuthTokenSet? GetOAuthTokens(string connectorId) =>
        GetOAuthConnector(connectorId)?.Tokens;

    public async Task UpsertOAuthConnectorAsync(OAuthConnectorInstall install, CancellationToken ct = default)
    {
        if (install is null || string.IsNullOrWhiteSpace(install.ConnectorId))
            return;

        var id = install.ConnectorId.Trim();
        var label = install.AccountLabel
            ?? install.Tokens?.AccountLabel;
        var normalized = install with
        {
            ConnectorId = id,
            AccountLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
        };

        _oauthConnectors.RemoveAll(c => c.ConnectorId.Equals(id, StringComparison.OrdinalIgnoreCase));
        _oauthConnectors.Add(normalized);
        await SaveOAuthConnectorsAsync(ct);
    }

    public async Task SetOAuthConnectorEnabledAsync(string connectorId, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return;
        var id = connectorId.Trim();
        var idx = _oauthConnectors.FindIndex(c => c.ConnectorId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        _oauthConnectors[idx] = _oauthConnectors[idx] with { Enabled = enabled };
        await SaveOAuthConnectorsAsync(ct);
    }

    public async Task RemoveOAuthConnectorAsync(string connectorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connectorId)) return;
        var removed = _oauthConnectors.RemoveAll(c =>
            c.ConnectorId.Equals(connectorId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            await SaveOAuthConnectorsAsync(ct);
    }

    public async Task ReplaceOAuthConnectorsAsync(IEnumerable<OAuthConnectorInstall> installs, CancellationToken ct = default)
    {
        _oauthConnectors = (installs ?? Array.Empty<OAuthConnectorInstall>())
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.ConnectorId))
            .Select(i =>
            {
                var label = i.AccountLabel ?? i.Tokens?.AccountLabel;
                return i with
                {
                    ConnectorId = i.ConnectorId.Trim(),
                    AccountLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim()
                };
            })
            .GroupBy(i => i.ConnectorId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
        await SaveOAuthConnectorsAsync(ct);
    }

    private async Task SaveOAuthConnectorsAsync(CancellationToken ct)
    {
        var stored = new List<OAuthConnectorStoredDto>();
        foreach (var install in _oauthConnectors)
        {
            string? tokensField = null;
            if (install.Tokens is not null)
            {
                var plain = JsonSerializer.Serialize(install.Tokens);
                tokensField = await EncryptSecretAsync(plain, ct);
            }

            stored.Add(new OAuthConnectorStoredDto
            {
                ConnectorId = install.ConnectorId,
                Enabled = install.Enabled,
                ConnectedAtUtc = install.ConnectedAtUtc,
                AccountLabel = install.AccountLabel ?? install.Tokens?.AccountLabel,
                Tokens = tokensField
            });
        }

        await SetItemAsync(OAuthConnectorsKey, JsonSerializer.Serialize(stored), ct);
    }

    private async Task<List<OAuthConnectorInstall>> DeserializeOAuthConnectorsAsync(string json, CancellationToken ct)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<List<OAuthConnectorStoredDto>>(json);
            if (stored is null) return new();

            var result = new List<OAuthConnectorInstall>();
            foreach (var s in stored)
            {
                if (string.IsNullOrWhiteSpace(s.ConnectorId)) continue;
                OAuthTokenSet? tokens = null;
                if (!string.IsNullOrWhiteSpace(s.Tokens))
                {
                    var plain = await DecryptSecretAsync(s.Tokens, ct);
                    if (!string.IsNullOrWhiteSpace(plain))
                    {
                        try
                        {
                            tokens = JsonSerializer.Deserialize<OAuthTokenSet>(plain);
                        }
                        catch { /* ignore bad token blob */ }
                    }
                }

                var label = s.AccountLabel ?? tokens?.AccountLabel;
                result.Add(new OAuthConnectorInstall(
                    s.ConnectorId.Trim(),
                    s.Enabled,
                    tokens,
                    s.ConnectedAtUtc,
                    string.IsNullOrWhiteSpace(label) ? null : label.Trim()));
            }

            return result;
        }
        catch
        {
            return new();
        }
    }

    private async Task<string> EncryptSecretAsync(string plaintext, CancellationToken ct)
    {
        var key = _auth.LocalEncryptionKeyB64;
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(plaintext))
            return plaintext;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var crypto = scope.ServiceProvider.GetService<ICryptoService>();
            if (crypto is null) return plaintext;
            var cipher = await crypto.EncryptAsync(key, plaintext, ct);
            if (string.IsNullOrEmpty(cipher) || cipher == plaintext)
                return plaintext;
            return EncPrefix + cipher;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmKeyStore] OAuth encrypt failed, storing plaintext: {ex.Message}");
            return plaintext;
        }
    }

    private async Task<string> DecryptSecretAsync(string stored, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        if (!stored.StartsWith(EncPrefix, StringComparison.Ordinal))
            return stored;

        var cipher = stored[EncPrefix.Length..];
        var key = _auth.LocalEncryptionKeyB64;
        if (string.IsNullOrEmpty(key))
            return stored; // cannot decrypt yet

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var crypto = scope.ServiceProvider.GetService<ICryptoService>();
            if (crypto is null) return stored;
            return await crypto.DecryptAsync(key, cipher, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmKeyStore] OAuth decrypt failed: {ex.Message}");
            return stored;
        }
    }

    private sealed class OAuthConnectorStoredDto
    {
        public string ConnectorId { get; set; } = "";
        public bool Enabled { get; set; }
        public DateTimeOffset? ConnectedAtUtc { get; set; }
        public string? AccountLabel { get; set; }
        /// <summary>Plain JSON of OAuthTokenSet, or enc: + AES-GCM ciphertext.</summary>
        public string? Tokens { get; set; }
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
        await SetItemAsync(HomeAssistantConfigKey, json, ct);
    }

    public async Task UpdateHomeAssistantDeviceSummaryAsync(string summary, CancellationToken ct = default)
    {
        _homeAssistantConfig.CachedDeviceSummary = summary ?? "";
        _homeAssistantConfig.DeviceSummaryUpdatedAt = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(_homeAssistantConfig);
        await SetItemAsync(HomeAssistantConfigKey, json, ct);
    }
}