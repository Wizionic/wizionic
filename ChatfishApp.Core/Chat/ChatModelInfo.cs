namespace ChatfishApp.Core.Chat;

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
    string? VisionProxyModelId = null);