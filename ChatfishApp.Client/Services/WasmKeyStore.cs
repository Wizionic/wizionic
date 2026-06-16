using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Text.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side store for provider API keys and Ollama configuration.
/// Persists everything to browser localStorage (matching the local-first WASM target).
/// Mirrors the spirit of the server's ProviderKeyService but without auth/DB.
/// </summary>
public class WasmKeyStore
{
    private const string KeysStorageKey = "wasm-provider-keys";
    private const string OllamaConfigKey = "wasm-ollama-config";
    private const string McpEnabledKey = "wasm-mcp-enabled-servers";
    private const string McpTokensKey = "wasm-mcp-tokens";
    private const string McpCustomConnectorsKey = "wasm-mcp-custom-connectors";
    private const string LastSelectedModelKey = "wasm-last-selected-model";

    private Dictionary<string, string> _providerKeys = new();
    private string _lastSelectedModel = "";
    private OllamaConfig _ollamaConfig = new();
    private HashSet<string> _enabledMcpServers = new();
    private Dictionary<string, string> _mcpTokens = new();
    private List<CustomMcpConnector> _customMcpConnectors = new();

    public record OllamaConfig(
        string BaseUrl = "http://localhost:11434",
        List<string>? Models = null,
        Dictionary<string, OllamaModelSettings>? ModelSettings = null);

    public async Task LoadAsync(IJSRuntime js)
    {
        // Load provider keys
        var keysJson = await js.InvokeAsync<string>("localStorage.getItem", KeysStorageKey);
        if (!string.IsNullOrEmpty(keysJson))
        {
            _providerKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(keysJson) ?? new();
        }

        // Load Ollama config
        var ollamaJson = await js.InvokeAsync<string>("localStorage.getItem", OllamaConfigKey);
        if (!string.IsNullOrEmpty(ollamaJson))
        {
            var loaded = JsonSerializer.Deserialize<OllamaConfig>(ollamaJson);
            if (loaded != null)
            {
                _ollamaConfig = MigrateOllamaConfig(loaded);
            }
        }

        // Load enabled MCP server names (for Tools page selection)
        var mcpJson = await js.InvokeAsync<string>("localStorage.getItem", McpEnabledKey);
        if (!string.IsNullOrEmpty(mcpJson))
        {
            var names = JsonSerializer.Deserialize<List<string>>(mcpJson);
            if (names != null)
            {
                _enabledMcpServers = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            }
        }

        // Load per-MCP tokens (for authenticated remote MCP servers)
        var tokensJson = await js.InvokeAsync<string>("localStorage.getItem", McpTokensKey);
        if (!string.IsNullOrEmpty(tokensJson))
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(tokensJson);
            if (loaded != null)
            {
                _mcpTokens = loaded;
            }
        }

        // Load custom MCP connectors (user-added via "Custom Connector" dialog, not from the public registry)
        var customsJson = await js.InvokeAsync<string>("localStorage.getItem", McpCustomConnectorsKey);
        if (!string.IsNullOrEmpty(customsJson))
        {
            var loaded = JsonSerializer.Deserialize<List<CustomMcpConnector>>(customsJson);
            if (loaded != null)
            {
                _customMcpConnectors = loaded;
            }
        }

        _lastSelectedModel = await js.InvokeAsync<string?>("localStorage.getItem", LastSelectedModelKey) ?? "";
    }

    public string LastSelectedModel => _lastSelectedModel;

    public async Task SetLastSelectedModelAsync(IJSRuntime js, string modelId)
    {
        _lastSelectedModel = (modelId ?? "").Trim();
        if (string.IsNullOrEmpty(_lastSelectedModel))
            await js.InvokeVoidAsync("localStorage.removeItem", LastSelectedModelKey);
        else
            await js.InvokeVoidAsync("localStorage.setItem", LastSelectedModelKey, _lastSelectedModel);
    }

    public string GetKey(string providerId) =>
        _providerKeys.GetValueOrDefault(providerId, "");

    public async Task SetKeyAsync(IJSRuntime js, string providerId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            _providerKeys.Remove(providerId);
        else
            _providerKeys[providerId] = key.Trim();

        var json = JsonSerializer.Serialize(_providerKeys);
        await js.InvokeVoidAsync("localStorage.setItem", KeysStorageKey, json);
    }

    public async Task SaveAllKeysAsync(IJSRuntime js, string groq, string gemini, string openrouter)
    {
        _providerKeys["groq"] = (groq ?? "").Trim();
        _providerKeys["gemini"] = (gemini ?? "").Trim();
        _providerKeys["openrouter"] = (openrouter ?? "").Trim();

        var json = JsonSerializer.Serialize(_providerKeys);
        await js.InvokeVoidAsync("localStorage.setItem", KeysStorageKey, json);
    }

    public string OllamaBaseUrl => _ollamaConfig.BaseUrl ?? "http://localhost:11434";

    public string OllamaChatEndpoint => OllamaBaseUrl.TrimEnd('/') + "/v1/chat/completions";

    public List<string> OllamaModels => GetModelSettingsMap().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

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

    /// <summary>
    /// Ollama model name marked as the vision proxy (must also have <see cref="OllamaModelSettings.SupportsVision"/>).
    /// </summary>
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

    public async Task SetOllamaBaseUrlAsync(IJSRuntime js, string baseUrl)
    {
        _ollamaConfig = _ollamaConfig with { BaseUrl = baseUrl.Trim() };
        await SaveOllamaConfigAsync(js);
    }

    public async Task SetOllamaModelsAsync(IJSRuntime js, List<string> models)
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
        await SaveOllamaConfigAsync(js);
    }

    public async Task AddOllamaModelAsync(IJSRuntime js, string model, HttpClient? http = null)
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
            var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, OllamaBaseUrl, model);
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
        await SaveOllamaConfigAsync(js);
    }

    public async Task RemoveOllamaModelAsync(IJSRuntime js, string model)
    {
        var map = GetModelSettingsMap();
        map.Remove(model);
        _ollamaConfig = _ollamaConfig with
        {
            Models = map.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            ModelSettings = map
        };
        await SaveOllamaConfigAsync(js);
    }

    public async Task SaveOllamaModelSettingsAsync(IJSRuntime js, OllamaModelSettings settings)
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
        await SaveOllamaConfigAsync(js);
    }

    private async Task SaveOllamaConfigAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_ollamaConfig);
        await js.InvokeVoidAsync("localStorage.setItem", OllamaConfigKey, json);
    }

    public async Task RefreshOllamaModelsFromServerAsync(IJSRuntime js, HttpClient http, string? baseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            _ollamaConfig = _ollamaConfig with { BaseUrl = baseUrl.Trim() };

        try
        {
            var origin = OllamaBaseUrl.TrimEnd('/');
            var tagsUrl = origin + "/api/tags";
            var resp = await http.GetFromJsonAsync<OllamaTagsResponse>(tagsUrl);
            if (resp?.models == null)
                return;

            var existingMap = GetModelSettingsMap();
            var next = new Dictionary<string, OllamaModelSettings>(StringComparer.OrdinalIgnoreCase);

            foreach (var tag in resp.models.Where(m => !string.IsNullOrWhiteSpace(m.name)).Select(m => m.name!).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                existingMap.TryGetValue(tag, out var existing);
                var live = await OllamaCapabilitiesResolver.FetchLiveMetadataAsync(http, origin, tag);
                next[tag] = OllamaCapabilitiesResolver.ResolveSettings(tag, live, existing);
            }

            _ollamaConfig = _ollamaConfig with
            {
                Models = next.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                ModelSettings = next
            };
            await SaveOllamaConfigAsync(js);
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

    // Internal response types (kept here to avoid polluting UI)
    private class OllamaTagsResponse
    {
        public List<OllamaModel> models { get; set; } = new();
    }

    private class OllamaModel
    {
        public string name { get; set; } = "";
    }

    // --- MCP remote tools (Tools.razor) ---

    public IReadOnlySet<string> EnabledMcpServerNames => _enabledMcpServers;

    public bool IsMcpServerEnabled(string name) =>
        !string.IsNullOrWhiteSpace(name) && _enabledMcpServers.Contains(name);

    public async Task SetMcpServerEnabledAsync(IJSRuntime js, string name, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        if (enabled)
            _enabledMcpServers.Add(name);
        else
            _enabledMcpServers.Remove(name);

        await SaveMcpEnabledAsync(js);
    }

    public async Task SetEnabledMcpServersAsync(IJSRuntime js, IEnumerable<string> names)
    {
        _enabledMcpServers = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        await SaveMcpEnabledAsync(js);
    }

    private async Task SaveMcpEnabledAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_enabledMcpServers.ToList());
        await js.InvokeVoidAsync("localStorage.setItem", McpEnabledKey, json);
    }

    // --- Per-MCP server tokens (for RequiresAuth servers selected on the Tools page) ---

    public string GetMcpToken(string serverName) =>
        string.IsNullOrWhiteSpace(serverName) ? "" : _mcpTokens.GetValueOrDefault(serverName, "");

    public async Task SetMcpTokenAsync(IJSRuntime js, string serverName, string token)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return;

        if (string.IsNullOrWhiteSpace(token))
            _mcpTokens.Remove(serverName);
        else
            _mcpTokens[serverName] = token.Trim();

        var json = JsonSerializer.Serialize(_mcpTokens);
        await js.InvokeVoidAsync("localStorage.setItem", McpTokensKey, json);
    }

    public IReadOnlyDictionary<string, string> GetAllMcpTokens() =>
        new Dictionary<string, string>(_mcpTokens);

    // --- Custom MCP Connectors (added via the "Add Custom Connector" dialog on Tools page) ---

    /// <summary>
    /// User-defined MCP servers (name + URL). These are persisted separately from the public registry.
    /// Tokens (if any) are stored in the same _mcpTokens dictionary, keyed by the connector Name.
    /// </summary>
    public record CustomMcpConnector(string Name, string ServerUrl);

    public IReadOnlyList<CustomMcpConnector> GetCustomConnectors() =>
        _customMcpConnectors.ToList();

    public async Task AddCustomConnectorAsync(IJSRuntime js, string name, string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(serverUrl))
            return;

        name = name.Trim();
        serverUrl = serverUrl.Trim();

        // Remove any previous entry with the same name (allow overwrite on re-add)
        _customMcpConnectors.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        _customMcpConnectors.Add(new CustomMcpConnector(name, serverUrl));

        await SaveCustomConnectorsAsync(js);
    }

    public async Task RemoveCustomConnectorAsync(IJSRuntime js, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        int removed = _customMcpConnectors.RemoveAll(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            // Also clean up enabled flag and any token for this name
            _enabledMcpServers.Remove(name.Trim());
            _mcpTokens.Remove(name.Trim());

            await SaveCustomConnectorsAsync(js);
            await SaveMcpEnabledAsync(js);
            await SaveMcpTokensAsync(js);
        }
    }

    private async Task SaveCustomConnectorsAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_customMcpConnectors);
        await js.InvokeVoidAsync("localStorage.setItem", McpCustomConnectorsKey, json);
    }

    private async Task SaveMcpTokensAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_mcpTokens);
        await js.InvokeVoidAsync("localStorage.setItem", McpTokensKey, json);
    }
}