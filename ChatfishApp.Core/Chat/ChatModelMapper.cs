using ChatfishApp.Core.Sync;

namespace ChatfishApp.Core.Chat;

public static class ChatModelMapper
{
    public static ChatModelInfo ToChatModelInfo(SyncModelInfo model) =>
        new(
            model.Id,
            model.Label,
            model.Icon,
            model.ProviderId,
            model.ProviderName,
            model.SupportsTools,
            model.SupportsVision,
            model.IsOllamaBackend,
            model.ContextSize,
            model.VisionProxyModelId);

    public static List<ChatModelInfo> ToChatModelInfoList(IEnumerable<SyncModelInfo> models) =>
        models.Select(ToChatModelInfo).ToList();
}