using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Storage;

namespace ChatfishApp.Core.Sync;

/// <summary>
/// Cross-device sync: SignalR presence/signaling, WebRTC data transfer, and optional AI proxy.
/// </summary>
public interface ISyncService : INotesSyncBridge, IAsyncDisposable
{
    string? MyDeviceId { get; }
    string MyDeviceName { get; }
    IReadOnlyList<SyncDeviceInfo> Devices { get; }
    bool IsConnected { get; }

    string? AiServerDeviceId { get; }
    IReadOnlyList<SyncModelInfo> RemoteModels { get; }
    bool IsAiProxyConnected { get; }
    string? AiProxyError { get; }

    bool AutoSyncChatHistory { get; }
    bool AutoSyncNotes { get; }
    bool AutoSyncBookmarks { get; }
    bool AutoSyncInstalledApps { get; }
    IReadOnlyCollection<string> SyncTargetDeviceIds { get; }

    event Action? OnChanged;
    event Action? OnConversationsChanged;
    event Action<string, string, string>? OnSyncPayloadReceived;
    event Action<string, string>? OnSyncAckReceived;
    event Action<string, string, string>? OnNoteSyncPayloadReceived;
    event Action<string, string>? OnNoteSyncAckReceived;
    event Action? OnBookmarksChanged;
    event Action? OnInstalledAppsChanged;

    Task InitializeAsync();
    Task EnsureConnectedAndRegisteredAsync();
    Task RefreshAsync();
    Task PublishAiCapabilitiesAsync();

    Task SetDeviceNameAsync(string newName);
    Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds);
    Task SetAutoSyncChatHistoryAsync(bool enabled);
    Task SetAutoSyncNotesAsync(bool enabled);
    Task SetAutoSyncBookmarksAsync(bool enabled);
    Task SetAutoSyncInstalledAppsAsync(bool enabled);

    Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages);

    Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages);
    Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries);
    Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark);
    Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder);
    Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app);

    Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds);

    void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null);
    void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc);

    void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId);
    void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc);
    void ScheduleAutoSyncFolderAfterLocalSave(string folderId);
    void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc);
    void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId);
    void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc);

    string? GetAiServerDeviceName();
    Task SetAiServerDeviceAsync(string? deviceId);
    Task EnsureAiProxyConnectionAsync();
    Task RequestRemoteModelsAsync();
    Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<ChatMessage> messages,
        CancellationToken ct = default);

    bool IsSelf(string? deviceId);
    IEnumerable<SyncDeviceInfo> GetOtherDevices();
}
