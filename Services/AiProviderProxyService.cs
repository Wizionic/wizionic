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

        var baseUrl = provider.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages,
            ["stream"] = false
        };

        if (request.Tools is { Count: > 0 })
            body["tools"] = request.Tools;

        if (request.ToolChoice != null)
            body["tool_choice"] = request.ToolChoice;

        var client = _httpClientFactory.CreateClient("ai-proxy");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(httpRequest, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Proxied provider {ProviderId} returned {StatusCode}: {Body}",
                request.ProviderId,
                (int)response.StatusCode,
                Truncate(responseText, 500));

            throw new HttpRequestException(
                $"Provider '{request.ProviderId}' returned {(int)response.StatusCode}: {Truncate(responseText, 300)}");
        }

        return responseText;
    }

    public void LogStartupDiagnostics()
    {
        foreach (var provider in _options.Proxied)
        {
            bool hasKey = TryResolveApiKey(provider, out _);
            int modelCount = provider.Models?.Count ?? 0;
            bool discover = provider.DiscoverModels && IsOllama(provider);
            _logger.LogInformation(
                "[AiProxy] Provider {Id} ({Name}): type={Type} baseUrl={BaseUrl} keyConfigured={HasKey} staticModels={ModelCount} discoverModels={Discover}",
                provider.Id,
                provider.DisplayName,
                NormalizeType(provider.Type),
                provider.BaseUrl,
                hasKey,
                modelCount,
                discover);

            if (!hasKey && !IsOllama(provider) && !string.IsNullOrWhiteSpace(provider.ApiKeyEnvVar))
            {
                _logger.LogWarning(
                    "[AiProxy] Provider {Id} has no API key. Set env var {EnvVar} to enable it.",
                    provider.Id,
                    provider.ApiKeyEnvVar);
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

        foreach (var m in provider.Models.Where(m => !string.IsNullOrWhiteSpace(m.Id)))
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
        var resp = await client.GetFromJsonAsync<OllamaTagsResponse>(tagsUrl, ct);
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
                    if (!configured.SupportsTools && !configured.SupportsVision)
                    {
                        supportsTools = configured.SupportsTools;
                        supportsVision = configured.SupportsVision;
                    }
                }

                return new ProxiedProviderContracts.ProxiedModelDto
                {
                    Id = m.Name!,
                    Label = string.IsNullOrWhiteSpace(configured?.Label) ? m.Name! : configured!.Label,
                    Icon = string.IsNullOrWhiteSpace(configured?.Icon) ? "🦙" : configured!.Icon,
                    SupportsTools = supportsTools,
                    SupportsVision = supportsVision
                };
            })
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
            SupportsVision = m.SupportsVision
        };

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