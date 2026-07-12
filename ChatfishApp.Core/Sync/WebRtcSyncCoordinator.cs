using System.Text.Json;
using ChatfishApp.Core.Browser;
using ChatfishApp.Core.Storage;
using ChatfishApp.Core.Sync;

namespace ChatfishApp.Core.Sync;

/// <summary>
/// Shared WebRTC data-channel sync protocol (queue, manifest, ack state, signaling handlers).
/// Platform sync services own SignalR hub wiring and delegate data transfer here.
/// </summary>
public sealed class WebRtcSyncCoordinator : IWebRtcTransportCallbacks, IAsyncDisposable
{
    private readonly IWebRtcTransport _webrtc;
    private readonly IConversationStore _conversationStore;
    private readonly INoteStore _noteStore;
    private readonly IBrowserStore? _browserStore;
    private readonly IBrowserSidebarStore? _sidebarStore;
    private readonly ISyncPreferencesStore _prefs;
    private readonly Func<string, string, string, Task> _sendSignalingAsync;
    private readonly Func<bool> _isHubConnected;
    private readonly IWebRtcTransportCallbacks _transportCallbacks;

    public WebRtcSyncCoordinator(
        IWebRtcTransport webrtc,
        IConversationStore conversationStore,
        INoteStore noteStore,
        ISyncPreferencesStore prefs,
        Func<string, string, string, Task> sendSignalingAsync,
        Func<bool> isHubConnected,
        IWebRtcTransportCallbacks? transportCallbacks = null,
        IBrowserStore? browserStore = null,
        IBrowserSidebarStore? sidebarStore = null)
    {
        _webrtc = webrtc;
        _conversationStore = conversationStore;
        _noteStore = noteStore;
        _prefs = prefs;
        _sendSignalingAsync = sendSignalingAsync;
        _isHubConnected = isHubConnected;
        _transportCallbacks = transportCallbacks ?? this;
        _browserStore = browserStore;
        _sidebarStore = sidebarStore;
    }

    public bool AutoSyncChatHistory { get; set; }
    public bool AutoSyncNotes { get; set; }
    public bool AutoSyncBookmarks { get; set; }
    public bool AutoSyncInstalledApps { get; set; }
    public IReadOnlyCollection<string> SyncTargetDeviceIds { get; set; } = Array.Empty<string>();
    public Func<string, bool>? IsSelf { get; set; }
    public Func<bool>? IsAuthenticated { get; set; }
    public Func<Task>? EnsureConnectedAsync { get; set; }
    public Func<IReadOnlyList<SyncDeviceInfo>>? GetDevices { get; set; }

    public event Action? OnConversationsChanged;
    public event Action? OnNotesChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;

    public Task HandleReceiveSignalingAsync(string fromDeviceId, string type, string payload)
    {
        if (type is "webrtc-offer" or "webrtc-offer-ai")
            SyncDebugLog.WebRtc($"Received signaling '{type}' from {fromDeviceId}");

        return type switch
        {
            "webrtc-offer" => HandleWebRtcOffer(fromDeviceId, payload),
            "webrtc-answer" => HandleWebRtcAnswer(fromDeviceId, payload),
            "webrtc-ice" => HandleWebRtcIce(fromDeviceId, payload),
            _ => Task.CompletedTask
        };
    }

    public Task HandleIncomingSyncPayloadAsync(string convoId, string json, string fromDeviceId) =>
        HandleIncomingSyncPayload(convoId, json, fromDeviceId);

    public void OnDevicesUpdated(IReadOnlyList<SyncDeviceInfo> newDevices, ISet<string> previouslyOnlineDeviceIds)
    {
        foreach (var d in newDevices)
        {
            if (d.IsOnline
                && IsSelf?.Invoke(d.DeviceId) == false
                && !previouslyOnlineDeviceIds.Contains(d.DeviceId))
            {
                ScheduleMaybeAutoSyncPeer(d.DeviceId);
            }
        }
    }
    private readonly Dictionary<string, Queue<SyncQueueItem>> _syncQueues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SyncQueueItem> _activeSyncByPeer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _syncTimeoutByPeer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _autoSyncDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _peerOnlineAutoSyncDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChunkAssembly> _chunkAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SyncItemTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AutoSyncDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeerOnlineAutoSyncDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManifestRecheckCooldown = TimeSpan.FromMinutes(10);
    private const int MaxSyncRetries = 2;
    private sealed class ChunkAssembly
    {
        public required SyncItemKind Kind { get; init; }
        public required string ItemId { get; init; }
        public required int ChunkCount { get; init; }
        public required string[] Parts { get; init; }
        public int PartsReceived { get; set; }
    }

    private sealed class SyncQueueItem
    {
        public required SyncItemKind Kind { get; init; }
        public required string ItemId { get; init; }
        public string? NoteTitle { get; init; }
        public required string DataJson { get; init; }
        public string? ContentFingerprint { get; init; }
        public bool IsDelete { get; init; }
        public long? DeletedAtTicks { get; init; }
        public bool IsManifestExchange { get; init; }
        public bool IncludeConvosInManifest { get; init; }
        public bool IncludeNotesInManifest { get; init; }
        public bool IncludeBookmarksInManifest { get; init; }
        public bool IncludeSidebarAppsInManifest { get; init; }
        public int RetryCount { get; set; }
    }

    private record SyncManifestOffer(
        List<SyncManifestEntry> Convos,
        List<SyncManifestEntry> Notes,
        List<SyncManifestEntry>? Bookmarks = null,
        List<SyncManifestEntry>? BookmarkFolders = null,
        List<SyncManifestEntry>? SidebarApps = null);

    private record SyncManifestResponse(
        List<string> NeededConvos,
        List<string> NeededNotes,
        int UpToDateConvos,
        int UpToDateNotes,
        List<DeleteSyncPayload>? SenderShouldDeleteConvos = null,
        List<DeleteSyncPayload>? SenderShouldDeleteNotes = null,
        List<string>? NeededBookmarks = null,
        List<string>? NeededBookmarkFolders = null,
        List<string>? NeededSidebarApps = null,
        int UpToDateBookmarks = 0,
        int UpToDateBookmarkFolders = 0,
        int UpToDateSidebarApps = 0,
        List<DeleteSyncPayload>? SenderShouldDeleteBookmarks = null,
        List<DeleteSyncPayload>? SenderShouldDeleteBookmarkFolders = null,
        List<DeleteSyncPayload>? SenderShouldDeleteSidebarApps = null);

    private const string SyncAckStateKey = "chatfish-sync-ack-state";
    private const string SyncManifestVerifiedKey = "chatfish-sync-manifest-verified";


    private static bool TryUnwrapIcePayload(string payload, out string peerKey, out string iceJson)
    {
        peerKey = "";
        iceJson = payload;
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("peerKey", out var pk) &&
                doc.RootElement.TryGetProperty("ice", out var ice))
            {
                peerKey = pk.GetString() ?? "";
                iceJson = ice.GetRawText();
                return !string.IsNullOrEmpty(peerKey);
            }
        }
        catch { }
        return false;
    }

    // --- WebRTC DataChannel sync (Phase 2) ---

    public Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages) =>
        EnqueueConvoSyncAsync(targetDeviceId, convoId, messages);

    public Task StartWebRtcNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries) =>
        EnqueueNoteSyncAsync(targetDeviceId, noteId, title, entries);

    public Task StartWebRtcBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark) =>
        EnqueueBookmarkSyncAsync(targetDeviceId, bookmark);

    public Task StartWebRtcFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder) =>
        EnqueueFolderSyncAsync(targetDeviceId, folder);

    public Task StartWebRtcSidebarAppSyncAsync(string targetDeviceId, SidebarApp app) =>
        EnqueueSidebarAppSyncAsync(targetDeviceId, app);

    public async Task EnqueueConvoSyncAsync(
        string targetDeviceId,
        string convoId,
        List<ChatMessage> messages,
        string? title = null,
        bool? titleIsCustom = null)
    {
        if (string.IsNullOrEmpty(targetDeviceId))
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue convo {convoId}: hub not connected.");
            return;
        }

        var titleInfo = await _conversationStore.GetMetaTitleInfoAsync(convoId);
        title ??= titleInfo.Title;
        titleIsCustom ??= titleInfo.TitleIsCustom;
        var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Conversation,
            ItemId = convoId,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForConversation(convoId, title, messages)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueNoteSyncAsync(string targetDeviceId, string noteId, string title, List<ChatMessage> entries)
    {
        if (string.IsNullOrEmpty(targetDeviceId))
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue note {noteId}: hub not connected.");
            return;
        }

        var dataJson = NoteSyncPayload.Serialize(noteId, title, entries);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Note,
            ItemId = noteId,
            NoteTitle = title,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForNote(noteId, title, entries)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueBookmarkSyncAsync(string targetDeviceId, BrowserBookmark bookmark)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _browserStore == null)
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue bookmark {bookmark.Id}: hub not connected.");
            return;
        }

        var dataJson = BookmarkSyncPayload.Serialize(bookmark);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Bookmark,
            ItemId = bookmark.Id,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForBookmark(bookmark)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueFolderSyncAsync(string targetDeviceId, BrowserBookmarkFolder folder)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _browserStore == null)
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue folder {folder.Id}: hub not connected.");
            return;
        }

        var dataJson = BookmarkFolderSyncPayload.Serialize(folder);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.BookmarkFolder,
            ItemId = folder.Id,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForBookmarkFolder(folder)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueSidebarAppSyncAsync(string targetDeviceId, SidebarApp app)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _sidebarStore == null)
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue sidebar app {app.Id}: hub not connected.");
            return;
        }

        var dataJson = SidebarAppSyncPayload.Serialize(app);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.SidebarApp,
            ItemId = app.Id,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForSidebarApp(app)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    /// <summary>
    /// Exchanges a lightweight manifest with each target, then only queues items the peer still needs.
    /// </summary>
    public async Task<int> StartDeltaSyncToDevicesAsync(
        IEnumerable<string> targetDeviceIds,
        bool includeConvos,
        bool includeNotes,
        bool includeBookmarks = false,
        bool includeSidebarApps = false)
    {
        if (_browserStore == null)
            includeBookmarks = false;
        if (_sidebarStore == null)
            includeSidebarApps = false;

        var targets = targetDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0 || !_isHubConnected())
            return 0;

        foreach (var targetId in targets)
            await EnqueueManifestExchangeAsync(targetId, includeConvos, includeNotes, includeBookmarks, includeSidebarApps);

        return targets.Count;
    }

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: true, includeNotes: false);

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: false, includeNotes: true);

    public Task<int> SyncAllBookmarksToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        var filtered = FilterBrowserCapable(targetDeviceIds).ToList();
        SyncDebugLog.Browser(
            $"SyncAllBookmarks requested={string.Join(",", targetDeviceIds)} " +
            $"filtered={string.Join(",", filtered)} " +
            $"browserStore={(_browserStore != null ? "yes" : "NULL")}");
        return StartDeltaSyncToDevicesAsync(filtered, false, false, true, false);
    }

    public Task<int> SyncAllInstalledAppsToDevicesAsync(IEnumerable<string> targetDeviceIds)
    {
        var filtered = FilterBrowserCapable(targetDeviceIds).ToList();
        SyncDebugLog.Browser(
            $"SyncAllInstalledApps requested={string.Join(",", targetDeviceIds)} " +
            $"filtered={string.Join(",", filtered)} " +
            $"sidebarStore={(_sidebarStore != null ? "yes" : "NULL")}");
        return StartDeltaSyncToDevicesAsync(filtered, false, false, false, true);
    }

    private IEnumerable<string> FilterBrowserCapable(IEnumerable<string> targetDeviceIds)
    {
        var targets = targetDeviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var devices = GetDevices?.Invoke()?.ToList() ?? [];
        if (devices.Count == 0)
        {
            SyncDebugLog.Browser(
                $"FilterBrowserCapable: no device list yet; allowing {targets.Count} selected target(s) (capability unknown).");
            return targets;
        }

        var capable = devices
            .Where(d => d.SupportsBrowserSync)
            .Select(d => d.DeviceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = targets.Where(id => capable.Contains(id)).ToList();
        if (filtered.Count == 0 && targets.Count > 0)
        {
            // Hub flag may lag right after register; try targets anyway (WASM null stores no-op).
            SyncDebugLog.Warn(
                $"FilterBrowserCapable: none of {targets.Count} target(s) report SupportsBrowserSync=true " +
                $"(devices: {string.Join(", ", devices.Select(d => $"{d.Name}={(d.SupportsBrowserSync ? "browser" : "no-browser")}"))}). " +
                "Falling back to selected targets.");
            return targets;
        }

        SyncDebugLog.Browser(
            $"FilterBrowserCapable: {filtered.Count}/{targets.Count} target(s) browser-capable.");
        return filtered;
    }

    private async Task EnqueueManifestExchangeAsync(
        string targetDeviceId,
        bool includeConvos,
        bool includeNotes,
        bool includeBookmarks = false,
        bool includeSidebarApps = false)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        if (_browserStore == null)
            includeBookmarks = false;
        if (_sidebarStore == null)
            includeSidebarApps = false;

        if (!includeConvos && !includeNotes && !includeBookmarks && !includeSidebarApps)
            return;

        var manifest = await BuildLocalManifestAsync(includeConvos, includeNotes, includeBookmarks, includeSidebarApps);
        var item = new SyncQueueItem
        {
            IsManifestExchange = true,
            Kind = SyncItemKind.Conversation,
            ItemId = "__manifest__",
            DataJson = System.Text.Json.JsonSerializer.Serialize(manifest),
            IncludeConvosInManifest = includeConvos,
            IncludeNotesInManifest = includeNotes,
            IncludeBookmarksInManifest = includeBookmarks,
            IncludeSidebarAppsInManifest = includeSidebarApps
        };

        if (IsManifestSyncPending(targetDeviceId))
        {
            SyncDebugLog.Info($"Manifest already pending for {targetDeviceId}, skipping duplicate");
            return;
        }

        await EnqueueSyncAsync(targetDeviceId, item, allowDuplicate: false);
    }

    private bool IsManifestSyncPending(string peerId)
    {
        if (_activeSyncByPeer.TryGetValue(peerId, out var active) && active.IsManifestExchange)
            return true;

        return _syncQueues.TryGetValue(peerId, out var queue)
               && queue.Any(i => i.IsManifestExchange);
    }

    private async Task<SyncManifestOffer> BuildLocalManifestAsync(
        bool includeConvos,
        bool includeNotes,
        bool includeBookmarks,
        bool includeSidebarApps)
    {
        var convos = includeConvos
            ? await _conversationStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true)
            : new List<SyncManifestEntry>();
        var notes = includeNotes
            ? await _noteStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true)
            : new List<SyncManifestEntry>();
        List<SyncManifestEntry>? bookmarks = null;
        List<SyncManifestEntry>? folders = null;
        List<SyncManifestEntry>? apps = null;

        if (includeBookmarks && _browserStore != null)
        {
            await _browserStore.LoadAsync();
            bookmarks = await _browserStore.LoadBookmarkManifestEntriesAsync(backfillMissingFingerprints: true);
            folders = await _browserStore.LoadFolderManifestEntriesAsync(backfillMissingFingerprints: true);
            SyncDebugLog.Browser(
                $"BuildLocalManifest bookmarks={bookmarks.Count} folders={folders.Count}");
        }

        if (includeSidebarApps && _sidebarStore != null)
        {
            await _sidebarStore.LoadAsync();
            apps = await _sidebarStore.LoadSidebarAppManifestEntriesAsync(backfillMissingFingerprints: true);
            SyncDebugLog.Browser($"BuildLocalManifest sidebarApps={apps.Count}");
        }

        return new SyncManifestOffer(convos, notes, bookmarks, folders, apps);
    }

    private static bool ManifestEntryNeedsSync(
        SyncManifestEntry remote,
        SyncManifestEntry? local)
    {
        if (remote.IsDeleted)
            return false;

        if (local == null)
            return true;

        if (local.IsDeleted)
            return remote.LastUpdatedTicks > local.DeletedAtTicks!.Value;

        if (!string.IsNullOrEmpty(remote.ContentFingerprint) && string.IsNullOrEmpty(local.ContentFingerprint))
            return true;

        if (!string.IsNullOrEmpty(remote.ContentFingerprint) && !string.IsNullOrEmpty(local.ContentFingerprint))
        {
            if (!string.Equals(remote.ContentFingerprint, local.ContentFingerprint, StringComparison.Ordinal))
                return true;

            if (!string.Equals(remote.Title, local.Title, StringComparison.Ordinal))
                return true;

            return remote.LastUpdatedTicks != local.LastUpdatedTicks;
        }

        return remote.LastUpdatedTicks != local.LastUpdatedTicks;
    }

    private static bool LocalDeleteShouldWinOverRemote(
        SyncManifestEntry remote,
        SyncManifestEntry local) =>
        !remote.IsDeleted
        && local.IsDeleted
        && local.DeletedAtTicks!.Value > remote.LastUpdatedTicks;

    private static string GetAckItemKey(SyncItemKind kind, string itemId) => kind switch
    {
        SyncItemKind.Conversation => $"c:{itemId}",
        SyncItemKind.Note => $"n:{itemId}",
        SyncItemKind.Bookmark => $"b:{itemId}",
        SyncItemKind.BookmarkFolder => $"f:{itemId}",
        SyncItemKind.SidebarApp => $"a:{itemId}",
        _ => $"c:{itemId}"
    };

    private static string KindLabel(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "convo",
        SyncItemKind.Note => "note",
        SyncItemKind.Bookmark => "bookmark",
        SyncItemKind.BookmarkFolder => "folder",
        SyncItemKind.SidebarApp => "app",
        _ => "item"
    };

    private static string ChannelLabelFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "chatfish-sync",
        SyncItemKind.Note => "chatfish-note-sync",
        SyncItemKind.Bookmark => "chatfish-bookmark-sync",
        SyncItemKind.BookmarkFolder => "chatfish-folder-sync",
        SyncItemKind.SidebarApp => "chatfish-app-sync",
        _ => "chatfish-sync"
    };

    private static string DeleteTypeFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "convo-delete",
        SyncItemKind.Note => "note-delete",
        SyncItemKind.Bookmark => "bookmark-delete",
        SyncItemKind.BookmarkFolder => "folder-delete",
        SyncItemKind.SidebarApp => "app-delete",
        _ => "convo-delete"
    };

    private static string DataTypeFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "sync-data",
        SyncItemKind.Note => "note-sync-data",
        SyncItemKind.Bookmark => "bookmark-sync-data",
        SyncItemKind.BookmarkFolder => "folder-sync-data",
        SyncItemKind.SidebarApp => "app-sync-data",
        _ => "sync-data"
    };

    private async Task<Dictionary<string, string>> LoadPeerAckStateAsync(string peerId)
    {
        try
        {
            var json = await _prefs.GetStringAsync(SyncAckStateKey);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            if (all != null && all.TryGetValue(peerId, out var peerState))
                return new Dictionary<string, string>(peerState, StringComparer.OrdinalIgnoreCase);
        }
        catch { }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task SavePeerAckStateAsync(string peerId, Dictionary<string, string> peerState)
    {
        try
        {
            Dictionary<string, Dictionary<string, string>> all;
            var json = await _prefs.GetStringAsync(SyncAckStateKey);
            if (string.IsNullOrWhiteSpace(json))
                all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            else
                all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                      ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            all[peerId] = peerState;
            await _prefs.SetStringAsync(SyncAckStateKey, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch { }
    }

    private async Task<bool> IsItemAcknowledgedAsync(string peerId, SyncItemKind kind, string itemId, string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return false;

        var state = await LoadPeerAckStateAsync(peerId);
        var key = GetAckItemKey(kind, itemId);
        return state.TryGetValue(key, out var ackFp)
               && string.Equals(ackFp, fingerprint, StringComparison.Ordinal);
    }

    private async Task RecordSuccessfulSyncAsync(string peerId, SyncQueueItem item)
    {
        if (item.IsManifestExchange || string.IsNullOrEmpty(item.ContentFingerprint))
            return;

        var state = await LoadPeerAckStateAsync(peerId);
        state[GetAckItemKey(item.Kind, item.ItemId)] = item.ContentFingerprint;
        await SavePeerAckStateAsync(peerId, state);
    }

    private async Task ClearPeerAckForItemAsync(SyncItemKind kind, string itemId)
    {
        try
        {
            var json = await _prefs.GetStringAsync(SyncAckStateKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            if (all == null || all.Count == 0)
                return;

            var key = GetAckItemKey(kind, itemId);
            var changed = false;
            foreach (var peerState in all.Values)
            {
                if (peerState.Remove(key))
                    changed = true;
            }

            if (changed)
            {
                await _prefs.SetStringAsync(SyncAckStateKey, System.Text.Json.JsonSerializer.Serialize(all));
            }
        }
        catch { }
    }

    public async Task EnqueueConvoDeleteAsync(string targetDeviceId, string convoId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Conversation,
            IsDelete = true,
            ItemId = convoId,
            DataJson = DeleteSyncPayload.Serialize(convoId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueNoteDeleteAsync(string targetDeviceId, string noteId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Note,
            IsDelete = true,
            ItemId = noteId,
            DataJson = DeleteSyncPayload.Serialize(noteId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueBookmarkDeleteAsync(string targetDeviceId, string bookmarkId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Bookmark,
            IsDelete = true,
            ItemId = bookmarkId,
            DataJson = DeleteSyncPayload.Serialize(bookmarkId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueFolderDeleteAsync(string targetDeviceId, string folderId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.BookmarkFolder,
            IsDelete = true,
            ItemId = folderId,
            DataJson = DeleteSyncPayload.Serialize(folderId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueSidebarAppDeleteAsync(string targetDeviceId, string appId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.SidebarApp,
            IsDelete = true,
            ItemId = appId,
            DataJson = DeleteSyncPayload.Serialize(appId, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    private async Task<int> QueueNeededItemsFromManifestAsync(string peerId, SyncManifestResponse response)
    {
        var queued = 0;

        foreach (var convoId in response.NeededConvos.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var messages = await _conversationStore.LoadConversationAsync(convoId);
            var (title, titleIsCustom) = await _conversationStore.GetMetaTitleInfoAsync(convoId);
            var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom);
            var fingerprint = SyncFingerprint.ForConversation(convoId, title, messages);

            var item = new SyncQueueItem
            {
                Kind = SyncItemKind.Conversation,
                ItemId = convoId,
                DataJson = dataJson,
                ContentFingerprint = fingerprint
            };
            if (!IsAlreadyQueuedOrActive(peerId, item))
            {
                await EnqueueSyncAsync(peerId, item, allowDuplicate: true);
                queued++;
            }
        }

        foreach (var noteId in response.NeededNotes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var index = await _noteStore.LoadIndexAsync();
            var title = index.FirstOrDefault(n => n.Id == noteId)?.Title ?? noteId;
            var entries = await _noteStore.LoadNoteAsync(noteId);
            var dataJson = NoteSyncPayload.Serialize(noteId, title, entries);
            var fingerprint = SyncFingerprint.ForNote(noteId, title, entries);

            var item = new SyncQueueItem
            {
                Kind = SyncItemKind.Note,
                ItemId = noteId,
                NoteTitle = title,
                DataJson = dataJson,
                ContentFingerprint = fingerprint
            };
            if (!IsAlreadyQueuedOrActive(peerId, item))
            {
                await EnqueueSyncAsync(peerId, item, allowDuplicate: true);
                queued++;
            }
        }

        // Folders first so bookmarks can resolve folder membership on the peer.
        if (_browserStore != null)
        {
            foreach (var folderId in (response.NeededBookmarkFolders ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var folder = await _browserStore.GetFolderByIdAsync(folderId);
                if (folder == null)
                    continue;

                var dataJson = BookmarkFolderSyncPayload.Serialize(folder);
                var item = new SyncQueueItem
                {
                    Kind = SyncItemKind.BookmarkFolder,
                    ItemId = folder.Id,
                    DataJson = dataJson,
                    ContentFingerprint = SyncFingerprint.ForBookmarkFolder(folder)
                };
                if (!IsAlreadyQueuedOrActive(peerId, item))
                {
                    await EnqueueSyncAsync(peerId, item, allowDuplicate: true);
                    queued++;
                }
            }

            foreach (var bookmarkId in (response.NeededBookmarks ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var bookmark = await _browserStore.GetBookmarkByIdAsync(bookmarkId);
                if (bookmark == null)
                    continue;

                var dataJson = BookmarkSyncPayload.Serialize(bookmark);
                var item = new SyncQueueItem
                {
                    Kind = SyncItemKind.Bookmark,
                    ItemId = bookmark.Id,
                    DataJson = dataJson,
                    ContentFingerprint = SyncFingerprint.ForBookmark(bookmark)
                };
                if (!IsAlreadyQueuedOrActive(peerId, item))
                {
                    await EnqueueSyncAsync(peerId, item, allowDuplicate: true);
                    queued++;
                }
            }
        }

        if (_sidebarStore != null)
        {
            foreach (var appId in (response.NeededSidebarApps ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var app = await _sidebarStore.GetAppByIdAsync(appId);
                if (app == null)
                    continue;

                var dataJson = SidebarAppSyncPayload.Serialize(app);
                var item = new SyncQueueItem
                {
                    Kind = SyncItemKind.SidebarApp,
                    ItemId = app.Id,
                    DataJson = dataJson,
                    ContentFingerprint = SyncFingerprint.ForSidebarApp(app)
                };
                if (!IsAlreadyQueuedOrActive(peerId, item))
                {
                    await EnqueueSyncAsync(peerId, item, allowDuplicate: true);
                    queued++;
                }
            }
        }

        SyncDebugLog.Info($"Manifest result for {peerId}: " +
            $"{response.UpToDateConvos} convo(s), {response.UpToDateNotes} note(s), " +
            $"{response.UpToDateBookmarkFolders} folder(s), {response.UpToDateBookmarks} bookmark(s), " +
            $"{response.UpToDateSidebarApps} app(s) up to date; queued {queued} item(s)");

        return queued;
    }

    private async Task EnqueueSyncAsync(string targetDeviceId, SyncQueueItem item, bool allowDuplicate = false)
    {
        if (!allowDuplicate && IsAlreadyQueuedOrActive(targetDeviceId, item))
        {
            SyncDebugLog.Info($"Skipping duplicate {KindLabel(item.Kind)} " +
                $"{item.ItemId} for {targetDeviceId}");
            return;
        }

        if (!_syncQueues.TryGetValue(targetDeviceId, out var queue))
        {
            queue = new Queue<SyncQueueItem>();
            _syncQueues[targetDeviceId] = queue;
        }

        queue.Enqueue(item);
        var itemLabel = item.IsManifestExchange
            ? "manifest"
            : item.IsDelete
                ? $"{KindLabel(item.Kind)} delete {item.ItemId}"
                : $"{KindLabel(item.Kind)} {item.ItemId}";
        SyncDebugLog.Info($"Enqueued {itemLabel} for {targetDeviceId} (queue depth: {queue.Count})");
        await ProcessSyncQueueAsync(targetDeviceId);
    }

    private async Task ProcessSyncQueueAsync(string targetDeviceId)
    {
        if (_activeSyncByPeer.TryGetValue(targetDeviceId, out var active))
        {
            var pending = _syncQueues.TryGetValue(targetDeviceId, out var q) ? q.Count : 0;
            SyncDebugLog.Info($"Sync queue for {targetDeviceId} waiting " +
                $"({pending} pending, active: {DescribeQueueItem(active)})");
            return;
        }

        if (!_syncQueues.TryGetValue(targetDeviceId, out var queue) || queue.Count == 0)
            return;

        var item = queue.Dequeue();
        _activeSyncByPeer[targetDeviceId] = item;
        StartSyncTimeout(targetDeviceId);

        try
        {
            var channelLabel = item.IsManifestExchange
                ? "chatfish-sync-manifest"
                : ChannelLabelFor(item.Kind);
            var label = item.IsManifestExchange
                ? "manifest"
                : item.IsDelete
                    ? $"{KindLabel(item.Kind)} delete {item.ItemId}"
                    : $"{KindLabel(item.Kind)} {item.ItemId}";
            SyncDebugLog.Info($"Starting WebRTC sync for {targetDeviceId}: {label}");
            await StartWebRtcDataChannelAsync(targetDeviceId, channelLabel);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"WebRTC sync start failed: {ex.Message}");
            await FailActiveSyncAsync(targetDeviceId, ex.Message);
        }
    }

    private void StartSyncTimeout(string peerId)
    {
        CancelSyncTimeout(peerId);
        var cts = new CancellationTokenSource();
        _syncTimeoutByPeer[peerId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SyncItemTimeout, cts.Token);
                if (!cts.Token.IsCancellationRequested && _activeSyncByPeer.ContainsKey(peerId))
                {
                    SyncDebugLog.Info($"Sync timed out for peer {peerId} after {SyncItemTimeout.TotalSeconds}s");
                    await FailActiveSyncAsync(peerId, "timed out waiting for peer acknowledgement");
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    private void CancelSyncTimeout(string peerId)
    {
        if (_syncTimeoutByPeer.Remove(peerId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private async Task FailActiveSyncAsync(string peerId, string reason)
    {
        if (!_activeSyncByPeer.TryGetValue(peerId, out var failedItem))
            return;

        _activeSyncByPeer.Remove(peerId);
        CancelSyncTimeout(peerId);
        ClearChunkAssembliesForPeer(peerId);
        SyncDebugLog.Info($"Active sync failed for {peerId}: {reason}");
        try { await CloseWebRtcPeerAsync(peerId); } catch { }

        if (failedItem.RetryCount < MaxSyncRetries)
        {
            failedItem.RetryCount++;
            if (!_syncQueues.TryGetValue(peerId, out var queue))
            {
                queue = new Queue<SyncQueueItem>();
                _syncQueues[peerId] = queue;
            }

            queue.Enqueue(failedItem);
            SyncDebugLog.Info($"Re-queued {KindLabel(failedItem.Kind)} {failedItem.ItemId} " +
                $"for {peerId} (retry {failedItem.RetryCount}/{MaxSyncRetries})");
        }

        await ProcessSyncQueueAsync(peerId);
    }

    private bool IsAlreadyQueuedOrActive(string targetDeviceId, SyncQueueItem item)
    {
        if (_activeSyncByPeer.TryGetValue(targetDeviceId, out var active)
            && ItemsMatchForDedup(active, item))
            return true;

        return _syncQueues.TryGetValue(targetDeviceId, out var queue)
               && queue.Any(i => ItemsMatchForDedup(i, item));
    }

    private static string DescribeQueueItem(SyncQueueItem item)
    {
        if (item.IsManifestExchange)
            return "manifest";
        if (item.IsDelete)
            return $"{KindLabel(item.Kind)} delete {item.ItemId}";
        return $"{KindLabel(item.Kind)} {item.ItemId}";
    }

    private static bool ItemsMatchForDedup(SyncQueueItem a, SyncQueueItem b)
    {
        if (a.IsManifestExchange || b.IsManifestExchange)
            return a.IsManifestExchange && b.IsManifestExchange;

        return a.Kind == b.Kind
               && a.IsDelete == b.IsDelete
               && string.Equals(a.ItemId, b.ItemId, StringComparison.Ordinal);
    }

    /// <summary>
    /// After a local conversation save, push updates to online sync targets when auto-sync is enabled.
    /// </summary>
    public void ScheduleAutoSyncConvoAfterLocalSave(string convoId, string? title = null)
    {
        if (!AutoSyncChatHistory || SyncTargetDeviceIds.Count == 0)
            return;

        var forceTitleSync = !string.IsNullOrWhiteSpace(title);

        _ = DebouncedAutoSyncAsync($"convo:{convoId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            if (forceTitleSync)
                await ClearPeerAckForItemAsync(SyncItemKind.Conversation, convoId);

            var manifest = await _conversationStore.LoadManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(c => c.Id == convoId);
            var fingerprint = entry?.ContentFingerprint;

            var messages = await _conversationStore.LoadConversationAsync(convoId);
            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (!forceTitleSync
                    && await IsItemAcknowledgedAsync(targetId, SyncItemKind.Conversation, convoId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping convo {convoId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueConvoSyncAsync(targetId, convoId, messages, title);
            }
        });
    }

    /// <summary>
    /// After a local note save, push updates to online sync targets when auto-sync is enabled.
    /// </summary>
    public void ScheduleAutoSyncConvoDeleteAfterLocalDelete(string convoId, DateTime deletedAtUtc)
    {
        if (!AutoSyncChatHistory || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"convo-delete:{convoId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Conversation, convoId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping convo delete {convoId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueConvoDeleteAsync(targetId, convoId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAtUtc)
    {
        if (!AutoSyncNotes || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"note-delete:{noteId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Note, noteId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping note delete {noteId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueNoteDeleteAsync(targetId, noteId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title)
    {
        if (!AutoSyncNotes || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"note:{noteId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            var manifest = await _noteStore.LoadManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(n => n.Id == noteId);
            var fingerprint = entry?.ContentFingerprint;

            var entries = await _noteStore.LoadNoteAsync(noteId);
            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Note, noteId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping note {noteId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueNoteSyncAsync(targetId, noteId, title, entries);
            }
        });
    }

    public void ScheduleAutoSyncBookmarkAfterLocalSave(string bookmarkId)
    {
        if (!AutoSyncBookmarks || _browserStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"bookmark:{bookmarkId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _browserStore == null)
                return;

            var bookmark = await _browserStore.GetBookmarkByIdAsync(bookmarkId);
            if (bookmark == null)
                return;

            var fingerprint = SyncFingerprint.ForBookmark(bookmark);
            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Bookmark, bookmarkId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping bookmark {bookmarkId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueBookmarkSyncAsync(targetId, bookmark);
            }
        });
    }

    public void ScheduleAutoSyncBookmarkDeleteAfterLocalDelete(string bookmarkId, DateTime deletedAtUtc)
    {
        if (!AutoSyncBookmarks || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"bookmark-delete:{bookmarkId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Bookmark, bookmarkId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping bookmark delete {bookmarkId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueBookmarkDeleteAsync(targetId, bookmarkId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncFolderAfterLocalSave(string folderId)
    {
        if (!AutoSyncBookmarks || _browserStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"folder:{folderId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _browserStore == null)
                return;

            var folder = await _browserStore.GetFolderByIdAsync(folderId);
            if (folder == null)
                return;

            var fingerprint = SyncFingerprint.ForBookmarkFolder(folder);
            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.BookmarkFolder, folderId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping folder {folderId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueFolderSyncAsync(targetId, folder);
            }
        });
    }

    public void ScheduleAutoSyncFolderDeleteAfterLocalDelete(string folderId, DateTime deletedAtUtc)
    {
        if (!AutoSyncBookmarks || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"folder-delete:{folderId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.BookmarkFolder, folderId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping folder delete {folderId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueFolderDeleteAsync(targetId, folderId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncSidebarAppAfterLocalSave(string appId)
    {
        if (!AutoSyncInstalledApps || _sidebarStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"app:{appId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _sidebarStore == null)
                return;

            var app = await _sidebarStore.GetAppByIdAsync(appId);
            if (app == null)
                return;

            var fingerprint = SyncFingerprint.ForSidebarApp(app);
            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.SidebarApp, appId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping sidebar app {appId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueSidebarAppSyncAsync(targetId, app);
            }
        });
    }

    public void ScheduleAutoSyncSidebarAppDeleteAfterLocalDelete(string appId, DateTime deletedAtUtc)
    {
        if (!AutoSyncInstalledApps || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"app-delete:{appId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineBrowserSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.SidebarApp, appId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping sidebar app delete {appId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueSidebarAppDeleteAsync(targetId, appId, deletedAtUtc);
            }
        });
    }

    private IEnumerable<string> GetOnlineSyncTargetIdsInternal() =>
        (GetDevices?.Invoke() ?? Array.Empty<SyncDeviceInfo>())
            .Where(d => d.IsOnline
                        && IsSelf?.Invoke(d.DeviceId) == false
                        && SyncTargetDeviceIds.Any(id => string.Equals(id, d.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.DeviceId);

    private IEnumerable<string> GetOnlineBrowserSyncTargetIdsInternal()
    {
        var devices = (GetDevices?.Invoke() ?? Array.Empty<SyncDeviceInfo>()).ToList();
        var targets = devices
            .Where(d => d.IsOnline
                        && IsSelf?.Invoke(d.DeviceId) == false
                        && SyncTargetDeviceIds.Any(id => string.Equals(id, d.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var capable = targets.Where(d => d.SupportsBrowserSync).Select(d => d.DeviceId).ToList();
        if (capable.Count > 0)
            return capable;

        // Hub capability lag: still try configured online targets so first auto-sync is not a no-op.
        if (targets.Count > 0)
        {
            SyncDebugLog.Warn(
                "GetOnlineBrowserSyncTargetIds: no peer has SupportsBrowserSync yet; using all online sync targets.");
        }

        return targets.Select(d => d.DeviceId);
    }

    private bool DeviceSupportsBrowserSync(string deviceId)
    {
        var devices = GetDevices?.Invoke();
        if (devices == null)
            return true; // unknown list — do not block peer-online browser auto-sync

        var match = devices.FirstOrDefault(d =>
            string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            return true;

        // If nobody in the list has advertised yet, treat as capable (register race).
        if (!devices.Any(d => d.SupportsBrowserSync))
            return true;

        return match.SupportsBrowserSync;
    }

    private async Task DebouncedAutoSyncAsync(string key, Func<Task> action)
    {
        if (_autoSyncDebounce.TryGetValue(key, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _autoSyncDebounce[key] = cts;

        try
        {
            await Task.Delay(AutoSyncDebounce, cts.Token);
            await action();
        }
        catch (TaskCanceledException) { }
        finally
        {
            if (_autoSyncDebounce.TryGetValue(key, out var current) && current == cts)
                _autoSyncDebounce.Remove(key);
            cts.Dispose();
        }
    }

    private static bool ManifestEntryHasPendingAck(
        Dictionary<string, string> ackState,
        SyncItemKind kind,
        SyncManifestEntry entry)
    {
        var key = GetAckItemKey(kind, entry.Id);
        var expectedAck = entry.IsDeleted && entry.DeletedAtTicks.HasValue
            ? DeleteSyncPayload.AckValue(entry.DeletedAtTicks.Value)
            : entry.ContentFingerprint;

        if (string.IsNullOrEmpty(expectedAck))
            return !ackState.ContainsKey(key);

        return !ackState.TryGetValue(key, out var ackFp)
               || !string.Equals(ackFp, expectedAck, StringComparison.Ordinal);
    }

    private async Task<bool> HasPendingOutboundSyncAsync(
        string peerId,
        bool includeConvos,
        bool includeNotes,
        bool includeBookmarks = false,
        bool includeSidebarApps = false)
    {
        var ackState = await LoadPeerAckStateAsync(peerId);

        if (includeConvos)
        {
            var convos = await _conversationStore.LoadManifestEntriesAsync();
            if (convos.Any(c => ManifestEntryHasPendingAck(ackState, SyncItemKind.Conversation, c)))
                return true;
        }

        if (includeNotes)
        {
            var notes = await _noteStore.LoadManifestEntriesAsync();
            if (notes.Any(n => ManifestEntryHasPendingAck(ackState, SyncItemKind.Note, n)))
                return true;
        }

        if (includeBookmarks && _browserStore != null)
        {
            var folders = await _browserStore.LoadFolderManifestEntriesAsync();
            if (folders.Any(f => ManifestEntryHasPendingAck(ackState, SyncItemKind.BookmarkFolder, f)))
                return true;

            var bookmarks = await _browserStore.LoadBookmarkManifestEntriesAsync();
            if (bookmarks.Any(b => ManifestEntryHasPendingAck(ackState, SyncItemKind.Bookmark, b)))
                return true;
        }

        if (includeSidebarApps && _sidebarStore != null)
        {
            var apps = await _sidebarStore.LoadSidebarAppManifestEntriesAsync();
            if (apps.Any(a => ManifestEntryHasPendingAck(ackState, SyncItemKind.SidebarApp, a)))
                return true;
        }

        return false;
    }

    private async Task<DateTime?> GetLastManifestVerifiedUtcAsync(string peerId)
    {
        try
        {
            var json = await _prefs.GetStringAsync(SyncManifestVerifiedKey);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json);
            if (all != null && all.TryGetValue(peerId, out var ticks))
                return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch { }

        return null;
    }

    private async Task RecordManifestVerifiedAsync(string peerId)
    {
        try
        {
            Dictionary<string, long> all;
            var json = await _prefs.GetStringAsync(SyncManifestVerifiedKey);
            if (string.IsNullOrWhiteSpace(json))
                all = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            else
                all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                      ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            all[peerId] = DateTime.UtcNow.Ticks;
            await _prefs.SetStringAsync(SyncManifestVerifiedKey, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch { }
    }

    private void ScheduleMaybeAutoSyncPeer(string deviceId)
    {
        if (_peerOnlineAutoSyncDebounce.TryGetValue(deviceId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _peerOnlineAutoSyncDebounce[deviceId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PeerOnlineAutoSyncDebounce, cts.Token);
                await MaybeAutoSyncPeerAsync(deviceId);
            }
            catch (TaskCanceledException) { }
            finally
            {
                if (_peerOnlineAutoSyncDebounce.TryGetValue(deviceId, out var current) && current == cts)
                    _peerOnlineAutoSyncDebounce.Remove(deviceId);
                cts.Dispose();
            }
        });
    }

    private async Task MaybeAutoSyncPeerAsync(string deviceId)
    {
        if (IsAuthenticated?.Invoke() != true
            || IsSelf?.Invoke(deviceId) == true
            || !SyncTargetDeviceIds.Any(id => string.Equals(id, deviceId, StringComparison.OrdinalIgnoreCase)))
            return;

        var supportsBrowser = DeviceSupportsBrowserSync(deviceId);
        var includeBookmarks = AutoSyncBookmarks && supportsBrowser && _browserStore != null;
        var includeApps = AutoSyncInstalledApps && supportsBrowser && _sidebarStore != null;

        if (!AutoSyncChatHistory && !AutoSyncNotes && !includeBookmarks && !includeApps)
            return;

        try
        {
            if (!await HasPendingOutboundSyncAsync(
                    deviceId,
                    AutoSyncChatHistory,
                    AutoSyncNotes,
                    includeBookmarks,
                    includeApps))
            {
                SyncDebugLog.Info($"Skipping auto-sync for {deviceId} (all items already acknowledged)");
                return;
            }

            var lastVerified = await GetLastManifestVerifiedUtcAsync(deviceId);
            if (lastVerified.HasValue && DateTime.UtcNow - lastVerified.Value < ManifestRecheckCooldown)
            {
                var minutesAgo = (int)(DateTime.UtcNow - lastVerified.Value).TotalMinutes;
                SyncDebugLog.Info($"Skipping auto-sync for {deviceId} " +
                    $"(manifest verified {minutesAgo}m ago)");
                return;
            }

            await EnqueueManifestExchangeAsync(
                deviceId,
                AutoSyncChatHistory,
                AutoSyncNotes,
                includeBookmarks,
                includeApps);
            SyncDebugLog.Info($"Auto-sync manifest queued for {deviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Auto-sync failed for {deviceId}: {ex.Message}");
        }
    }

    private async Task AdvanceSyncQueueAsync(string peerId)
    {
        if (!_activeSyncByPeer.Remove(peerId))
            return;

        CancelSyncTimeout(peerId);
        ClearChunkAssembliesForPeer(peerId);

        if (!_syncQueues.TryGetValue(peerId, out var queue) || queue.Count == 0)
        {
            try { await CloseWebRtcPeerAsync(peerId); } catch { }
            return;
        }

        var nextItem = queue.Dequeue();
        _activeSyncByPeer[peerId] = nextItem;
        StartSyncTimeout(peerId);

        try
        {
            var channelOpen = await _webrtc.IsDataChannelOpenAsync(peerId);
            if (channelOpen)
            {
                var reuseLabel = DescribeQueueItem(nextItem);
                SyncDebugLog.Info($"Reusing open channel for {peerId}: {reuseLabel}");

                if (nextItem.IsManifestExchange)
                {
                    var sent = await _webrtc.SendDataAsync(
                        peerId,
                        SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-offer", content: nextItem.DataJson)));
                    if (!sent)
                        await FailActiveSyncAsync(peerId, "data channel not ready for manifest");
                }
                else
                {
                    await TrySendActiveItemAsync(peerId, nextItem);
                }

                return;
            }

            var channelLabel = nextItem.IsManifestExchange
                ? "chatfish-sync-manifest"
                : ChannelLabelFor(nextItem.Kind);
            var label = nextItem.IsManifestExchange
                ? "manifest"
                : $"{KindLabel(nextItem.Kind)} {nextItem.ItemId}";
            SyncDebugLog.Info($"Starting WebRTC sync for {peerId}: {label}");
            await StartWebRtcDataChannelAsync(peerId, channelLabel);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"WebRTC sync start failed: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    private Task CloseWebRtcPeerAsync(string peerId) =>
        _webrtc.CloseAsync(peerId, suppressCallbacks: true);

    private void ClearChunkAssembliesForPeer(string peerId)
    {
        var prefix = $"{peerId}:";
        foreach (var key in _chunkAssemblies.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _chunkAssemblies.Remove(key);
    }

    private async Task<bool> SendSyncPayloadAsync(string peerId, SyncItemKind kind, string itemId, string contentJson)
    {
        var maxMessageSize = await _webrtc.GetMaxMessageSizeAsync(peerId);
        var chunkPayloadBytes = Math.Max(4096, (int)(maxMessageSize * 0.7) - 256);
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(contentJson);

        if (contentBytes.Length <= chunkPayloadBytes)
        {
            var dataType = DataTypeFor(kind);
            var msg = kind switch
            {
                SyncItemKind.Conversation => new DataChannelMessage(dataType, convoId: itemId, content: contentJson),
                SyncItemKind.Note => new DataChannelMessage(dataType, content: contentJson),
                _ => new DataChannelMessage(dataType, content: contentJson, itemId: itemId)
            };
            return await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(msg));
        }

        var chunkType = kind switch
        {
            SyncItemKind.Conversation => "sync-chunk",
            SyncItemKind.Note => "note-sync-chunk",
            SyncItemKind.Bookmark => "bookmark-sync-chunk",
            SyncItemKind.BookmarkFolder => "folder-sync-chunk",
            SyncItemKind.SidebarApp => "app-sync-chunk",
            _ => "sync-chunk"
        };

        var chunkCount = (contentBytes.Length + chunkPayloadBytes - 1) / chunkPayloadBytes;
        SyncDebugLog.Info($"Chunking sync payload for {itemId}: {contentBytes.Length} bytes -> {chunkCount} chunk(s)");

        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkPayloadBytes;
            var length = Math.Min(chunkPayloadBytes, contentBytes.Length - offset);
            var slice = System.Text.Encoding.UTF8.GetString(contentBytes, offset, length);

            var chunkMsg = new DataChannelMessage(
                chunkType,
                convoId: kind == SyncItemKind.Conversation ? itemId : null,
                noteId: kind == SyncItemKind.Note ? itemId : null,
                chunkIndex: i,
                chunkCount: chunkCount,
                chunkData: slice,
                itemId: kind is SyncItemKind.Bookmark or SyncItemKind.BookmarkFolder or SyncItemKind.SidebarApp
                    ? itemId
                    : null);

            var sent = await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(chunkMsg));
            if (!sent)
                return false;
        }

        return true;
    }

    private static string SerializeDataChannelMessage(DataChannelMessage msg) =>
        System.Text.Json.JsonSerializer.Serialize(msg);

    private bool TryAddChunk(
        string peerId,
        string itemId,
        SyncItemKind kind,
        int? chunkIndex,
        int? chunkCount,
        string? chunkData,
        out string? completeJson)
    {
        completeJson = null;
        if (chunkIndex is null or < 0 || chunkCount is null or < 1 || string.IsNullOrEmpty(chunkData))
            return false;

        var key = $"{peerId}:{kind}:{itemId}";
        if (!_chunkAssemblies.TryGetValue(key, out var assembly))
        {
            assembly = new ChunkAssembly
            {
                Kind = kind,
                ItemId = itemId,
                ChunkCount = chunkCount.Value,
                Parts = new string[chunkCount.Value]
            };
            _chunkAssemblies[key] = assembly;
        }

        if (assembly.ChunkCount != chunkCount.Value)
            return false;

        if (assembly.Parts[chunkIndex.Value] is null)
            assembly.PartsReceived++;

        assembly.Parts[chunkIndex.Value] = chunkData;

        if (assembly.PartsReceived < assembly.ChunkCount || assembly.Parts.Any(p => p is null))
            return false;

        _chunkAssemblies.Remove(key);
        completeJson = string.Concat(assembly.Parts);
        return true;
    }

    private async Task StartWebRtcDataChannelAsync(string targetDeviceId, string channelLabel)
    {
        await _webrtc.CreatePeerConnectionAsync(targetDeviceId, _transportCallbacks);
        await _webrtc.CreateDataChannelAsync(targetDeviceId, channelLabel);

        var offerJson = await _webrtc.CreateOfferAsync(targetDeviceId);
        SyncDebugLog.WebRtc($"Sending offer to {targetDeviceId}");
        await _sendSignalingAsync(targetDeviceId, "webrtc-offer", offerJson ?? "");
    }

    private async Task HandleWebRtcOffer(string fromDeviceId, string offerJson)
    {
                SyncDebugLog.WebRtc($"Received offer from {fromDeviceId}");

        try
        {
            await _webrtc.CreatePeerConnectionAsync(fromDeviceId, _transportCallbacks);
            await _webrtc.SetRemoteDescriptionAsync(fromDeviceId, offerJson);

            var answerJson = await _webrtc.CreateAnswerAsync(fromDeviceId);
            await _sendSignalingAsync(fromDeviceId, "webrtc-answer", answerJson ?? "");
            SyncDebugLog.WebRtc($"Sent answer to {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Handle offer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswer(string fromDeviceId, string answerJson)
    {
        try
        {
            var applied = await _webrtc.SetRemoteDescriptionAsync(fromDeviceId, answerJson);
            if (applied)
                SyncDebugLog.WebRtc($"Applied answer from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Handle answer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcIce(string fromDeviceId, string payload)
    {
        if (TryUnwrapIcePayload(payload, out _, out var iceJson))
            await _webrtc.AddIceCandidateAsync(fromDeviceId, iceJson);
        else
            await _webrtc.AddIceCandidateAsync(fromDeviceId, payload);
    }

    public async Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        if (!_isHubConnected())
            return;

        var signalingPayload = JsonSerializer.Serialize(new
        {
            peerKey = peerId,
            ice = JsonDocument.Parse(candidateJson).RootElement
        });

        await _sendSignalingAsync(peerId, "webrtc-ice", signalingPayload);
    }

    public async Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default)
    {
        SyncDebugLog.Info($"DataChannel open for peer {peerId}");

        if (!_activeSyncByPeer.TryGetValue(peerId, out var item))
            return;

        if (item.IsManifestExchange)
        {
            try
            {
                var sent = await _webrtc.SendDataAsync(
                    peerId,
                    SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-offer", content: item.DataJson)));
                if (!sent)
                    await FailActiveSyncAsync(peerId, "data channel not ready for manifest");
                else
                    SyncDebugLog.Info($"Sent sync-manifest-offer to {peerId}");
            }
            catch (Exception ex)
            {
                await FailActiveSyncAsync(peerId, ex.Message);
            }

            return;
        }

        await TrySendActiveItemAsync(peerId, item);
    }

    private async Task TrySendActiveItemAsync(string peerId, SyncQueueItem item)
    {
        try
        {
            if (item.IsDelete)
            {
                var deleteType = DeleteTypeFor(item.Kind);
                SyncDebugLog.Info($"Sending {KindLabel(item.Kind)} delete for {item.ItemId} to {peerId}");
                var msg = new DataChannelMessage(deleteType, content: item.DataJson);
                var sent = await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(msg));
                if (!sent)
                {
                    await FailActiveSyncAsync(peerId, "data channel not ready for delete");
                    return;
                }

                SyncDebugLog.Info($"Sent {deleteType} to {peerId} for {item.ItemId}");
                return;
            }

            var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(item.DataJson);
            SyncDebugLog.Info($"Preparing {KindLabel(item.Kind)} sync payload " +
                $"for {item.ItemId} ({payloadBytes} bytes)");

            var payloadSent = await SendSyncPayloadAsync(peerId, item.Kind, item.ItemId, item.DataJson);
            if (!payloadSent)
            {
                SyncDebugLog.Info($"webrtcSendData failed (channel not ready) for {peerId}");
                await FailActiveSyncAsync(peerId, "data channel not ready for send");
                return;
            }

            SyncDebugLog.Info($"Sent {DataTypeFor(item.Kind)} " +
                $"over DataChannel to {peerId} for {item.ItemId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"DataChannel send failed for {peerId}: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    public void OnConnectionStateChange(string peerId, string state)
    {
        if (!_activeSyncByPeer.ContainsKey(peerId))
            return;

        if (state is "failed" or "disconnected" or "closed")
            _ = FailActiveSyncAsync(peerId, $"WebRTC connection {state}");
    }

    public async Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default)
    {
        try
        {
            var msg = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(data);
            if (msg == null) return;

            if ((msg.type == "sync-data" || msg.type == "sync-chunk")
                && msg.convoId != null)
            {
                var contentJson = msg.content;
                if (msg.type == "sync-chunk")
                {
                    if (!TryAddChunk(peerId, msg.convoId, SyncItemKind.Conversation, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingSyncPayload(msg.convoId, contentJson, peerId);

                var ack = new DataChannelMessage("sync-ack", msg.convoId);
                await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
            }
            else if (msg.type == "sync-ack" && msg.convoId != null)
            {
                HandleSyncAck(msg.convoId, peerId);
            }
            else if ((msg.type == "note-sync-data" || msg.type == "note-sync-chunk")
                     && (msg.content != null || msg.noteId != null))
            {
                var contentJson = msg.content;
                var noteId = msg.noteId;

                if (msg.type == "note-sync-chunk")
                {
                    if (noteId == null
                        || !TryAddChunk(peerId, noteId, SyncItemKind.Note, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingNoteSyncPayload(contentJson, peerId);

                var payload = NoteSyncPayload.Deserialize(contentJson);
                if (payload?.NoteId != null)
                {
                    var ack = new DataChannelMessage("note-sync-ack", noteId: payload.NoteId);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "note-sync-ack" && msg.noteId != null)
            {
                HandleNoteSyncAck(msg.noteId, peerId);
            }
            else if ((msg.type == "bookmark-sync-data" || msg.type == "bookmark-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "bookmark-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.Bookmark, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingBookmarkSyncPayload(contentJson, peerId);

                var payload = BookmarkSyncPayload.Deserialize(contentJson);
                if (payload?.Bookmark?.Id != null)
                {
                    var ack = new DataChannelMessage("bookmark-sync-ack", itemId: payload.Bookmark.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "bookmark-sync-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("bookmark-sync-ack", msg.itemId, peerId);
            }
            else if ((msg.type == "folder-sync-data" || msg.type == "folder-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "folder-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.BookmarkFolder, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingFolderSyncPayload(contentJson, peerId);

                var payload = BookmarkFolderSyncPayload.Deserialize(contentJson);
                if (payload?.Folder?.Id != null)
                {
                    var ack = new DataChannelMessage("folder-sync-ack", itemId: payload.Folder.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "folder-sync-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("folder-sync-ack", msg.itemId, peerId);
            }
            else if ((msg.type == "app-sync-data" || msg.type == "app-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "app-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.SidebarApp, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingSidebarAppSyncPayload(contentJson, peerId);

                var payload = SidebarAppSyncPayload.Deserialize(contentJson);
                if (payload?.App?.Id != null)
                {
                    var ack = new DataChannelMessage("app-sync-ack", itemId: payload.App.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "app-sync-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("app-sync-ack", msg.itemId, peerId);
            }
            else if (msg.type == "convo-delete" && msg.content != null)
            {
                await HandleIncomingConvoDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("convo-delete-ack", convoId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "convo-delete-ack" && msg.convoId != null)
            {
                HandleConvoDeleteAck(msg.convoId, peerId);
            }
            else if (msg.type == "note-delete" && msg.content != null)
            {
                await HandleIncomingNoteDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("note-delete-ack", noteId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "note-delete-ack" && msg.noteId != null)
            {
                HandleNoteDeleteAck(msg.noteId, peerId);
            }
            else if (msg.type == "bookmark-delete" && msg.content != null)
            {
                await HandleIncomingBookmarkDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("bookmark-delete-ack", itemId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "bookmark-delete-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("bookmark-delete-ack", msg.itemId, peerId);
            }
            else if (msg.type == "folder-delete" && msg.content != null)
            {
                await HandleIncomingFolderDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("folder-delete-ack", itemId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "folder-delete-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("folder-delete-ack", msg.itemId, peerId);
            }
            else if (msg.type == "app-delete" && msg.content != null)
            {
                await HandleIncomingSidebarAppDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("app-delete-ack", itemId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "app-delete-ack" && msg.itemId != null)
            {
                HandleGenericItemAck("app-delete-ack", msg.itemId, peerId);
            }
            else if (msg.type == "sync-manifest-offer" && msg.content != null)
            {
                await HandleManifestOfferAsync(peerId, msg.content);
            }
            else if (msg.type == "sync-manifest-response" && msg.content != null)
            {
                await HandleManifestResponseAsync(peerId, msg.content);
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to parse DataChannel message: {ex.Message}");
        }
    }

    private async Task HandleManifestOfferAsync(string peerId, string offerJson)
    {
        var offer = System.Text.Json.JsonSerializer.Deserialize<SyncManifestOffer>(offerJson);
        if (offer == null)
            return;

        var localConvos = await _conversationStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);
        var localNotes = await _noteStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);
        var remoteBookmarks = offer.Bookmarks ?? [];
        var remoteFolders = offer.BookmarkFolders ?? [];
        var remoteApps = offer.SidebarApps ?? [];

        var neededConvos = new List<string>();
        var senderShouldDeleteConvos = new List<DeleteSyncPayload>();
        var upToDateConvos = 0;
        var appliedConvoDeletes = 0;
        foreach (var remote in offer.Convos)
        {
            var local = localConvos.FirstOrDefault(c => string.Equals(c.Id, remote.Id, StringComparison.Ordinal));

            if (remote.IsDeleted)
            {
                if (await _conversationStore.TryApplyRemoteDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                    appliedConvoDeletes++;
                upToDateConvos++;
                continue;
            }

            if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
            {
                senderShouldDeleteConvos.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                upToDateConvos++;
                continue;
            }

            if (ManifestEntryNeedsSync(remote, local))
                neededConvos.Add(remote.Id);
            else
                upToDateConvos++;
        }

        var neededNotes = new List<string>();
        var senderShouldDeleteNotes = new List<DeleteSyncPayload>();
        var upToDateNotes = 0;
        var appliedNoteDeletes = 0;
        foreach (var remote in offer.Notes)
        {
            var local = localNotes.FirstOrDefault(n => string.Equals(n.Id, remote.Id, StringComparison.Ordinal));

            if (remote.IsDeleted)
            {
                if (await _noteStore.TryApplyRemoteDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                    appliedNoteDeletes++;
                upToDateNotes++;
                continue;
            }

            if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
            {
                senderShouldDeleteNotes.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                upToDateNotes++;
                continue;
            }

            if (ManifestEntryNeedsSync(remote, local))
                neededNotes.Add(remote.Id);
            else
                upToDateNotes++;
        }

        var neededBookmarks = new List<string>();
        var neededFolders = new List<string>();
        var neededApps = new List<string>();
        var senderShouldDeleteBookmarks = new List<DeleteSyncPayload>();
        var senderShouldDeleteFolders = new List<DeleteSyncPayload>();
        var senderShouldDeleteApps = new List<DeleteSyncPayload>();
        var upToDateBookmarks = 0;
        var upToDateFolders = 0;
        var upToDateApps = 0;
        var appliedBookmarkDeletes = 0;
        var appliedFolderDeletes = 0;
        var appliedAppDeletes = 0;

        if (_browserStore != null)
        {
            var localFolders = await _browserStore.LoadFolderManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteFolders)
            {
                var local = localFolders.FirstOrDefault(f => string.Equals(f.Id, remote.Id, StringComparison.Ordinal));

                if (remote.IsDeleted)
                {
                    if (await _browserStore.TryApplyRemoteFolderDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedFolderDeletes++;
                    upToDateFolders++;
                    continue;
                }

                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteFolders.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateFolders++;
                    continue;
                }

                if (ManifestEntryNeedsSync(remote, local))
                    neededFolders.Add(remote.Id);
                else
                    upToDateFolders++;
            }

            var localBookmarks = await _browserStore.LoadBookmarkManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteBookmarks)
            {
                var local = localBookmarks.FirstOrDefault(b => string.Equals(b.Id, remote.Id, StringComparison.Ordinal));

                if (remote.IsDeleted)
                {
                    if (await _browserStore.TryApplyRemoteBookmarkDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedBookmarkDeletes++;
                    upToDateBookmarks++;
                    continue;
                }

                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteBookmarks.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateBookmarks++;
                    continue;
                }

                if (ManifestEntryNeedsSync(remote, local))
                    neededBookmarks.Add(remote.Id);
                else
                    upToDateBookmarks++;
            }
        }

        if (_sidebarStore != null)
        {
            var localApps = await _sidebarStore.LoadSidebarAppManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteApps)
            {
                var local = localApps.FirstOrDefault(a => string.Equals(a.Id, remote.Id, StringComparison.Ordinal));

                if (remote.IsDeleted)
                {
                    if (await _sidebarStore.TryApplyRemoteSidebarAppDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedAppDeletes++;
                    upToDateApps++;
                    continue;
                }

                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteApps.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateApps++;
                    continue;
                }

                if (ManifestEntryNeedsSync(remote, local))
                    neededApps.Add(remote.Id);
                else
                    upToDateApps++;
            }
        }

        if (appliedConvoDeletes > 0)
            OnConversationsChanged?.Invoke();
        if (appliedNoteDeletes > 0)
            OnNotesChanged?.Invoke();
        if (appliedBookmarkDeletes + appliedFolderDeletes > 0)
            OnBookmarksChanged?.Invoke();
        if (appliedAppDeletes > 0)
            OnInstalledAppsChanged?.Invoke();

        var response = new SyncManifestResponse(
            neededConvos,
            neededNotes,
            upToDateConvos,
            upToDateNotes,
            senderShouldDeleteConvos,
            senderShouldDeleteNotes,
            neededBookmarks,
            neededFolders,
            neededApps,
            upToDateBookmarks,
            upToDateFolders,
            upToDateApps,
            senderShouldDeleteBookmarks,
            senderShouldDeleteFolders,
            senderShouldDeleteApps);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(response);
        await _webrtc.SendDataAsync(
            peerId,
            SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-response", content: responseJson)));

        SyncDebugLog.Info($"Manifest offer from {peerId}: " +
            $"{upToDateConvos}/{offer.Convos.Count} convos, {upToDateNotes}/{offer.Notes.Count} notes, " +
            $"{upToDateFolders}/{remoteFolders.Count} folders, {upToDateBookmarks}/{remoteBookmarks.Count} bookmarks, " +
            $"{upToDateApps}/{remoteApps.Count} apps up to date" +
            (appliedConvoDeletes + appliedNoteDeletes + appliedBookmarkDeletes + appliedFolderDeletes + appliedAppDeletes > 0
                ? $" (applied {appliedConvoDeletes} convo, {appliedNoteDeletes} note, {appliedFolderDeletes} folder, {appliedBookmarkDeletes} bookmark, {appliedAppDeletes} app delete(s))"
                : ""));
    }

    private async Task HandleManifestResponseAsync(string peerId, string responseJson)
    {
        var response = System.Text.Json.JsonSerializer.Deserialize<SyncManifestResponse>(responseJson);
        if (response == null)
        {
            await FailActiveSyncAsync(peerId, "invalid manifest response");
            return;
        }

        var appliedConvoDeletes = 0;
        foreach (var del in response.SenderShouldDeleteConvos ?? [])
        {
            if (await _conversationStore.TryApplyRemoteDeleteAsync(del.Id, del.DeletedAtTicks))
                appliedConvoDeletes++;
        }

        var appliedNoteDeletes = 0;
        foreach (var del in response.SenderShouldDeleteNotes ?? [])
        {
            if (await _noteStore.TryApplyRemoteDeleteAsync(del.Id, del.DeletedAtTicks))
                appliedNoteDeletes++;
        }

        var appliedBookmarkDeletes = 0;
        var appliedFolderDeletes = 0;
        if (_browserStore != null)
        {
            foreach (var del in response.SenderShouldDeleteBookmarkFolders ?? [])
            {
                if (await _browserStore.TryApplyRemoteFolderDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedFolderDeletes++;
            }

            foreach (var del in response.SenderShouldDeleteBookmarks ?? [])
            {
                if (await _browserStore.TryApplyRemoteBookmarkDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedBookmarkDeletes++;
            }
        }

        var appliedAppDeletes = 0;
        if (_sidebarStore != null)
        {
            foreach (var del in response.SenderShouldDeleteSidebarApps ?? [])
            {
                if (await _sidebarStore.TryApplyRemoteSidebarAppDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedAppDeletes++;
            }
        }

        if (appliedConvoDeletes > 0)
            OnConversationsChanged?.Invoke();
        if (appliedNoteDeletes > 0)
            OnNotesChanged?.Invoke();
        if (appliedBookmarkDeletes + appliedFolderDeletes > 0)
            OnBookmarksChanged?.Invoke();
        if (appliedAppDeletes > 0)
            OnInstalledAppsChanged?.Invoke();

        var queued = await QueueNeededItemsFromManifestAsync(peerId, response);
        var noNeeded =
            response.NeededConvos.Count == 0
            && response.NeededNotes.Count == 0
            && (response.NeededBookmarks?.Count ?? 0) == 0
            && (response.NeededBookmarkFolders?.Count ?? 0) == 0
            && (response.NeededSidebarApps?.Count ?? 0) == 0;
        if (noNeeded)
            await RecordManifestVerifiedAsync(peerId);
        if (queued == 0)
            SyncDebugLog.Info($"Delta sync complete for {peerId} - peer is up to date");
        await AdvanceSyncQueueAsync(peerId);
    }

    private async Task HandleSyncAckAsync(string convoId, string peerId)
    {
        SyncDebugLog.Info($"Received sync-ack for convo {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnSyncAckReceived?.Invoke(convoId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleSyncAck(string convoId, string peerId) =>
        _ = HandleSyncAckAsync(convoId, peerId);

    private async Task HandleNoteSyncAckAsync(string noteId, string peerId)
    {
        SyncDebugLog.Info($"Received note-sync-ack for note {noteId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnNoteSyncAckReceived?.Invoke(noteId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleNoteSyncAck(string noteId, string peerId) =>
        _ = HandleNoteSyncAckAsync(noteId, peerId);

    private async Task HandleGenericItemAckAsync(string type, string itemId, string peerId)
    {
        SyncDebugLog.Info($"Received {type} for {itemId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleGenericItemAck(string type, string itemId, string peerId) =>
        _ = HandleGenericItemAckAsync(type, itemId, peerId);

    private async Task HandleConvoDeleteAckAsync(string convoId, string peerId)
    {
        SyncDebugLog.Info($"Received convo-delete-ack for {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleConvoDeleteAck(string convoId, string peerId) =>
        _ = HandleConvoDeleteAckAsync(convoId, peerId);

    private async Task HandleNoteDeleteAckAsync(string noteId, string peerId)
    {
        SyncDebugLog.Info($"Received note-delete-ack for {noteId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleNoteDeleteAck(string noteId, string peerId) =>
        _ = HandleNoteDeleteAckAsync(noteId, peerId);

    private async Task HandleIncomingNoteSyncPayload(string json, string fromDeviceId)
    {
        try
        {
            var payload = NoteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.NoteId))
                return;

            var entries = ChatMessageHelper.NormalizeAll(payload.Entries);
            if (!await _noteStore.ShouldAcceptIncomingContentAsync(payload.NoteId, entries))
            {
                SyncDebugLog.Info($"Ignoring stale note sync for {payload.NoteId} (local delete is newer)");
                return;
            }

            var localTitle = await _noteStore.GetMetaTitleAsync(payload.NoteId);
            var title = ChatMessageHelper.ResolveIncomingNoteTitle(payload.Title, localTitle);

            await _noteStore.SaveNoteAsync(payload.NoteId, entries);
            await _noteStore.UpdateIndexAfterSaveAsync(payload.NoteId, title, entries);

            OnNoteSyncPayloadReceived?.Invoke(payload.NoteId, json, fromDeviceId);
            OnNotesChanged?.Invoke();

            SyncDebugLog.Info($"Auto-saved incoming note sync for {payload.NoteId} from {fromDeviceId} ({payload.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming note sync payload: {ex.Message}");
        }
    }

    private async Task HandleIncomingBookmarkSyncPayload(string json, string fromDeviceId)
    {
        if (_browserStore == null)
        {
            SyncDebugLog.Warn($"Incoming bookmark sync from {fromDeviceId} ignored — no IBrowserStore on this device.");
            return;
        }

        try
        {
            await _browserStore.LoadAsync();
            var payload = BookmarkSyncPayload.Deserialize(json);
            if (payload?.Bookmark == null || string.IsNullOrEmpty(payload.Bookmark.Id))
                return;

            if (!await _browserStore.ShouldAcceptIncomingBookmarkAsync(payload.Bookmark))
            {
                SyncDebugLog.Info($"Ignoring stale bookmark sync for {payload.Bookmark.Id}");
                return;
            }

            await _browserStore.ApplyBookmarkPayloadAsync(payload.Bookmark);
            OnBookmarksChanged?.Invoke();
            SyncDebugLog.Info($"Applied incoming bookmark sync for {payload.Bookmark.Id} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming bookmark sync: {ex.Message}");
        }
    }

    private async Task HandleIncomingFolderSyncPayload(string json, string fromDeviceId)
    {
        if (_browserStore == null)
        {
            SyncDebugLog.Warn($"Incoming folder sync from {fromDeviceId} ignored — no IBrowserStore.");
            return;
        }

        try
        {
            await _browserStore.LoadAsync();
            var payload = BookmarkFolderSyncPayload.Deserialize(json);
            if (payload?.Folder == null || string.IsNullOrEmpty(payload.Folder.Id))
                return;

            if (!await _browserStore.ShouldAcceptIncomingFolderAsync(payload.Folder))
            {
                SyncDebugLog.Info($"Ignoring stale folder sync for {payload.Folder.Id}");
                return;
            }

            await _browserStore.ApplyFolderPayloadAsync(payload.Folder);
            OnBookmarksChanged?.Invoke();
            SyncDebugLog.Info($"Applied incoming folder sync for {payload.Folder.Id} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming folder sync: {ex.Message}");
        }
    }

    private async Task HandleIncomingSidebarAppSyncPayload(string json, string fromDeviceId)
    {
        if (_sidebarStore == null)
        {
            SyncDebugLog.Warn($"Incoming sidebar app sync from {fromDeviceId} ignored — no IBrowserSidebarStore.");
            return;
        }

        try
        {
            await _sidebarStore.LoadAsync();
            var payload = SidebarAppSyncPayload.Deserialize(json);
            if (payload?.App == null || string.IsNullOrEmpty(payload.App.Id))
                return;

            if (!await _sidebarStore.ShouldAcceptIncomingSidebarAppAsync(payload.App))
            {
                SyncDebugLog.Info($"Ignoring stale sidebar app sync for {payload.App.Id}");
                return;
            }

            await _sidebarStore.ApplySidebarAppPayloadAsync(payload.App);
            OnInstalledAppsChanged?.Invoke();
            SyncDebugLog.Info($"Applied incoming sidebar app sync for {payload.App.Id} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming sidebar app sync: {ex.Message}");
        }
    }

    private async Task HandleIncomingConvoDeleteAsync(string json, string fromDeviceId)
    {
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _conversationStore.TryApplyRemoteDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnConversationsChanged?.Invoke();
                SyncDebugLog.Info($"Applied remote convo delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply convo delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingNoteDeleteAsync(string json, string fromDeviceId)
    {
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _noteStore.TryApplyRemoteDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnNotesChanged?.Invoke();
                SyncDebugLog.Info($"Applied remote note delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply note delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingBookmarkDeleteAsync(string json, string fromDeviceId)
    {
        if (_browserStore == null)
            return;

        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _browserStore.TryApplyRemoteBookmarkDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnBookmarksChanged?.Invoke();
                SyncDebugLog.Info($"Applied remote bookmark delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply bookmark delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingFolderDeleteAsync(string json, string fromDeviceId)
    {
        if (_browserStore == null)
            return;

        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _browserStore.TryApplyRemoteFolderDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnBookmarksChanged?.Invoke();
                SyncDebugLog.Info($"Applied remote folder delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply folder delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingSidebarAppDeleteAsync(string json, string fromDeviceId)
    {
        if (_sidebarStore == null)
            return;

        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _sidebarStore.TryApplyRemoteSidebarAppDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                OnInstalledAppsChanged?.Invoke();
                SyncDebugLog.Info($"Applied remote sidebar app delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply sidebar app delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingSyncPayload(string convoId, string json, string fromDeviceId)
    {
        try
        {
            List<ChatMessage> msgs;
            string? incomingTitle = null;
            bool incomingTitleIsCustom = false;

            var payload = ConvoSyncPayload.TryDeserialize(json);
            if (payload == null)
            {
                SyncDebugLog.Info($"Ignoring invalid convo sync payload for {convoId}");
                return;
            }

            convoId = payload.ConvoId;
            msgs = ChatMessageHelper.NormalizeAll(payload.Messages);
            incomingTitle = payload.Title;
            incomingTitleIsCustom = payload.TitleIsCustom == true;

            if (!await _conversationStore.ShouldAcceptIncomingContentAsync(convoId, msgs))
            {
                SyncDebugLog.Info($"Ignoring stale convo sync for {convoId} (local delete is newer)");
                return;
            }

            await _conversationStore.SaveConversationAsync(convoId, msgs);

            if (incomingTitleIsCustom)
            {
                var localTitleInfo = await _conversationStore.GetMetaTitleInfoAsync(convoId);
                var resolvedTitle = ChatMessageHelper.ResolveIncomingConvoTitle(
                    incomingTitle,
                    localTitleInfo.Title,
                    incomingTitleIsCustom: true,
                    localTitleInfo.TitleIsCustom);
                await _conversationStore.SetConversationTitleAsync(convoId, resolvedTitle);
            }
            else
            {
                var currentIndex = await _conversationStore.LoadIndexAsync();
                await _conversationStore.UpdateIndexAfterSaveAsync(convoId, msgs, currentIndex);
            }

            OnSyncPayloadReceived?.Invoke(convoId, json, fromDeviceId);
            OnConversationsChanged?.Invoke();

            SyncDebugLog.Info($"Auto-saved incoming sync for convo {convoId} from {fromDeviceId} " +
                $"({msgs.Count} messages, title=\"{incomingTitle ?? ""}\", custom={incomingTitleIsCustom})");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming sync payload: {ex.Message}");
        }
    }

    public void OnDataChannelClose(string peerId)
    {
        SyncDebugLog.Info($"DataChannel closed for {peerId}");

        // Queue progression is owned by CompleteActiveSyncAsync / FailActiveSyncAsync.
        // Only treat a close as failure when a sync transfer is still marked active.
        if (_activeSyncByPeer.ContainsKey(peerId))
            _ = FailActiveSyncAsync(peerId, "data channel closed before acknowledgement");
    }

    
    private record DataChannelMessage(
        string type,
        string? convoId = null,
        string? content = null,
        string? noteId = null,
        int? chunkIndex = null,
        int? chunkCount = null,
        string? chunkData = null,
        string? itemId = null);

    public async ValueTask DisposeAsync()
    {
        foreach (var peerId in _activeSyncByPeer.Keys.ToList())
        {
            try { await CloseWebRtcPeerAsync(peerId); } catch { }
        }

        foreach (var cts in _autoSyncDebounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _autoSyncDebounce.Clear();

        foreach (var cts in _peerOnlineAutoSyncDebounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _peerOnlineAutoSyncDebounce.Clear();

        foreach (var cts in _syncTimeoutByPeer.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _syncTimeoutByPeer.Clear();
    }
}
