namespace App.Core.Chat;

public record ChatModelInfo(
    string Id,
    string Label,
    string Icon,
    string ProviderId,
    string ProviderName,
    bool SupportsTools = true,
    bool SupportsVision = false,
    bool IsOllamaBackend = false,
    int ContextSize = 0,
    string? VisionProxyModelId = null,
    bool IsOmniCollection = false,
    bool IsLemonadeBackend = false,
    /// <summary>
    /// Lemonade (or similar) text-to-image model. Not used for chat completions;
    /// selecting it in the model picker routes Send to image generation.
    /// </summary>
    bool IsImageGeneration = false,
    /// <summary>Model has Lemonade <c>edit</c> label (img2img / instruction edit).</summary>
    bool SupportsImageEdit = false);
