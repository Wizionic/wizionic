using ChatfishApp.Contracts;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Net.Http.Json;

namespace ChatfishApp.Client.Services;

/// <summary>
/// WASM/client-side equivalent of the server's AiProviderService.
/// Creates configured IChatClient instances using keys from local storage (via WasmKeyStore)
/// for CORS-friendly providers, or the server AI proxy for CORS-restricted providers.
/// </summary>
public class WasmAiProviderService
{
    /// <summary>
    /// HTTP client for same-origin /api/proxy/* calls. Set from Client/Program.cs at startup.
    /// </summary>
    internal static HttpClient? ProxyHttp { get; set; }

    private readonly WasmKeyStore _keyStore;
    private List<ProxiedProviderContracts.ProxiedProviderDto> _proxiedProviders = new();

    public WasmAiProviderService(WasmKeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    public IReadOnlyList<ProxiedProviderContracts.ProxiedProviderDto> ProxiedProviders => _proxiedProviders;

    /// <summary>
    /// Loads proxied provider/model definitions from the server (configured in appsettings).
    /// </summary>
    public async Task RefreshProxiedProvidersAsync(CancellationToken ct = default)
    {
        var http = ProxyHttp;
        if (http == null)
        {
            Console.WriteLine("[WasmAiProviderService] ProxyHttp is not configured.");
            _proxiedProviders = new List<ProxiedProviderContracts.ProxiedProviderDto>();
            return;
        }

        try
        {
            var response = await http.GetFromJsonAsync<ProxiedProviderContracts.ProxyProvidersResponse>(
                "/api/proxy/providers", ct);
            _proxiedProviders = response?.Providers ?? new List<ProxiedProviderContracts.ProxiedProviderDto>();
            int modelCount = _proxiedProviders.Sum(p => p.Models.Count);
            Console.WriteLine($"[WasmAiProviderService] Loaded {_proxiedProviders.Count} proxied provider(s), {modelCount} model(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmAiProviderService] Failed to load proxied providers: {ex.Message}");
            _proxiedProviders = new List<ProxiedProviderContracts.ProxiedProviderDto>();
        }
    }

    /// <summary>
    /// Returns a configured IChatClient for the given model.
    /// Ollama and CORS-friendly cloud providers call the provider API directly.
    /// CORS-restricted providers route through POST /api/proxy/chat on the backend.
    /// </summary>
    public IChatClient GetChatClientForModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID is required.", nameof(modelId));

        if (modelId.StartsWith("ollama/"))
        {
            var ollamaModel = modelId.Split('/', 2)[1];
            var baseUrl = _keyStore.OllamaBaseUrl.TrimEnd('/') + "/v1/";
            var credential = new ApiKeyCredential("ollama");
            var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var rootClient = new OpenAIClient(credential, options);
            var chatClient = rootClient.GetChatClient(ollamaModel);
            return chatClient.AsIChatClient();
        }

        var proxied = TryGetProxiedModel(modelId);
        if (proxied.HasValue)
        {
            var (provider, model) = proxied.Value;
            return CreateProxyChatClient(provider.Id, model.Id);
        }

        var entry = ProviderCatalog.GetModel(modelId);
        if (entry == null)
            throw new InvalidOperationException($"Unknown model '{modelId}'.");

        var (catalogProvider, modelDef) = entry.Value;

        var apiKey = _keyStore.GetKey(catalogProvider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key configured for provider '{catalogProvider.DisplayName}'. Add one in Settings.");

        var providerBaseUrl = catalogProvider.BaseUrl?.TrimEnd('/') + "/";
        var credential2 = new ApiKeyCredential(apiKey);
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(providerBaseUrl) };

        var rootClient2 = new OpenAIClient(credential2, clientOptions);
        var directClient = rootClient2.GetChatClient(modelDef.Id);
        return directClient.AsIChatClient();
    }

    /// <summary>
    /// Models available based on local keys (cloud), Ollama config, and server-proxied providers.
    /// </summary>
    public List<ModelInfo> GetAvailableModels()
    {
        var result = new List<ModelInfo>();

        var ollamaModels = _keyStore.OllamaModels.Any() ? _keyStore.OllamaModels : new List<string> { "gemma2", "llama3.2" };
        foreach (var m in ollamaModels.Distinct())
        {
            var caps = ProviderCatalog.GetCapabilitiesForModel($"ollama/{m}");
            result.Add(new ModelInfo($"ollama/{m}", $"{m} (Ollama)", "🦙", "ollama", "Ollama", SupportsTools: caps.SupportsTools, SupportsVision: caps.SupportsVision));
        }

        foreach (var provider in ProviderCatalog.Providers)
        {
            var key = _keyStore.GetKey(provider.Id);
            if (!string.IsNullOrWhiteSpace(key))
            {
                foreach (var m in provider.Models)
                {
                    result.Add(new ModelInfo(m.Id, m.Label, m.Icon, provider.Id, provider.DisplayName, SupportsTools: m.SupportsTools, SupportsVision: m.SupportsVision));
                }
            }
        }

        foreach (var provider in _proxiedProviders)
        {
            bool isOllamaBackend = string.Equals(provider.Type, "Ollama", StringComparison.OrdinalIgnoreCase);
            foreach (var m in provider.Models)
            {
                result.Add(new ModelInfo(
                    m.Id, m.Label, m.Icon, provider.Id, provider.DisplayName,
                    SupportsTools: m.SupportsTools,
                    SupportsVision: m.SupportsVision,
                    IsOllamaBackend: isOllamaBackend));
            }
        }

        return result;
    }

    public record ModelInfo(
        string Id,
        string Label,
        string Icon,
        string ProviderId,
        string ProviderName,
        bool SupportsTools = true,
        bool SupportsVision = false,
        bool IsOllamaBackend = false);

    private (ProxiedProviderContracts.ProxiedProviderDto Provider, ProxiedProviderContracts.ProxiedModelDto Model)? TryGetProxiedModel(string modelId)
    {
        foreach (var provider in _proxiedProviders)
        {
            var model = provider.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
            if (model != null)
                return (provider, model);
        }

        return null;
    }

    private IChatClient CreateProxyChatClient(string providerId, string modelId) =>
        new ServerProxyChatClient(providerId, modelId);
}