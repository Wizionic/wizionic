using System.Collections.Immutable;

namespace App.Contracts;

/// <summary>
/// Known local-model name patterns (Ollama and similar) for tool + vision badges.
/// User-keyed cloud models live in <c>IKeyStore.CloudProviders</c>, not here.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>
    /// Known local (Ollama and other local model runners) model name patterns and their capabilities.
    /// Used for dynamic models (user can pull any tag) so we can still show correct tool + vision badges in the UI.
    /// Matching is done with Contains (case-insensitive) against the model name after any "ollama/" prefix.
    /// </summary>
    public static readonly ImmutableArray<(string NamePattern, bool SupportsTools, bool SupportsVision)> LocalModelPatterns = ImmutableArray.Create(
        ("llava", false, true),
        ("bakllava", true, true),
        ("moondream", false, true),
        ("llama3.2-vision", true, true),
        ("llama-3.2-vision", true, true),
        ("vision", true, true),
        ("llama3.2", true, false),
        ("llama-3.2", true, false),
        ("qwen2-vl", true, true),
        ("qwen2.5-vl", true, true),
        ("minicpm", false, true)
    );

    public static (bool Matched, bool SupportsTools, bool SupportsVision) GetLocalPatternCapabilities(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return (false, true, false);

        string name = modelName;
        if (name.Contains('/'))
            name = name.Split('/', 2)[1];

        name = name.ToLowerInvariant();

        foreach (var p in LocalModelPatterns)
        {
            if (name.Contains(p.NamePattern.ToLowerInvariant()))
                return (true, p.SupportsTools, p.SupportsVision);
        }

        return (false, true, false);
    }

    public static (bool SupportsTools, bool SupportsVision) GetCapabilitiesForModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return (false, false);

        var (matched, supportsTools, supportsVision) = GetLocalPatternCapabilities(modelId);
        if (matched)
            return (supportsTools, supportsVision);

        return (true, false);
    }
}