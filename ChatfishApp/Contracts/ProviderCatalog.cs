using System.Collections.Immutable;

namespace ChatfishApp.Contracts;

/// <summary>
/// Static catalog of supported AI providers and their models.
/// This is the source of truth for the UI model selector and the AiProviderService factory.
/// </summary>
public static class ProviderCatalog
{
    public sealed record ModelDefinition(string Id, string Label, string Icon, bool SupportsTools = true);

    public sealed record ProviderDefinition(
        string Id,
        string DisplayName,
        string Type,           // "OpenAICompatible" | "Ollama" | future
        string? BaseUrl,
        ImmutableArray<ModelDefinition> Models);

    public static readonly ImmutableArray<ProviderDefinition> Providers = ImmutableArray.Create(
        new ProviderDefinition(
            "groq",
            "Groq",
            "OpenAICompatible",
            "https://api.groq.com/openai/v1/",
            ImmutableArray.Create(
                new ModelDefinition("llama-3.1-8b-instant", "Llama 3.1 8B", "⚡", SupportsTools: false), // small model, limited tool support on Groq
                new ModelDefinition("llama-3.3-70b-versatile", "Llama 3.3 70B", "🧠"),
                new ModelDefinition("qwen/qwen3-32b", "Qwen3 32B", "🐉"),
                new ModelDefinition("openai/gpt-oss-20b", "GPT-OSS 20B", "🔧"),
                new ModelDefinition("openai/gpt-oss-120b", "GPT-OSS 120B", "🔧"),
                new ModelDefinition("meta-llama/llama-4-scout-17b-16e-instruct", "Llama 4 Scout", "🦙"),
                new ModelDefinition("allam-2-7b", "Allam 2 7B", "🪶", SupportsTools: false)
            )),

        // Google Gemini via the official OpenAI-compatible endpoint (https://ai.google.dev/gemini-api/docs/openai).
        // IMPORTANT: As of June 2026, gemini-2.0-flash and earlier 2.0 models are shut down.
        // Use current Flash models such as gemini-2.5-flash (this is what appears in AI Studio Rate Limits for free tier).
        // Your Gemini API key from https://aistudio.google.com/app/apikey is used as the "OpenAI key".
        // Free tier usually requires "Set up billing" (link a billing account to the GCP project) to activate quotas,
        // even if you stay on free tier and are not charged.
        new ProviderDefinition(
            "gemini",
            "Google Gemini",
            "OpenAICompatible",
            "https://generativelanguage.googleapis.com/v1beta/openai/",
            ImmutableArray.Create(
                new ModelDefinition("gemini-2.5-flash", "Gemini 2.5 Flash", "✨", SupportsTools: true)
                // Add "gemini-2.5-flash-lite" etc. if you want them available to users with keys.
            )),

        // OpenRouter: One key for 400+ models from many providers (OpenAI, Anthropic, Google, Meta, Mistral, etc.).
        // Base is OpenAI-compatible. Great for free-tier models, tool calling / agentic use, and model variety.
        // IMPORTANT: Send the two attribution headers on every request (see AiProviderService for config).
        // Get keys at https://openrouter.ai/keys . Recommended: dedicated key (avoids quota sharing like the "default project" issue).
        // Many models support tool calling (Claude, GPT-4o, Llama 3.3/4, Gemini, etc.). Free options often use :free suffix or have free quotas.
        new ProviderDefinition(
            "openrouter",
            "OpenRouter",
            "OpenAICompatible",
            "https://openrouter.ai/api/v1/",
            ImmutableArray.Create(
                new ModelDefinition("anthropic/claude-3.5-sonnet", "Claude 3.5 Sonnet (strong tools)", "🧠"),
                new ModelDefinition("openai/gpt-4o", "GPT-4o", "🔧"),
                new ModelDefinition("google/gemini-2.0-flash", "Gemini 2.0 Flash (via OR)", "✨"),
                new ModelDefinition("meta-llama/llama-3.3-70b-instruct", "Llama 3.3 70B Instruct", "🦙"),
                new ModelDefinition("mistralai/mistral-large", "Mistral Large", "🌪️"),
                new ModelDefinition("qwen/qwen2.5-72b-instruct", "Qwen2.5 72B", "🐉"),
                // Free / low-cost friendly examples on OpenRouter (availability and exact slugs can vary; check openrouter.ai/models)
                // Note: free tier models often have limited tool support or rate limits
                new ModelDefinition("meta-llama/llama-3.2-3b-instruct:free", "Llama 3.2 3B (free tier)", "🆓", SupportsTools: false),
                new ModelDefinition("google/gemini-2.0-flash:free", "Gemini 2.0 Flash (free on OR)", "🆓", SupportsTools: true)
            ))
    );

    /// <summary>
    /// All models flattened (for quick lookup).
    /// </summary>
    public static readonly ImmutableDictionary<string, (ProviderDefinition Provider, ModelDefinition Model)> AllModelsById =
        Providers
            .SelectMany(p => p.Models.Select(m => (p, m)))
            .ToImmutableDictionary(x => x.m.Id, x => (x.p, x.m));

    public static ProviderDefinition? GetProvider(string providerId) =>
        Providers.FirstOrDefault(p => p.Id == providerId);

    public static (ProviderDefinition Provider, ModelDefinition Model)? GetModel(string modelId) =>
        AllModelsById.TryGetValue(modelId, out var entry) ? entry : null;
}
