namespace App.Core.Sync;

/// <summary>
/// Model descriptor exposed via remote AI proxy over sync WebRTC.
/// </summary>
public record SyncModelInfo(
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