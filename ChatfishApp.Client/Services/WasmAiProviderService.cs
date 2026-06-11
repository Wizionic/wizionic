using ChatfishApp.Contracts;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ChatfishApp.Client.Services;

/// <summary>
/// WASM/client-side equivalent of the server's AiProviderService.
/// Creates configured IChatClient instances using keys from local storage (via WasmKeyStore)
/// and the shared ProviderCatalog.
/// This lets the WASM chat use the same pluggable AI abstractions as the server
/// for future agentic/tool-calling support.
/// </summary>
public class WasmAiProviderService
{
    private readonly WasmKeyStore _keyStore;

    public WasmAiProviderService(WasmKeyStore keyStore)
    {
        _keyStore = keyStore;
    }

    /// <summary>
    /// Returns a configured IChatClient for the given model using locally stored keys.
    /// For Ollama: uses the configured base URL.
    /// For cloud providers: uses the user's key + catalog base URL (OpenAI-compatible).
    /// </summary>
    public IChatClient GetChatClientForModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            throw new ArgumentException("Model ID is required.", nameof(modelId));

        if (modelId.StartsWith("ollama/"))
        {
            var ollamaModel = modelId.Split('/', 2)[1];
            var baseUrl = _keyStore.OllamaBaseUrl.TrimEnd('/') + "/v1/";
            // Use OpenAI-compatible client pointed at local Ollama (or remote if base URL changed)
            var credential = new ApiKeyCredential("ollama"); // Ollama /v1 often ignores key or accepts any
            var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            var rootClient = new OpenAIClient(credential, options);
            var chatClient = rootClient.GetChatClient(ollamaModel);
            return chatClient.AsIChatClient();
        }
        else
        {
            var entry = ProviderCatalog.GetModel(modelId);
        if (entry == null)
            throw new InvalidOperationException($"Unknown model '{modelId}' in ProviderCatalog.");

        var (provider, modelDef) = entry.Value;

        var apiKey = _keyStore.GetKey(provider.Id);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No API key configured for provider '{provider.DisplayName}'. Add one in Settings.");

        var baseUrl = provider.BaseUrl?.TrimEnd('/') + "/";
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };

        var rootClient = new OpenAIClient(credential, options);
        var chatClient = rootClient.GetChatClient(modelDef.Id);  // use the catalog's model id

        // OpenRouter attribution headers (recommended). In full M.E.AI pipeline this would be
        // injected via custom transport/policy. For now we rely on the caller or SDK defaults.
        // (The manual SendAsync path in older code added them explicitly.)
        if (provider.Id == "openrouter")
        {
            // Note: When using the full IChatClient pipeline these headers should be added
            // at the transport level. For the initial port we keep behavior similar.
        }

        return chatClient.AsIChatClient();
        }
    }

    /// <summary>
    /// Models available based on what the user has configured locally (keys present for cloud, or any for Ollama).
    /// </summary>
    public List<ModelInfo> GetAvailableModels()
    {
        var result = new List<ModelInfo>();

        // Always offer Ollama models the user has configured (or a minimal safe default).
        // The chat page will always coerce the initial selection to something that actually exists in this list.
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

        return result;
    }

    public record ModelInfo(string Id, string Label, string Icon, string ProviderId, string ProviderName, bool SupportsTools = true, bool SupportsVision = false);
}
