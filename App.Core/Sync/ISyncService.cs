using App.Core.Browser;
using App.Core.Storage;

namespace App.Core.Sync;

/// <summary>
/// Cross-device sync: SignalR presence/signaling, WebRTC data transfer, and optional AI proxy.
/// </summary>
public interface ISyncService : INotesSyncBridge, IGallerySyncBridge, ICalendarSyncBridge, IAsyncDisposable
{
    string? MyDeviceId { get; }
    string MyDeviceName { get; }
    IReadOnlyList<SyncDeviceInfo> Devices { get; }
    bool IsConnected { get; }

    string? AiServerDeviceId { get; }
    IReadOnlyList<SyncModelInfo> RemoteModels { get; }
    bool IsAiProxyConnected { get; }
    string? AiProxyError { get; }

    bool SyncToAllDevices { get; }
    bool AutoSyncChatHistory { get; }
    bool AutoSyncNotes { get; }
    bool AutoSyncGallery { get; }
    bool AutoSyncCalendar { get; }
    bool AutoSyncBookmarks { get; }
    bool AutoSyncInstalledApps { get; }
    bool AutoSyncLocalAi { get; }
    bool AutoSyncLemonade { get; }
    bool AutoSyncCloudProviders { get; }
    bool AutoSyncHomeAssistant { get; }
    bool AutoSyncTools { get; }
    bool AutoSyncSystemPrompt { get; }
    bool AutoSyncProfile { get; }
    bool AutoSyncMemories { get; }
    bool AutoSyncAppearance { get; }

    /// <summary>
    /// Explicit per-device targets when <see cref="SyncToAllDevices"/> is false.
    /// Prefer <see cref="GetEffectiveSyncTargetDeviceIds"/> for sync operations.
    /// </summary>
    IReadOnlyCollection<string> SyncTargetDeviceIds { get; }

    event Action? OnChanged;
    event Action? OnConversationsChanged;
    event Action<string, string, string>? OnSyncPayloadReceived;
    event Action<string, string>? OnSyncAckReceived;
    event Action<string, string, string>? OnNoteSyncPayloadReceived;
    event Action<string, string>? OnNoteSyncAckReceived;
    event Action<string, string, string>? OnAlbumSyncPayloadReceived;
    event Action<string, string>? OnAlbumSyncAckReceived;
    event Action? OnBookmarksChanged;
    event Action? OnInstalledAppsChanged;
    event Action? OnSettingsChanged;

    Task InitializeAsync();
    Task EnsureConnectedAndRegisteredAsync();
    Task RefreshAsync();
    Task PublishAiCapabilitiesAsync();

    Task SetDeviceNameAsync(string newName);
    Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds);
    Task SetSyncToAllDevicesAsync(bool enabled);
    Task SetAutoSyncChatHistoryAsync(bool enabled);
    Task SetAutoSyncNotesAsync(bool enabled);
    Task SetAutoSyncGalleryAsync(bool enabled);
    Task SetAutoSyncCalendarAsync(bool enabled);
    Task SetAutoSyncBookmarksAsync(bool enabled);
    Task SetAutoSyncInstalledAppsAsync(bool enabled);
    Task SetAutoSyncLocalAiAsync(bool enabled);
    Task SetAutoSyncLemonadeAsync(bool enabled);
    Task SetAutoSyncCloudProvidersAsync(bool enabled);
    Task SetAutoSyncHomeAssistantAsync(bool enabled);
    Task SetAutoSyncToolsAsync(bool enabled);
    Task SetAutoSyncSystemPromptAsync(bool enabled);
    Task SetAutoSyncProfileAsync(bool enabled);
    Task SetAutoSyncMemoriesAsync(bool enabled);
    Task SetAutoSyncAppearanceAsync(bool enabled);

    Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages);

    Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages);
    Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries);
    Task StartWebRtcAlbumSyncAsync(string targetDeviceId, string albumId, string title);
    Task StartWebRtcAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId);
    Task StartWebRtcCalendarSyncAsync(string targetDeviceId, string calendarId);
    Task StartWebRtcCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId);
    Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark);
    Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder);
    Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app);
    Task StartWebRtcSettingsSyncAsync(string targetDeviceId, string category);

    Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllAlbumsToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllCalendarsToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds);
    Task<int> SyncSettingsCategoryToDevicesAsync(string category, IEnumerable<string> targetDeviceIds);

    void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null);
    void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc);

    void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId);
    void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc);
    void ScheduleAutoSyncFolderAfterLocalSave(string folderId);
    void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc);
    void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId);
    void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc);
    void ScheduleAutoSyncSettingsAfterLocalSave(string category);

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

    /// <summary>
    /// Device ids that should receive sync: all others when SyncToAllDevices, else explicit targets.
    /// </summary>
    IReadOnlyCollection<string> GetEffectiveSyncTargetDeviceIds();
}
