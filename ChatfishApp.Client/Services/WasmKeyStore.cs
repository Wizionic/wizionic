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

    private Dictionary<string, string> _providerKeys = new();
    private OllamaConfig _ollamaConfig = new();

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
}
