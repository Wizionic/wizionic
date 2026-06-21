using ChatfishApp.Contracts;
using ChatfishApp.Core.Chat;
using ChatfishApp.Core.Storage;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;
using System.Net.Http.Json;

namespace ChatfishApp.Shared.Services;

/// <summary>
/// Client-side model catalog: local keys, Ollama config, and server-proxied providers.
/// </summary>
public sealed class ChatModelCatalogService : IChatModelCatalog
{
    private readonly IKeyStore _keyStore;
    private readonly HttpClient _proxyHttp;
    private List<ProxiedProviderContracts.ProxiedProviderDto> _proxiedProviders = new();

    public ChatModelCatalogService(IKeyStore keyStore, HttpClient proxyHttp)
    {
        _keyStore = keyStore;
        _proxyHttp = proxyHttp;
    }

    public IReadOnlyList<ProxiedProviderContracts.ProxiedProviderDto> ProxiedProviders => _proxiedProviders;

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
            return new ServerProxyChatClient(_proxyHttp, provider.Id, model.Id);
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

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            var response = await _proxyHttp.GetFromJsonAsync<ProxiedProviderContracts.ProxyProvidersResponse>(
                "/api/proxy/providers", timeoutCts.Token);
            _proxiedProviders = response?.Providers ?? new List<ProxiedProviderContracts.ProxiedProviderDto>();
            var modelCount = _proxiedProviders.Sum(p => p.Models.Count);
            Console.WriteLine($"[ChatModelCatalog] Loaded {_proxiedProviders.Count} proxied provider(s), {modelCount} model(s).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ChatModelCatalog] Failed to load proxied providers: {ex.Message}");
            _proxiedProviders = new List<ProxiedProviderContracts.ProxiedProviderDto>();
        }
    }

    public List<ChatModelInfo> GetAvailableModels()
    {
        var result = new List<ChatModelInfo>();

        foreach (var settings in _keyStore.OllamaModelSettingsList)
        {
            var label = string.IsNullOrWhiteSpace(settings.Label) ? settings.Name : settings.Label;
            result.Add(new ChatModelInfo(
                $"ollama/{settings.Name}",
                $"{label} (Ollama)",
                "🦙",
                "ollama",
                "Ollama",
                SupportsTools: settings.SupportsTools,
                SupportsVision: settings.SupportsVision,
                ContextSize: settings.ContextSize));
        }

        foreach (var provider in ProviderCatalog.Providers)
        {
            var key = _keyStore.GetKey(provider.Id);
            if (!string.IsNullOrWhiteSpace(key))
            {
                foreach (var m in provider.Models)
                {
                    result.Add(new ChatModelInfo(
                        m.Id, m.Label, m.Icon, provider.Id, provider.DisplayName,
                        SupportsTools: m.SupportsTools, SupportsVision: m.SupportsVision));
                }
            }
        }

        foreach (var provider in _proxiedProviders)
        {
            var isOllamaBackend = string.Equals(provider.Type, "Ollama", StringComparison.OrdinalIgnoreCase);
            foreach (var m in provider.Models)
            {
                result.Add(new ChatModelInfo(
                    m.Id, m.Label, m.Icon, provider.Id, provider.DisplayName,
                    SupportsTools: m.SupportsTools,
                    SupportsVision: m.SupportsVision,
                    IsOllamaBackend: isOllamaBackend,
                    VisionProxyModelId: provider.VisionProxyModelId));
            }
        }

        return result;
    }

    public string? GetConfiguredDefaultModelId(IReadOnlyList<ChatModelInfo> availableModels)
    {
        foreach (var provider in _proxiedProviders)
        {
            if (string.IsNullOrWhiteSpace(provider.DefaultModel))
                continue;

            var defaultId = provider.DefaultModel.Trim();
            if (availableModels.Any(m => string.Equals(m.Id, defaultId, StringComparison.OrdinalIgnoreCase)))
                return defaultId;
        }

        return null;
    }

    public string? GetProxiedVisionProxyModelId(string modelId)
    {
        var match = TryGetProxiedModel(modelId);
        return match?.Provider.VisionProxyModelId;
    }

    public bool IsProxiedModel(string modelId) => TryGetProxiedModel(modelId).HasValue;

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
}