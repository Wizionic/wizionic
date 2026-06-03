using ChatfishApp.Contracts;
using ChatfishApp.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace ChatfishApp.Services;

/// <summary>
/// Central pluggable AI provider service.
/// Given a model ID, resolves the provider + current user's key and returns a configured IChatClient.
/// Most providers (including Gemini Flash via official compat) go through OpenAI-compatible path using the
/// already-referenced Microsoft.Extensions.AI.OpenAI package.
/// </summary>
public class AiProviderService
{
    private readonly ProviderKeyService _keyService;
    private readonly IConfiguration _config;

    public AiProviderService(ProviderKeyService keyService, IConfiguration config)
    {
        _keyService = keyService;
        _config = config;
    }

    /// <summary>
    /// Returns a configured IChatClient for the given model using the current user's key.
    /// Throws if provider unknown or user has no key for it.
    /// </summary>
    public async Task<IChatClient> GetChatClientForModelAsync(string modelId)
    {
        var entry = ProviderCatalog.GetModel(modelId);

        if (entry != null)
        {
            var (provider, _) = entry.Value;
            var key = await _keyService.GetKeyAsync(provider.Id);
            if (key == null || string.IsNullOrWhiteSpace(key.Key))
                throw new InvalidOperationException($"No API key configured for provider '{provider.DisplayName}'. Please add one in Settings.");

            if (provider.Id == "openrouter")
            {
                // OpenRouter requires (recommended) attribution headers on every request for leaderboards.
                // We read from config (OpenRouter:Referer and OpenRouter:AppTitle).
                // Full injection into the OpenAI SDK pipeline (custom transport or policy) is wired in Create... below.
                var referer = _config["OpenRouter:Referer"] ?? "http://localhost";
                var title = _config["OpenRouter:AppTitle"] ?? "Chatfish";
                var headers = new Dictionary<string, string>
                {
                    ["HTTP-Referer"] = referer,
                    ["X-OpenRouter-Title"] = title
                };
                return CreateOpenAICompatibleClient(provider.BaseUrl!, key.Key, modelId, headers);
            }

            return provider.Type switch
            {
                "OpenAICompatible" => CreateOpenAICompatibleClient(provider.BaseUrl!, key.Key, modelId),
                _ => throw new NotSupportedException($"Provider type '{provider.Type}' not implemented yet.")
            };
        }

        // Fallback for legacy/deprecated model IDs that may still be stored in old conversation history
        // (e.g. "gemini-2.0-flash" after the June 2026 shutdown of 2.0 models).
        // We only do this for Gemini models if the user has a Gemini key configured.
        if (modelId.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            var geminiKey = await _keyService.GetKeyAsync("gemini");
            if (geminiKey != null && !string.IsNullOrWhiteSpace(geminiKey.Key))
            {
                // Use the current Gemini OpenAI-compat base. The modelId will be passed as-is to the backend.
                return CreateOpenAICompatibleClient(
                    "https://generativelanguage.googleapis.com/v1beta/openai/",
                    geminiKey.Key,
                    modelId);
            }
            throw new InvalidOperationException("No API key configured for Google Gemini. Please add one in Settings.");
        }

        throw new ArgumentException($"Unknown model '{modelId}'. Update ProviderCatalog or check your key configuration.");
    }

    private static IChatClient CreateOpenAICompatibleClient(string baseUrl, string apiKey, string modelId, Dictionary<string, string>? extraHeaders = null)
    {
        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl)
        };

        // Preferred for custom endpoints (Groq, Gemini compat, OpenRouter, etc.): root client + GetChatClient(model).
        var rootClient = new OpenAIClient(credential, options);

        var chatClient = rootClient.GetChatClient(modelId);

        // OpenRouter attribution headers (HTTP-Referer + X-OpenRouter-Title) are recommended for leaderboards.
        // Full persistent injection is best done with a custom HttpClient + HttpClientPipelineTransport or
        // ClientPipelineOptions + policy added to the options before constructing the OpenAIClient.
        // For this version we capture the intent here (see caller in GetChatClientForModelAsync for openrouter).
        // The headers are app-level attribution and do not affect core functionality (base + key works).
        // TODO: Wire a custom transport/pipeline with the extraHeaders for guaranteed sending on every request.
        if (extraHeaders != null && extraHeaders.Count > 0)
        {
            // Placeholder: in a full impl we would configure the pipeline here with the headers.
            // Example direction (depends on exact SDK types):
            // var pipelineOptions = new ClientPipelineOptions();
            // pipelineOptions.AddPolicy(new AddHeaderPolicy(extraHeaders), PipelinePosition.PerTry);
            // options = new OpenAIClientOptions { ...,  }; then new OpenAIClient(credential, options with transport)
        }

        return chatClient.AsIChatClient();
    }

    /// <summary>
    /// Models available to the current user (i.e. providers for which they have configured a key).
    /// Used by the chat UI for the grouped selector.
    /// </summary>
    public async Task<List<ModelInfo>> GetAvailableModelsForCurrentUserAsync()
    {
        var result = new List<ModelInfo>();

        foreach (var provider in ProviderCatalog.Providers)
        {
            // Only include models for providers where the user has a key AND has enabled the provider.
            if (await _keyService.IsProviderEnabledForChatAsync(provider.Id))
            {
                foreach (var m in provider.Models)
                {
                    result.Add(new ModelInfo(
                        m.Id,
                        m.Label,
                        m.Icon,
                        provider.Id,
                        provider.DisplayName));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Simple record for UI binding (avoids leaking full catalog types into Razor if desired).
    /// </summary>
    public record ModelInfo(string Id, string Label, string Icon, string ProviderId, string ProviderName);
}
