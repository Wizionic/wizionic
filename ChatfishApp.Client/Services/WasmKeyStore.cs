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

    public record OllamaConfig(string BaseUrl = "http://localhost:11434", List<string>? Models = null);

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
                _ollamaConfig = loaded;
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

    public List<string> OllamaModels => _ollamaConfig.Models ?? new List<string>();

    public async Task SetOllamaBaseUrlAsync(IJSRuntime js, string baseUrl)
    {
        _ollamaConfig = new OllamaConfig(baseUrl.Trim(), _ollamaConfig.Models);
        await SaveOllamaConfigAsync(js);
    }

    public async Task SetOllamaModelsAsync(IJSRuntime js, List<string> models)
    {
        _ollamaConfig = new OllamaConfig(_ollamaConfig.BaseUrl, models.Distinct().ToList());
        await SaveOllamaConfigAsync(js);
    }

    public async Task AddOllamaModelAsync(IJSRuntime js, string model)
    {
        var models = new List<string>(OllamaModels);
        if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model.Trim()))
        {
            models.Add(model.Trim());
            await SetOllamaModelsAsync(js, models);
        }
    }

    public async Task RemoveOllamaModelAsync(IJSRuntime js, string model)
    {
        var models = new List<string>(OllamaModels);
        models.Remove(model);
        await SetOllamaModelsAsync(js, models);
    }

    private async Task SaveOllamaConfigAsync(IJSRuntime js)
    {
        var json = JsonSerializer.Serialize(_ollamaConfig);
        await js.InvokeVoidAsync("localStorage.setItem", OllamaConfigKey, json);
    }

    public async Task RefreshOllamaModelsFromServerAsync(IJSRuntime js, HttpClient http)
    {
        try
        {
            var url = OllamaBaseUrl.TrimEnd('/') + "/api/tags";
            var resp = await http.GetFromJsonAsync<OllamaTagsResponse>(url);
            if (resp?.models != null)
            {
                var models = resp.models.Select(m => m.name).Distinct().ToList();
                await SetOllamaModelsAsync(js, models);
            }
        }
        catch (Exception ex)
        {
            // Let caller handle (e.g. show alert in UI)
            throw new InvalidOperationException($"Failed to refresh Ollama models from {OllamaBaseUrl}: {ex.Message}", ex);
        }
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
            await SaveMcpTokensAsync(js);  // small helper we'll add
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
