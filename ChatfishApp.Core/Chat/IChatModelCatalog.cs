namespace ChatfishApp.Core.Chat;

public interface IChatModelCatalog
{
    Task RefreshAsync(CancellationToken ct = default);
    List<ChatModelInfo> GetAvailableModels();
    string? GetConfiguredDefaultModelId(IReadOnlyList<ChatModelInfo> availableModels);
    string? GetProxiedVisionProxyModelId(string modelId);
}