namespace ChatfishApp.Services;

/// <summary>
/// Configuration for CORS-restricted AI providers that are proxied through the ASP.NET backend.
/// Keys are resolved from <see cref="ProxiedProviderOptions.ApiKeyEnvVar"/> or direct config/env binding.
/// </summary>
public class AiProviderProxyOptions
{
    public const string SectionName = "AiProviders";

    public List<ProxiedProviderOptions> Proxied { get; set; } = new();
}

public class ProxiedProviderOptions
{
    /// <summary>OpenAICompatible (default) or Ollama (OpenAI-compatible /v1 on an Ollama server).</summary>
    public string Type { get; set; } = "OpenAICompatible";

    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string? ApiKeyEnvVar { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional custom header name sent on every proxied request (e.g. for Cloudflare access rules).
    /// The header value is read from <see cref="SecretHeaderValueEnvVar"/> or direct config/env binding.
    /// </summary>
    public string? SecretHeaderName { get; set; }

    public string? SecretHeaderValueEnvVar { get; set; }
    public string? SecretHeaderValue { get; set; }

    /// <summary>
    /// When true and Type is Ollama, models are fetched live from {origin}/api/tags.
    /// Only tags whose name exactly matches a <see cref="ProxiedModelOptions.Id"/> in
    /// <see cref="Models"/> are exposed to end users (Models is the allowlist).
    /// </summary>
    public bool DiscoverModels { get; set; }

    /// <summary>
    /// When true, this provider and its models are omitted from the WASM model selector
    /// (chat may still be routed through the proxy when configured).
    /// </summary>
    public bool HideFromModelList { get; set; }

    /// <summary>
    /// Model id to select when the user has no saved preference or their saved model
    /// is no longer in the available list. Must match a <see cref="ProxiedModelOptions.Id"/>.
    /// </summary>
    public string? DefaultModel { get; set; }

    public List<ProxiedModelOptions> Models { get; set; } = new();
}

public class ProxiedModelOptions
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = "🤖";
    public bool SupportsTools { get; set; } = true;
    public bool SupportsVision { get; set; }

    /// <summary>
    /// When true, non-vision models in the same provider route image uploads through this model first.
    /// Only one model per provider should be marked as the vision proxy.
    /// </summary>
    public bool IsVisionProxy { get; set; }

    /// <summary>
    /// Omit from the WASM model selector (e.g. infrastructure-only vision proxy models).
    /// </summary>
    public bool HideFromModelList { get; set; }
}