using App.Core.Browser;
using App.Core.Storage;

namespace App.Core.Sync;

/// <summary>
/// No-op sync for server-side rendering of shared layout/components.
/// Interactive WASM and MAUI register real implementations in their own DI.
/// </summary>
public sealed class NullSyncService : ISyncService
{
    public string? MyDeviceId => null;
    public string MyDeviceName => "This device";
    public IReadOnlyList<SyncDeviceInfo> Devices { get; } = Array.Empty<SyncDeviceInfo>();
    public bool IsConnected => false;

    public string? AiServerDeviceId => null;
    public IReadOnlyList<SyncModelInfo> RemoteModels { get; } = Array.Empty<SyncModelInfo>();
    public bool IsAiProxyConnected => false;
    public string? AiProxyError => null;

    public bool SyncToAllDevices => true;
    public bool AutoSyncChatHistory => true;
    public bool AutoSyncNotes => true;
    public bool AutoSyncGallery => true;
    public bool AutoSyncCalendar => true;
    public bool AutoSyncBookmarks => true;
    public bool AutoSyncInstalledApps => true;
    public bool AutoSyncLocalAi => true;
    public bool AutoSyncLemonade => true;
    public bool AutoSyncCloudProviders => true;
    public bool AutoSyncHomeAssistant => true;
    public bool AutoSyncTools => true;
    public bool AutoSyncSystemPrompt => true;
    public bool AutoSyncProfile => true;
    public bool AutoSyncMemories => true;
    public bool AutoSyncAppearance => true;
    public IReadOnlyCollection<string> SyncTargetDeviceIds { get; } = Array.Empty<string>();

    public event Action? OnChanged;
    public event Action? OnConversationsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;
    public event Action<string, string, string>? OnAlbumSyncPayloadReceived;
    public event Action<string, string>? OnAlbumSyncAckReceived;
    public event Action? OnNotesChanged;
    public event Action? OnGalleryChanged;
    public event Action? OnCalendarsChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;
    public event Action? OnSettingsChanged;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task EnsureConnectedAndRegisteredAsync() => Task.CompletedTask;
    public Task RefreshAsync() => Task.CompletedTask;
    public Task PublishAiCapabilitiesAsync() => Task.CompletedTask;

    public Task SetDeviceNameAsync(string newName) => Task.CompletedTask;
    public Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds) => Task.CompletedTask;
    public Task SetSyncToAllDevicesAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncChatHistoryAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncNotesAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncGalleryAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncCalendarAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncBookmarksAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncInstalledAppsAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncLocalAiAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncLemonadeAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncCloudProvidersAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncHomeAssistantAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncToolsAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncSystemPromptAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncProfileAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncMemoriesAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncAppearanceAsync(bool enabled) => Task.CompletedTask;

    public Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        Task.CompletedTask;

    public Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        Task.CompletedTask;

    public Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries) =>
        Task.CompletedTask;

    public Task StartWebRtcAlbumSyncAsync(string targetDeviceId, string albumId, string title) =>
        Task.CompletedTask;

    public Task StartWebRtcAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId) =>
        Task.CompletedTask;

    public Task StartWebRtcCalendarSyncAsync(string targetDeviceId, string calendarId) =>
        Task.CompletedTask;

    public Task StartWebRtcCalendarEventSyncAsync(string targetDeviceId, string calendarId, string eventId) =>
        Task.CompletedTask;

    public Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark) =>
        Task.CompletedTask;

    public Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder) =>
        Task.CompletedTask;

    public Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app) =>
        Task.CompletedTask;

    public Task StartWebRtcSettingsSyncAsync(string targetDeviceId, string category) =>
        Task.CompletedTask;

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllAlbumsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllCalendarsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncSettingsCategoryToDevicesAsync(string category, IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null) { }
    public void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title) { }
    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAt) { }
    public void ScheduleAutoSyncAlbumMetaAfterLocalSave(string albumId, string title) { }
    public void ScheduleAutoSyncAlbumDeleteAfterLocalDelete(string albumId, DateTime deletedAt) { }
    public void ScheduleAutoSyncAlbumImageAfterLocalSave(string albumId, string imageId) { }
    public void ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(string albumId, string imageId, DateTime deletedAt) { }
    public void ScheduleAutoSyncCalendarAfterLocalSave(string calendarId) { }
    public void ScheduleAutoSyncCalendarDeleteAfterLocalDelete(string calendarId, DateTime deletedAt) { }
    public void ScheduleAutoSyncEventAfterLocalSave(string calendarId, string eventId) { }
    public void ScheduleAutoSyncEventDeleteAfterLocalDelete(string calendarId, string eventId, DateTime deletedAt) { }
    public void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId) { }
    public void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncFolderAfterLocalSave(string folderId) { }
    public void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId) { }
    public void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncSettingsAfterLocalSave(string category) { }

    public string? GetAiServerDeviceName() => null;
    public Task SetAiServerDeviceAsync(string? deviceId) => Task.CompletedTask;
    public Task EnsureAiProxyConnectionAsync() => Task.CompletedTask;
    public Task RequestRemoteModelsAsync() => Task.CompletedTask;

    public Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<ChatMessage> messages,
        CancellationToken ct = default) =>
        Task.FromResult(("", ""));

    public bool IsSelf(string? deviceId) => false;
    public IEnumerable<SyncDeviceInfo> GetOtherDevices() => Array.Empty<SyncDeviceInfo>();
    public IReadOnlyCollection<string> GetEffectiveSyncTargetDeviceIds() => Array.Empty<string>();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
