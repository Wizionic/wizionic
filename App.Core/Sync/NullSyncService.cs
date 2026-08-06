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

    public bool AutoSyncChatHistory => false;
    public bool AutoSyncNotes => false;
    public bool AutoSyncBookmarks => false;
    public bool AutoSyncInstalledApps => false;
    public IReadOnlyCollection<string> SyncTargetDeviceIds { get; } = Array.Empty<string>();

    public event Action? OnChanged;
    public event Action? OnConversationsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;
    public event Action? OnNotesChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task EnsureConnectedAndRegisteredAsync() => Task.CompletedTask;
    public Task RefreshAsync() => Task.CompletedTask;
    public Task PublishAiCapabilitiesAsync() => Task.CompletedTask;

    public Task SetDeviceNameAsync(string newName) => Task.CompletedTask;
    public Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds) => Task.CompletedTask;
    public Task SetAutoSyncChatHistoryAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncNotesAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncBookmarksAsync(bool enabled) => Task.CompletedTask;
    public Task SetAutoSyncInstalledAppsAsync(bool enabled) => Task.CompletedTask;

    public Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        Task.CompletedTask;

    public Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        Task.CompletedTask;

    public Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries) =>
        Task.CompletedTask;

    public Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark) =>
        Task.CompletedTask;

    public Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder) =>
        Task.CompletedTask;

    public Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app) =>
        Task.CompletedTask;

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        Task.FromResult(0);

    public void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null) { }
    public void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title) { }
    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAt) { }
    public void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId) { }
    public void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncFolderAfterLocalSave(string folderId) { }
    public void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc) { }
    public void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId) { }
    public void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc) { }

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
