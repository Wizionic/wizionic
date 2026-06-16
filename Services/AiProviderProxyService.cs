using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatfishApp.Contracts;
using Microsoft.Extensions.Options;

namespace ChatfishApp.Services;

/// <summary>
/// Server-side proxy for OpenAI-compatible providers that block browser-direct CORS.
/// Provider definitions and API keys come from appsettings / environment variables only.
/// </summary>
public class AiProviderProxyService
{
    private readonly AiProviderProxyOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AiProviderProxyService> _logger;

    public AiProviderProxyService(
        IOptions<AiProviderProxyOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AiProviderProxyService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ProxiedProviderContracts.ProxiedProviderDto>> GetAvailableProvidersAsync(
        CancellationToken ct = default)
    {
        var result = new List<ProxiedProviderContracts.ProxiedProviderDto>();

        foreach (var provider in _options.Proxied)
        {
            if (string.IsNullOrWhiteSpace(provider.Id) || string.IsNullOrWhiteSpace(provider.BaseUrl))
                continue;

            if (provider.HideFromModelList)
                continue;

            if (!IsOllama(provider) && !TryResolveApiKey(provider, out _))
                continue;

            var models = await ResolveProviderModelsAsync(provider, ct);
            if (models.Count == 0)
                continue;

            result.Add(new ProxiedProviderContracts.ProxiedProviderDto
            {
                Id = provider.Id,
                DisplayName = string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id : provider.DisplayName,
                Type = NormalizeType(provider.Type),
                DefaultModel = string.IsNullOrWhiteSpace(provider.DefaultModel) ? null : provider.DefaultModel.Trim(),
                VisionProxyModelId = GetVisionProxyModelId(provider),
                Models = models
            });
        }

        return result;
    }

    public async Task<string> ProxyChatAsync(ProxiedProviderContracts.ProxyChatRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId))
            throw new ArgumentException("ProviderId is required.");
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.");
        if (request.Messages is not { Count: > 0 })
            throw new ArgumentException("At least one message is required.");

        var provider = _options.Proxied.FirstOrDefault(p =>
            string.Equals(p.Id, request.ProviderId, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
            throw new InvalidOperationException($"Unknown proxied provider '{request.ProviderId}'.");

        if (!TryResolveApiKey(provider, out var apiKey))
            throw new InvalidOperationException($"No API key configured for proxied provider '{request.ProviderId}'.");

        var messages = await ApplyVisionProxyAsync(provider, request.Model, request.Messages, ct);
        return await SendChatCompletionAsync(provider, request.Model, messages, request.Tools, request.ToolChoice, apiKey, ct);
    }

    private async Task<string> SendChatCompletionAsync(
        ProxiedProviderOptions provider,
        string model,
        List<Dictionary<string, object?>> messages,
        List<Dictionary<string, object?>>? tools,
        object? toolChoice,
        string apiKey,
        CancellationToken ct)
    {
        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };

        if (tools is { Count: > 0 })
            body["tools"] = tools;

        if (toolChoice != null)
            body["tool_choice"] = toolChoice;

        var client = _httpClientFactory.CreateClient("ai-proxy");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        ApplyOutgoingHeaders(httpRequest, provider, apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpRequest, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Proxied provider {ProviderId} returned {StatusCode}: {Body}",
                provider.Id,
                (int)response.StatusCode,
                Truncate(responseText, 500));

            throw new HttpRequestException(
                $"Provider '{provider.Id}' returned {(int)response.StatusCode}: {Truncate(responseText, 300)}");
        }

        return responseText;
    }

    private async Task<List<Dictionary<string, object?>>> ApplyVisionProxyAsync(
        ProxiedProviderOptions provider,
        string targetModelId,
        List<Dictionary<string, object?>> messages,
        CancellationToken ct)
    {
        var targetModel = ResolveConfiguredModel(provider, targetModelId);
        if (targetModel?.SupportsVision == true)
            return messages;

        var proxyModelId = GetVisionProxyModelId(provider);
        if (string.IsNullOrWhiteSpace(proxyModelId))
            return messages;

        if (!TryFindLastUserMessageWithImages(messages, out int messageIndex, out var imageUrls, out var existingText))
            return messages;

        if (!TryResolveApiKey(provider, out var apiKey))
            return messages;

        var descriptions = new List<string>();
        for (int i = 0; i < imageUrls.Count; i++)
        {
            var isPdf = imageUrls[i].Contains("application/pdf", StringComparison.OrdinalIgnoreCase);
            var prompt = isPdf
                ? "Summarize this document in detail for use as context in a follow-up text-only conversation. Include key facts, structure, and any visible text."
                : "Describe this image in detail for use as context in a follow-up text-only conversation. Include objects, text, colors, layout, and anything relevant to answering questions about it.";

            var visionMessages = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["role"] = "user",
                    ["content"] = new List<Dictionary<string, object?>>
                    {
                        new() { ["type"] = "text", ["text"] = prompt },
                        new()
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new Dictionary<string, object?> { ["url"] = imageUrls[i] }
                        }
                    }
                }
            };

            try
            {
                var json = await SendChatCompletionAsync(provider, proxyModelId, visionMessages, tools: null, toolChoice: null, apiKey, ct);
                var description = ExtractAssistantContent(json);
                if (!string.IsNullOrWhiteSpace(description))
                    descriptions.Add($"[attachment {i + 1}]: {description.Trim()}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiProxy] Vision proxy model {ProxyModel} failed for provider {ProviderId}", proxyModelId, provider.Id);
            }
        }

        if (descriptions.Count == 0)
            return messages;

        _logger.LogInformation(
            "[AiProxy] Vision proxy ({ProxyModel}) described {Count} attachment(s) for {TargetModel} on provider {ProviderId}",
            proxyModelId,
            descriptions.Count,
            targetModelId,
            provider.Id);

        var prefix = string.IsNullOrWhiteSpace(existingText) ? "" : existingText.Trim() + "\n\n";
        var enrichedText = prefix +
            "[Image context — described by vision proxy model '" + proxyModelId + "']\n" +
            string.Join("\n\n", descriptions);

        var enriched = messages.Select(m => new Dictionary<string, object?>(m)).ToList();
        enriched[messageIndex] = new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = enrichedText.Trim()
        };

        return enriched;
    }

    public void LogStartupDiagnostics()
    {
        foreach (var provider in _options.Proxied)
        {
            bool hasKey = TryResolveApiKey(provider, out _);
            bool hasSecretHeader = TryResolveSecretHeader(provider, out _);
            int modelCount = provider.Models?.Count ?? 0;
            bool discover = provider.DiscoverModels && IsOllama(provider);
            _logger.LogInformation(
                "[AiProxy] Provider {Id} ({Name}): type={Type} baseUrl={BaseUrl} keyConfigured={HasKey} secretHeaderConfigured={HasSecretHeader} staticModels={ModelCount} discoverModels={Discover}",
                provider.Id,
                provider.DisplayName,
                NormalizeType(provider.Type),
                provider.BaseUrl,
                hasKey,
                hasSecretHeader,
                modelCount,
                discover);

            if (!hasKey && !IsOllama(provider) && !string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
            {
                _logger.LogWarning(
                    "[AiProxy] Provider {Id} has no API key. Set env var {EnvVar} to enable it.",
                    provider.Id,
                    provider.ApiKeyEnvVar);
            }

            if (!string.IsNullOrWhiteSpace(provider.SecretHeaderName) && !hasSecretHeader)
            {
                _logger.LogWarning(
                    "[AiProxy] Provider {Id} requires secret header {HeaderName} but no value is configured. Set env var {EnvVar} to enable it.",
                    provider.Id,
                    provider.SecretHeaderName,
                    provider.SecretHeaderValueEnvVar ?? "(SecretHeaderValueEnvVar not set)");
            }
        }
    }

    private async Task<List<ProxiedProviderContracts.ProxiedModelDto>> ResolveProviderModelsAsync(
        ProxiedProviderOptions provider,
        CancellationToken ct)
    {
        var models = new List<ProxiedProviderContracts.ProxiedModelDto>();

        if (IsOllama(provider) && provider.DiscoverModels)
        {
            try
            {
                return await DiscoverOllamaModelsAsync(provider, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AiProxy] Failed to discover Ollama models for {ProviderId}", provider.Id);
            }
        }

        foreach (var m in provider.Models.Where(m => !string.IsNullOrWhiteSpace(m.Id) && !m.HideFromModelList))
            models.Add(ToModelDto(m));

        return models;
    }

    private async Task<List<ProxiedProviderContracts.ProxiedModelDto>> DiscoverOllamaModelsAsync(
        ProxiedProviderOptions provider,
        CancellationToken ct)
    {
        var origin = GetOllamaOrigin(provider.BaseUrl);
        var tagsUrl = $"{origin}/api/tags";

        var client = _httpClientFactory.CreateClient("ai-proxy");
        using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, tagsUrl);
        TryResolveApiKey(provider, out var apiKey);
        ApplyOutgoingHeaders(tagsRequest, provider, apiKey);
        using var tagsResponse = await client.SendAsync(tagsRequest, ct);
        tagsResponse.EnsureSuccessStatusCode();
        var resp = await tagsResponse.Content.ReadFromJsonAsync<OllamaTagsResponse>(ct);
        if (resp?.Models == null)
            return new List<ProxiedProviderContracts.ProxiedModelDto>();

        var configuredModels = provider.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .ToList();

        if (configuredModels.Count == 0)
            return new List<ProxiedProviderContracts.ProxiedModelDto>();

        var allowlist = configuredModels.ToDictionary(m => m.Id, m => m, StringComparer.OrdinalIgnoreCase);
        var allowlistOrder = configuredModels.Select(m => m.Id).ToList();

        return resp.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Where(m => allowlist.ContainsKey(m.Name!))
            .Select(m =>
            {
                allowlist.TryGetValue(m.Name!, out var configured);

                var caps = m.Capabilities ?? Array.Empty<string>();
                bool supportsTools = caps.Any(c => string.Equals(c, "tools", StringComparison.OrdinalIgnoreCase));
                bool supportsVision = caps.Any(c => string.Equals(c, "vision", StringComparison.OrdinalIgnoreCase));

                if (!supportsTools && !supportsVision)
                {
                    var fromCatalog = ProviderCatalog.GetCapabilitiesForModel($"ollama/{m.Name}");
                    supportsTools = fromCatalog.SupportsTools;
                    supportsVision = fromCatalog.SupportsVision;
                }

                if (configured != null)
                {
                    supportsTools = configured.SupportsTools;
                    supportsVision = configured.SupportsVision;
                }

                if (configured?.HideFromModelList == true)
                    return null;

                return new ProxiedProviderContracts.ProxiedModelDto
                {
                    Id = m.Name!,
                    Label = string.IsNullOrWhiteSpace(configured?.Label) ? m.Name! : configured!.Label,
                    Icon = string.IsNullOrWhiteSpace(configured?.Icon) ? "🦙" : configured!.Icon,
                    SupportsTools = supportsTools,
                    SupportsVision = supportsVision,
                    IsVisionProxy = configured?.IsVisionProxy ?? false
                };
            })
            .Where(dto => dto != null)
            .Select(dto => dto!)
            .OrderBy(m => allowlistOrder.FindIndex(k => string.Equals(k, m.Id, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProxiedProviderContracts.ProxiedModelDto ToModelDto(ProxiedModelOptions m) =>
        new()
        {
            Id = m.Id,
            Label = string.IsNullOrWhiteSpace(m.Label) ? m.Id : m.Label,
            Icon = string.IsNullOrWhiteSpace(m.Icon) ? "🤖" : m.Icon,
            SupportsTools = m.SupportsTools,
            SupportsVision = m.SupportsVision,
            IsVisionProxy = m.IsVisionProxy
        };

    private static ProxiedModelOptions? ResolveConfiguredModel(ProxiedProviderOptions provider, string modelId) =>
        provider.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    private static string? GetVisionProxyModelId(ProxiedProviderOptions provider) =>
        provider.Models.FirstOrDefault(m => m.IsVisionProxy && m.SupportsVision)?.Id;

    private static bool TryFindLastUserMessageWithImages(
        List<Dictionary<string, object?>> messages,
        out int messageIndex,
        out List<string> imageUrls,
        out string existingText)
    {
        messageIndex = -1;
        imageUrls = new List<string>();
        existingText = "";

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (!messages[i].TryGetValue("role", out var roleObj) ||
                !string.Equals(roleObj?.ToString(), "user", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryExtractUserMessageContent(messages[i], out existingText, out var urls) || urls.Count == 0)
                continue;

            messageIndex = i;
            imageUrls = urls;
            return true;
        }

        return false;
    }

    private static bool TryExtractUserMessageContent(
        Dictionary<string, object?> message,
        out string text,
        out List<string> imageUrls)
    {
        text = "";
        imageUrls = new List<string>();

        if (!message.TryGetValue("content", out var contentObj) || contentObj == null)
            return false;

        if (contentObj is string s)
        {
            text = s;
            return false;
        }

        if (contentObj is JsonElement jsonEl)
            return TryExtractFromContentElement(jsonEl, out text, out imageUrls);

        if (contentObj is IEnumerable<object?> parts)
        {
            foreach (var part in parts)
            {
                if (part is Dictionary<string, object?> dict)
                    AccumulateContentPart(dict, ref text, imageUrls);
                else if (part is JsonElement el)
                    AccumulateContentPart(el, ref text, imageUrls);
            }
            return imageUrls.Count > 0;
        }

        return false;
    }

    private static bool TryExtractFromContentElement(JsonElement contentEl, out string text, out List<string> imageUrls)
    {
        text = "";
        imageUrls = new List<string>();

        if (contentEl.ValueKind == JsonValueKind.String)
        {
            text = contentEl.GetString() ?? "";
            return false;
        }

        if (contentEl.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var part in contentEl.EnumerateArray())
            AccumulateContentPart(part, ref text, imageUrls);

        return imageUrls.Count > 0;
    }

    private static void AccumulateContentPart(JsonElement part, ref string text, List<string> imageUrls)
    {
        if (!part.TryGetProperty("type", out var typeEl))
            return;

        var type = typeEl.GetString() ?? "";
        if (type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            part.TryGetProperty("text", out var textEl))
        {
            var piece = textEl.GetString();
            if (!string.IsNullOrWhiteSpace(piece))
                text = string.IsNullOrWhiteSpace(text) ? piece : text + "\n" + piece;
            return;
        }

        if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase) &&
            part.TryGetProperty("image_url", out var imageUrlEl))
        {
            string? url = null;
            if (imageUrlEl.ValueKind == JsonValueKind.String)
                url = imageUrlEl.GetString();
            else if (imageUrlEl.TryGetProperty("url", out var urlEl))
                url = urlEl.GetString();

            if (!string.IsNullOrWhiteSpace(url))
                imageUrls.Add(url);
        }
    }

    private static void AccumulateContentPart(Dictionary<string, object?> part, ref string text, List<string> imageUrls)
    {
        if (!part.TryGetValue("type", out var typeObj))
            return;

        var type = typeObj?.ToString() ?? "";
        if (type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            part.TryGetValue("text", out var textObj))
        {
            var piece = textObj?.ToString();
            if (!string.IsNullOrWhiteSpace(piece))
                text = string.IsNullOrWhiteSpace(text) ? piece : text + "\n" + piece;
            return;
        }

        if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase) &&
            part.TryGetValue("image_url", out var imageUrlObj))
        {
            string? url = imageUrlObj switch
            {
                string s => s,
                Dictionary<string, object?> dict when dict.TryGetValue("url", out var urlObj) => urlObj?.ToString(),
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                JsonElement el when el.TryGetProperty("url", out var urlEl) => urlEl.GetString(),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(url))
                imageUrls.Add(url);
        }
    }

    private static string? ExtractAssistantContent(string openAiJson)
    {
        using var doc = JsonDocument.Parse(openAiJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return null;

        var message = choices[0].GetProperty("message");
        if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString();

        return null;
    }

    private static void ApplyOutgoingHeaders(
        HttpRequestMessage request,
        ProxiedProviderOptions provider,
        string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (TryResolveSecretHeader(provider, out var secretValue))
            request.Headers.TryAddWithoutValidation(provider.SecretHeaderName!.Trim(), secretValue);
    }

    private static bool TryResolveSecretHeader(ProxiedProviderOptions provider, out string secretValue)
    {
        secretValue = "";

        if (string.IsNullOrWhiteSpace(provider.SecretHeaderName))
            return false;

        if (!string.IsNullOrWhiteSpace(provider.SecretHeaderValue))
        {
            secretValue = provider.SecretHeaderValue.Trim();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(provider.SecretHeaderValueEnvVar))
        {
            var fromEnv = Environment.GetEnvironmentVariable(provider.SecretHeaderValueEnvVar)
                       ?? Environment.GetEnvironmentVariable(provider.SecretHeaderValueEnvVar, EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable(provider.SecretHeaderValueEnvVar.Replace("__", ":"));

            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                secretValue = fromEnv.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveApiKey(ProxiedProviderOptions provider, out string apiKey)
    {
        apiKey = "";

        if (IsOllama(provider))
        {
            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                apiKey = provider.ApiKey.Trim();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
            {
                var fromEnv = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar)
                           ?? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar, EnvironmentVariableTarget.User)
                           ?? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar.Replace("__", ":"));

                if (!string.IsNullOrWhiteSpace(fromEnv))
                {
                    apiKey = fromEnv.Trim();
                    return true;
                }
            }

            apiKey = "ollama";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            apiKey = provider.ApiKey.Trim();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
        {
            var fromEnv = Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar)
                       ?? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar, EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable(provider.ApiKeyEnvVar.Replace("__", ":"));

            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                apiKey = fromEnv.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool IsOllama(ProxiedProviderOptions provider) =>
        string.Equals(NormalizeType(provider.Type), "Ollama", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeType(string? type) =>
        string.IsNullOrWhiteSpace(type) ? "OpenAICompatible" : type.Trim();

    private static string GetOllamaOrigin(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return url[..^3];
        return url;
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..maxLen] + "...";

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public List<OllamaTagModel>? Models { get; set; }
    }

    private sealed class OllamaTagModel
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("capabilities")]
        public string[]? Capabilities { get; set; }
    }
}