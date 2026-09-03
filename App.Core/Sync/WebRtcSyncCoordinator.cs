using System.Text.Json;
using App.Core.Browser;
using App.Core.Storage;
using App.Core.Sync;

namespace App.Core.Sync;

/// <summary>
/// Shared WebRTC data-channel sync protocol (queue, manifest, ack state, signaling handlers).
/// Platform sync services own SignalR hub wiring and delegate data transfer here.
/// </summary>
public sealed partial class WebRtcSyncCoordinator : IWebRtcTransportCallbacks, IAsyncDisposable
{
    private readonly IWebRtcTransport _webrtc;
    private readonly IConversationStore _conversationStore;
    private readonly INoteStore _noteStore;
    private readonly IGalleryStore? _galleryStore;
    private readonly ICalendarStore? _calendarStore;
    private readonly IStorageQuotaService? _storageQuota;
    private readonly IBrowserStore? _browserStore;
    private readonly IBrowserSidebarStore? _sidebarStore;
    private readonly ISettingsSyncStore? _settingsStore;
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
        IBrowserSidebarStore? sidebarStore = null,
        ISettingsSyncStore? settingsStore = null,
        IGalleryStore? galleryStore = null,
        IStorageQuotaService? storageQuota = null,
        ICalendarStore? calendarStore = null)
    {
        _webrtc = webrtc;
        _conversationStore = conversationStore;
        _noteStore = noteStore;
        _galleryStore = galleryStore;
        _calendarStore = calendarStore;
        _storageQuota = storageQuota;
        _prefs = prefs;
        _sendSignalingAsync = sendSignalingAsync;
        _isHubConnected = isHubConnected;
        _transportCallbacks = transportCallbacks ?? this;
        _browserStore = browserStore;
        _sidebarStore = sidebarStore;
        _settingsStore = settingsStore;
    }

    public bool AutoSyncChatHistory { get; set; }
    public bool AutoSyncNotes { get; set; }
    public bool AutoSyncGallery { get; set; }
    public bool AutoSyncCalendar { get; set; }
    public bool AutoSyncBookmarks { get; set; }
    public bool AutoSyncInstalledApps { get; set; }
    public bool AutoSyncLocalAi { get; set; }
    public bool AutoSyncLemonade { get; set; }
    public bool AutoSyncCloudProviders { get; set; }
    public bool AutoSyncModelProfiles { get; set; }
    public bool AutoSyncHomeAssistant { get; set; }
    public bool AutoSyncTools { get; set; }
    public bool AutoSyncSystemPrompt { get; set; }
    public bool AutoSyncProfile { get; set; }
    public bool AutoSyncMemories { get; set; }
    public bool AutoSyncAppearance { get; set; }
    public bool AutoSyncSkills { get; set; }
    public IReadOnlyCollection<string> SyncTargetDeviceIds { get; set; } = Array.Empty<string>();
    public Func<string, bool>? IsSelf { get; set; }
    /// <summary>This device's sync id — used for deterministic WebRTC glare (perfect negotiation).</summary>
    public string? LocalDeviceId { get; set; }
    public Func<bool>? IsAuthenticated { get; set; }
    public Func<Task>? EnsureConnectedAsync { get; set; }
    public Func<IReadOnlyList<SyncDeviceInfo>>? GetDevices { get; set; }

    public event Action? OnConversationsChanged;
    public event Action? OnNotesChanged;
    public event Action? OnGalleryChanged;
    public event Action? OnCalendarsChanged;
    public event Action? OnBookmarksChanged;
    public event Action? OnInstalledAppsChanged;
    public event Action? OnSettingsChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;
    public event Action<string, string, string>? OnAlbumSyncPayloadReceived;
    public event Action<string, string>? OnAlbumSyncAckReceived;

    public Task HandleReceiveSignalingAsync(string fromDeviceId, string type, string payload)
    {
        if (type is "webrtc-offer" or "webrtc-offer-ai")
            SyncDebugLog.WebRtc($"Received signaling '{type}' from {fromDeviceId}");

        return type switch
        {
            "webrtc-offer" => ExclusiveAsync(() => HandleWebRtcOffer(fromDeviceId, payload)),
            "webrtc-answer" => HandleWebRtcAnswer(fromDeviceId, payload),
            "webrtc-ice" => ExclusiveAsync(() => HandleWebRtcIce(fromDeviceId, payload)),
            "webrtc-need-offer" => ExclusiveAsync(() => HandleNeedOfferAsync(fromDeviceId)),
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
    private readonly HashSet<string> _pendingNotePushIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _pendingNotePushTitles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ChunkAssembly> _chunkAssemblies = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Remote offers deferred while we have an outbound transfer in flight (avoids killing the DataChannel mid-image).</summary>
    private readonly Dictionary<string, string> _deferredRemoteOffers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// After we were the passive answerer, claim one outbound turn so the polite peer can push
    /// its own albums/notes instead of forever yielding to the other device's continuous offers.
    /// </summary>
    private readonly HashSet<string> _owedOutboundTurn = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Device ids we have already sent a WebRTC offer to (avoid dual offers).</summary>
    private readonly HashSet<string> _offerInFlight = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Active outbound item already written to the DataChannel (waiting for ack, not handshake).</summary>
    private readonly HashSet<string> _outboundSentByPeer = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _galleryChangedDebounceCts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _holdingGate = new();
    /// <summary>ICE/SDP until DataChannel open. Ack timer starts only after a send.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(90);
    /// <summary>Small items (settings, notes, deletes) waiting for ack after send.</summary>
    private static readonly TimeSpan SmallItemAckTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AutoSyncDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoteAutoSyncDebounce = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PeerOnlineAutoSyncDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManifestRecheckCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GalleryChangedDebounce = TimeSpan.FromMilliseconds(400);
    private const int MaxSyncRetries = 2;
    private const int LargePayloadThresholdBytes = 256 * 1024;

    private async Task ExclusiveAsync(Func<Task> work)
    {
        if (_holdingGate.Value)
        {
            await work();
            return;
        }

        await _gate.WaitAsync();
        _holdingGate.Value = true;
        try
        {
            await work();
        }
        finally
        {
            _holdingGate.Value = false;
            _gate.Release();
        }
    }

    private static void Raise(Action? handler)
    {
        if (handler is null) return;
        try { handler(); }
        catch (Exception ex) { SyncDebugLog.Info($"Sync change notification failed: {ex.Message}"); }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handler, T1 a, T2 b)
    {
        if (handler is null) return;
        try { handler(a, b); }
        catch (Exception ex) { SyncDebugLog.Info($"Sync change notification failed: {ex.Message}"); }
    }

    private static void Raise<T1, T2, T3>(Action<T1, T2, T3>? handler, T1 a, T2 b, T3 c)
    {
        if (handler is null) return;
        try { handler(a, b, c); }
        catch (Exception ex) { SyncDebugLog.Info($"Sync change notification failed: {ex.Message}"); }
    }

    /// <summary>Fire-and-forget without flowing the exclusive-lock AsyncLocal into the new task.</summary>
    private void StartDetached(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try { await work(); }
                catch (Exception ex) { SyncDebugLog.Info($"Detached sync task failed: {ex.Message}"); }
            });
        }
    }

    /// <summary>
    /// Large gallery albums can be 100MB+ over the DataChannel; 45s is far too short.
    /// Scale with payload size: ~90s base + 3s/MB, capped at 30 minutes.
    /// </summary>
    private static TimeSpan TimeoutForPayloadBytes(int payloadBytes)
    {
        var mb = Math.Max(0, payloadBytes) / (1024.0 * 1024.0);
        var seconds = Math.Clamp(90 + (mb * 3.0), 90, 1800);
        return TimeSpan.FromSeconds(seconds);
    }

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
        public bool IncludeAlbumsInManifest { get; init; }
        public bool IncludeCalendarsInManifest { get; init; }
        public bool IncludeBookmarksInManifest { get; init; }
        public bool IncludeSidebarAppsInManifest { get; init; }
        public int RetryCount { get; set; }
    }

    private record SyncManifestOffer(
        List<SyncManifestEntry> Convos,
        List<SyncManifestEntry> Notes,
        List<SyncManifestEntry>? Bookmarks = null,
        List<SyncManifestEntry>? BookmarkFolders = null,
        List<SyncManifestEntry>? SidebarApps = null,
        List<SyncManifestEntry>? Albums = null,
        List<SyncManifestEntry>? AlbumImages = null,
        List<SyncManifestEntry>? Calendars = null,
        List<SyncManifestEntry>? CalendarEvents = null);

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
        List<DeleteSyncPayload>? SenderShouldDeleteSidebarApps = null,
        List<string>? NeededAlbums = null,
        int UpToDateAlbums = 0,
        List<DeleteSyncPayload>? SenderShouldDeleteAlbums = null,
        List<string>? NeededAlbumImages = null,
        int UpToDateAlbumImages = 0,
        List<DeleteSyncPayload>? SenderShouldDeleteAlbumImages = null,
        List<string>? NeededCalendars = null,
        int UpToDateCalendars = 0,
        List<DeleteSyncPayload>? SenderShouldDeleteCalendars = null,
        List<string>? NeededCalendarEvents = null,
        int UpToDateCalendarEvents = 0,
        List<DeleteSyncPayload>? SenderShouldDeleteCalendarEvents = null);

    private const string SyncAckStateKey = "app-sync-ack-state";
    private const string SyncManifestVerifiedKey = "app-sync-manifest-verified";


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
        var index = await _conversationStore.LoadIndexAsync();
        var meta = index.FirstOrDefault(c => c.Id == convoId);
        var isProtected = meta?.IsPasswordProtected == true;
        var proticks = meta?.ProtectionChangedTicks ?? 0;
        var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom, isProtected, proticks);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Conversation,
            ItemId = convoId,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForConversation(convoId, title, messages, isProtected, proticks)
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

        var index = await _noteStore.LoadIndexAsync();
        var meta = index.FirstOrDefault(n => string.Equals(n.Id, noteId, StringComparison.OrdinalIgnoreCase));
        var candidate = ChatMessageHelper.IsPlaceholderNoteTitle(title, noteId) ? meta?.Title : title;
        var resolvedTitle = ChatMessageHelper.ResolveOutgoingNoteTitle(candidate ?? meta?.Title, noteId);
        var isProtected = meta?.IsPasswordProtected == true;
        var proticks = meta?.ProtectionChangedTicks ?? 0;
        var titleTicks = meta?.TitleChangedTicks ?? 0;
        var dataJson = NoteSyncPayload.Serialize(noteId, resolvedTitle, entries, isProtected, proticks, titleTicks);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Note,
            ItemId = noteId,
            NoteTitle = resolvedTitle,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForNote(noteId, resolvedTitle, entries, isProtected, proticks, titleTicks)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueAlbumMetaSyncAsync(string targetDeviceId, string albumId, string title)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _galleryStore == null)
            return;
        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue album meta {albumId}: hub not connected.");
            return;
        }

        var index = await _galleryStore.LoadIndexAsync();
        var meta = index.FirstOrDefault(n => string.Equals(n.Id, albumId, StringComparison.OrdinalIgnoreCase));
        title = ChatMessageHelper.ResolveOutgoingNoteTitle(
            ChatMessageHelper.IsPlaceholderNoteTitle(title, albumId) ? meta?.Title : title,
            albumId);
        var isProtected = meta?.IsPasswordProtected == true;
        var proticks = meta?.ProtectionChangedTicks ?? 0;
        var images = await _galleryStore.LoadAlbumAsync(albumId);
        var refs = images
            .Select((img, i) => new AlbumImageRef(
                img.Id,
                SyncFingerprint.ForAlbumImage(albumId, GalleryImageSyncPayload.ForWire(img)),
                i,
                img.DeletedAt?.Ticks))
            .ToList();
        var dataJson = GalleryAlbumMetaPayload.Serialize(albumId, title, refs, isProtected, proticks);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Album,
            ItemId = albumId,
            NoteTitle = title,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForAlbumMeta(albumId, title, refs, isProtected, proticks)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _galleryStore == null)
            return;
        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue album image {albumId}/{imageId}: hub not connected.");
            return;
        }

        var image = await _galleryStore.LoadImageAsync(albumId, imageId);
        if (image == null)
            return;

        var composite = GalleryImageSyncPayload.CompositeId(albumId, imageId);
        if (image.DeletedAt.HasValue)
        {
            await EnqueueAlbumImageDeleteAsync(targetDeviceId, albumId, imageId, image.DeletedAt.Value);
            return;
        }

        var dataJson = GalleryImageSyncPayload.Serialize(albumId, image);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.AlbumImage,
            ItemId = composite,
            NoteTitle = image.Name,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForAlbumImage(albumId, image)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task EnqueueAlbumImageDeleteAsync(string targetDeviceId, string albumId, string imageId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected() || _galleryStore == null)
            return;

        var composite = GalleryImageSyncPayload.CompositeId(albumId, imageId);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.AlbumImage,
            IsDelete = true,
            ItemId = composite,
            DataJson = DeleteSyncPayload.Serialize(composite, deletedAtUtc.Ticks),
            ContentFingerprint = DeleteSyncPayload.AckValue(deletedAtUtc.Ticks),
            DeletedAtTicks = deletedAtUtc.Ticks
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public Task StartWebRtcAlbumSyncAsync(string targetDeviceId, string albumId, string title) =>
        EnqueueAlbumMetaSyncAsync(targetDeviceId, albumId, title);

    public Task StartWebRtcAlbumImageSyncAsync(string targetDeviceId, string albumId, string imageId) =>
        EnqueueAlbumImageSyncAsync(targetDeviceId, albumId, imageId);

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
        bool includeSidebarApps = false,
        bool includeAlbums = false,
        bool includeCalendars = false)
    {
        if (_browserStore == null)
            includeBookmarks = false;
        if (_sidebarStore == null)
            includeSidebarApps = false;
        if (_galleryStore == null)
            includeAlbums = false;
        if (_calendarStore == null)
            includeCalendars = false;

        var targets = targetDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0 || !_isHubConnected())
            return 0;

        foreach (var targetId in targets)
            await EnqueueManifestExchangeAsync(targetId, includeConvos, includeNotes, includeBookmarks, includeSidebarApps, includeAlbums, includeCalendars);

        return targets.Count;
    }

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: true, includeNotes: false);

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: false, includeNotes: true);

    public Task<int> SyncAllAlbumsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: false, includeNotes: false, includeAlbums: true);

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
        bool includeSidebarApps = false,
        bool includeAlbums = false,
        bool includeCalendars = false)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        if (_browserStore == null)
            includeBookmarks = false;
        if (_sidebarStore == null)
            includeSidebarApps = false;
        if (_galleryStore == null)
            includeAlbums = false;
        if (_calendarStore == null)
            includeCalendars = false;

        if (!includeConvos && !includeNotes && !includeBookmarks && !includeSidebarApps && !includeAlbums && !includeCalendars)
            return;

        var manifest = await BuildLocalManifestAsync(includeConvos, includeNotes, includeBookmarks, includeSidebarApps, includeAlbums, includeCalendars);
        var item = new SyncQueueItem
        {
            IsManifestExchange = true,
            Kind = SyncItemKind.Conversation,
            ItemId = "__manifest__",
            DataJson = System.Text.Json.JsonSerializer.Serialize(manifest),
            IncludeConvosInManifest = includeConvos,
            IncludeNotesInManifest = includeNotes,
            IncludeAlbumsInManifest = includeAlbums,
            IncludeCalendarsInManifest = includeCalendars,
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
        bool includeSidebarApps,
        bool includeAlbums = false,
        bool includeCalendars = false)
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
        List<SyncManifestEntry>? albums = null;

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

        List<SyncManifestEntry>? albumImages = null;
        if (includeAlbums && _galleryStore != null)
        {
            albums = await _galleryStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);
            albumImages = await _galleryStore.LoadImageManifestEntriesAsync();
            SyncDebugLog.Info($"BuildLocalManifest albums={albums.Count} albumImages={albumImages.Count}");
        }

        List<SyncManifestEntry>? calendars = null;
        List<SyncManifestEntry>? calendarEvents = null;
        if (includeCalendars && _calendarStore != null)
        {
            // Workflows calendar + WorkflowId events are device-local (schedules must not fan out).
            var workflowCalIds = await GetWorkflowCalendarIdsAsync();
            calendars = (await _calendarStore.LoadCalendarManifestEntriesAsync(backfillMissingFingerprints: true))
                .Where(e => !IsWorkflowCalendarId(e.Id, workflowCalIds))
                .ToList();
            var rawEvents = await _calendarStore.LoadEventManifestEntriesAsync(backfillMissingFingerprints: true);
            calendarEvents = new List<SyncManifestEntry>(rawEvents.Count);
            foreach (var entry in rawEvents)
            {
                if (await IsWorkflowScopedEventAsync(entry.Id, workflowCalIds))
                    continue;
                calendarEvents.Add(entry);
            }
            SyncDebugLog.Info($"BuildLocalManifest calendars={calendars.Count} calendarEvents={calendarEvents.Count} (workflow excluded)");
        }

        return new SyncManifestOffer(convos, notes, bookmarks, folders, apps, albums, albumImages, calendars, calendarEvents);
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
            // Content hash is the only dirty bit. LastUpdatedTicks often differs across
            // devices that already have the same bytes (receive clock), which used to
            // re-transfer 10MB+ gallery images forever.
            return !string.Equals(remote.ContentFingerprint, local.ContentFingerprint, StringComparison.Ordinal);
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
        SyncItemKind.Album => $"g:{itemId}",
        SyncItemKind.AlbumImage => $"gi:{itemId}",
        SyncItemKind.Calendar => $"cal:{itemId}",
        SyncItemKind.CalendarEvent => $"cale:{itemId}",
        SyncItemKind.Bookmark => $"b:{itemId}",
        SyncItemKind.BookmarkFolder => $"f:{itemId}",
        SyncItemKind.SidebarApp => $"a:{itemId}",
        SyncItemKind.Settings => $"s:{itemId}",
        _ => $"c:{itemId}"
    };

    private static string KindLabel(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "convo",
        SyncItemKind.Note => "note",
        SyncItemKind.Album => "album",
        SyncItemKind.AlbumImage => "album-image",
        SyncItemKind.Calendar => "calendar",
        SyncItemKind.CalendarEvent => "calendar-event",
        SyncItemKind.Bookmark => "bookmark",
        SyncItemKind.BookmarkFolder => "folder",
        SyncItemKind.SidebarApp => "app",
        SyncItemKind.Settings => "settings",
        _ => "item"
    };

    private static string ChannelLabelFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "app-sync",
        SyncItemKind.Note => "app-note-sync",
        SyncItemKind.Album => "app-album-sync",
        SyncItemKind.AlbumImage => "app-album-image-sync",
        SyncItemKind.Calendar => "app-calendar-sync",
        SyncItemKind.CalendarEvent => "app-calendar-event-sync",
        SyncItemKind.Bookmark => "app-bookmark-sync",
        SyncItemKind.BookmarkFolder => "app-folder-sync",
        SyncItemKind.SidebarApp => "wizionic-app-sync",
        SyncItemKind.Settings => "app-settings-sync",
        _ => "app-sync"
    };

    private static string DeleteTypeFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "convo-delete",
        SyncItemKind.Note => "note-delete",
        SyncItemKind.Album => "album-delete",
        SyncItemKind.AlbumImage => "album-image-delete",
        SyncItemKind.Calendar => "calendar-delete",
        SyncItemKind.CalendarEvent => "calendar-event-delete",
        SyncItemKind.Bookmark => "bookmark-delete",
        SyncItemKind.BookmarkFolder => "folder-delete",
        SyncItemKind.SidebarApp => "app-delete",
        _ => "convo-delete"
    };

    private static string DataTypeFor(SyncItemKind kind) => kind switch
    {
        SyncItemKind.Conversation => "sync-data",
        SyncItemKind.Note => "note-sync-data",
        SyncItemKind.Album => "album-sync-data",
        SyncItemKind.AlbumImage => "album-image-sync-data",
        SyncItemKind.Calendar => "calendar-sync-data",
        SyncItemKind.CalendarEvent => "calendar-event-sync-data",
        SyncItemKind.Bookmark => "bookmark-sync-data",
        SyncItemKind.BookmarkFolder => "folder-sync-data",
        SyncItemKind.SidebarApp => "app-sync-data",
        SyncItemKind.Settings => "settings-sync-data",
        _ => "sync-data"
    };

    public bool IsSettingsAutoSyncEnabled(string category) => category switch
    {
        SettingsSyncCategory.LocalAi => AutoSyncLocalAi,
        SettingsSyncCategory.Lemonade => AutoSyncLemonade,
        SettingsSyncCategory.CloudProviders => AutoSyncCloudProviders,
        SettingsSyncCategory.ModelProfiles => AutoSyncModelProfiles,
        SettingsSyncCategory.HomeAssistant => AutoSyncHomeAssistant,
        SettingsSyncCategory.Tools => AutoSyncTools,
        SettingsSyncCategory.SystemPrompt => AutoSyncSystemPrompt,
        SettingsSyncCategory.Profile => AutoSyncProfile,
        SettingsSyncCategory.Memories => AutoSyncMemories,
        SettingsSyncCategory.Appearance => AutoSyncAppearance,
        SettingsSyncCategory.Skills => AutoSyncSkills,
        _ => false
    };

    public async Task EnqueueSettingsSyncAsync(string targetDeviceId, string category)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _settingsStore == null || string.IsNullOrWhiteSpace(category))
            return;

        if (!_isHubConnected())
        {
            SyncDebugLog.Info($"Cannot enqueue settings {category}: hub not connected.");
            return;
        }

        var payload = await _settingsStore.ExportAsync(category);
        if (payload == null)
            return;

        var dataJson = SettingsSyncPayload.Serialize(payload.Category, payload.UpdatedTicks, payload.DataJson);
        var fingerprint = SyncFingerprint.Compute(dataJson);
        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Settings,
            ItemId = category,
            DataJson = dataJson,
            ContentFingerprint = fingerprint
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    public async Task StartWebRtcSettingsSyncAsync(string targetDeviceId, string category) =>
        await EnqueueSettingsSyncAsync(targetDeviceId, category);

    public async Task<int> SyncSettingsCategoryToDevicesAsync(string category, IEnumerable<string> targetDeviceIds)
    {
        if (_settingsStore == null || string.IsNullOrWhiteSpace(category))
            return 0;

        var targets = targetDeviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0 || !_isHubConnected())
            return 0;

        var queued = 0;
        foreach (var targetId in targets)
        {
            var payload = await _settingsStore.ExportAsync(category);
            if (payload == null)
                continue;

            var dataJson = SettingsSyncPayload.Serialize(payload.Category, payload.UpdatedTicks, payload.DataJson);
            var fingerprint = SyncFingerprint.Compute(dataJson);
            if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Settings, category, fingerprint))
                continue;

            await EnqueueSettingsSyncAsync(targetId, category);
            queued++;
        }

        return Math.Min(queued, targets.Count);
    }

    public void ScheduleAutoSyncSettingsAfterLocalSave(string category)
    {
        if (_settingsStore == null
            || string.IsNullOrWhiteSpace(category)
            || !IsSettingsAutoSyncEnabled(category)
            || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"settings:{category}", async () =>
        {
            if (!_isHubConnected() || _settingsStore == null)
                return;

            var payload = await _settingsStore.ExportAsync(category);
            if (payload == null)
                return;

            var dataJson = SettingsSyncPayload.Serialize(payload.Category, payload.UpdatedTicks, payload.DataJson);
            var fingerprint = SyncFingerprint.Compute(dataJson);

            foreach (var targetId in SyncTargetDeviceIds)
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Settings, category, fingerprint))
                    continue;
                await EnqueueSettingsSyncAsync(targetId, category);
            }
        });
    }

    private async Task MaybeAutoSyncSettingsForPeerAsync(string deviceId)
    {
        if (_settingsStore == null)
            return;

        foreach (var category in SettingsSyncCategory.All)
        {
            if (!IsSettingsAutoSyncEnabled(category))
                continue;

            var payload = await _settingsStore.ExportAsync(category);
            if (payload == null)
                continue;

            var dataJson = SettingsSyncPayload.Serialize(payload.Category, payload.UpdatedTicks, payload.DataJson);
            var fingerprint = SyncFingerprint.Compute(dataJson);
            if (await IsItemAcknowledgedAsync(deviceId, SyncItemKind.Settings, category, fingerprint))
                continue;

            await EnqueueSettingsSyncAsync(deviceId, category);
        }
    }

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

    public async Task EnqueueAlbumDeleteAsync(string targetDeviceId, string albumId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected() || _galleryStore == null)
            return;

        var item = new SyncQueueItem
        {
            Kind = SyncItemKind.Album,
            IsDelete = true,
            ItemId = albumId,
            DataJson = DeleteSyncPayload.Serialize(albumId, deletedAtUtc.Ticks),
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
            var index = await _conversationStore.LoadIndexAsync();
            var convoMeta = index.FirstOrDefault(c => c.Id == convoId);
            var isProtected = convoMeta?.IsPasswordProtected == true;
            var proticks = convoMeta?.ProtectionChangedTicks ?? 0;
            var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom, isProtected, proticks);
            var fingerprint = SyncFingerprint.ForConversation(convoId, title, messages, isProtected, proticks);

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

        var noteIndex = await _noteStore.LoadIndexAsync();
        var noteManifest = await _noteStore.LoadManifestEntriesAsync();
        foreach (var noteId in response.NeededNotes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var noteMeta = noteIndex.FirstOrDefault(n => string.Equals(n.Id, noteId, StringComparison.OrdinalIgnoreCase));
            var manifestTitle = noteManifest.FirstOrDefault(n => string.Equals(n.Id, noteId, StringComparison.OrdinalIgnoreCase))?.Title;
            var title = ChatMessageHelper.ResolveOutgoingNoteTitle(noteMeta?.Title ?? manifestTitle, noteId);
            var isProtected = noteMeta?.IsPasswordProtected == true;
            var proticks = noteMeta?.ProtectionChangedTicks ?? 0;
            var titleTicks = noteMeta?.TitleChangedTicks ?? 0;
            var entries = await _noteStore.LoadNoteAsync(noteId);
            var dataJson = NoteSyncPayload.Serialize(noteId, title, entries, isProtected, proticks, titleTicks);
            var fingerprint = SyncFingerprint.ForNote(noteId, title, entries, isProtected, proticks, titleTicks);

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

        if (_galleryStore != null)
        {
            var albumIndex = await _galleryStore.LoadIndexAsync();
            foreach (var albumId in (response.NeededAlbums ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var albumMeta = albumIndex.FirstOrDefault(n => string.Equals(n.Id, albumId, StringComparison.OrdinalIgnoreCase));
                var title = ChatMessageHelper.ResolveOutgoingNoteTitle(albumMeta?.Title, albumId);
                await EnqueueAlbumMetaSyncAsync(peerId, albumId, title);
                queued++;
            }

            foreach (var composite in (response.NeededAlbumImages ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!GalleryImageSyncPayload.TrySplitCompositeId(composite, out var albumId, out var imageId))
                    continue;
                await EnqueueAlbumImageSyncAsync(peerId, albumId, imageId);
                queued++;
            }
        }

        if (_calendarStore != null)
        {
            foreach (var calendarId in (response.NeededCalendars ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await EnqueueCalendarMetaSyncAsync(peerId, calendarId);
                queued++;
            }

            foreach (var eventId in (response.NeededCalendarEvents ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var evt = await _calendarStore.LoadEventAsync(eventId);
                if (evt is null) continue;
                await EnqueueCalendarEventSyncAsync(peerId, evt.CalendarId, eventId);
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

    private Task EnqueueSyncAsync(string targetDeviceId, SyncQueueItem item, bool allowDuplicate = false) =>
        ExclusiveAsync(() => EnqueueSyncUnlockedAsync(targetDeviceId, item, allowDuplicate));

    private async Task EnqueueSyncUnlockedAsync(string targetDeviceId, SyncQueueItem item, bool allowDuplicate = false)
    {
        if (!_syncQueues.TryGetValue(targetDeviceId, out var queue))
        {
            queue = new Queue<SyncQueueItem>();
            _syncQueues[targetDeviceId] = queue;
        }

        var prioritizeNote = item.Kind == SyncItemKind.Note && !item.IsManifestExchange;
        if (prioritizeNote)
        {
            var list = queue.ToList();
            list.RemoveAll(i => ItemsMatchForDedup(i, item));
            list.Insert(0, item);
            queue.Clear();
            foreach (var queued in list)
                queue.Enqueue(queued);
        }
        else
        {
            if (!allowDuplicate && IsAlreadyQueuedOrActive(targetDeviceId, item))
            {
                SyncDebugLog.Info($"Skipping duplicate {KindLabel(item.Kind)} " +
                    $"{item.ItemId} for {targetDeviceId}");
                return;
            }

            queue.Enqueue(item);
        }

        var itemLabel = item.IsManifestExchange
            ? "manifest"
            : item.IsDelete
                ? $"{KindLabel(item.Kind)} delete {item.ItemId}"
                : $"{KindLabel(item.Kind)} {item.ItemId}";
        SyncDebugLog.Info($"Enqueued {itemLabel} for {targetDeviceId} (queue depth: {queue.Count}" +
            (prioritizeNote ? ", note priority" : "") + ")");
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

        try
        {
            if (await _webrtc.IsDataChannelOpenAsync(targetDeviceId))
            {
                SyncDebugLog.Info($"Starting WebRTC sync for {targetDeviceId}: {DescribeQueueItem(item)} (channel already open)");
                await SendActiveItemOnOpenChannelAsync(targetDeviceId, item);
                return;
            }

            StartHandshakeTimeout(targetDeviceId, item);
            var channelLabel = item.IsManifestExchange
                ? "app-sync-manifest"
                : ChannelLabelFor(item.Kind);
            SyncDebugLog.Info($"Starting WebRTC sync for {targetDeviceId}: {DescribeQueueItem(item)}");
            await StartWebRtcDataChannelAsync(targetDeviceId, channelLabel);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"WebRTC sync start failed: {ex.Message}");
            await FailActiveSyncAsync(targetDeviceId, ex.Message);
        }
    }

    private void StartHandshakeTimeout(string peerId, SyncQueueItem item) =>
        StartSyncTimeout(peerId, HandshakeTimeout, item);

    private void StartAckTimeout(string peerId, SyncQueueItem item, int payloadBytes = 0)
    {
        var duration = payloadBytes > LargePayloadThresholdBytes
            ? TimeoutForPayloadBytes(payloadBytes)
            : SmallItemAckTimeout;
        StartSyncTimeout(peerId, duration, item);
    }

    private void StartSyncTimeout(string peerId, TimeSpan duration, SyncQueueItem item)
    {
        CancelSyncTimeout(peerId);
        var cts = new CancellationTokenSource();
        _syncTimeoutByPeer[peerId] = cts;
        var itemId = item.ItemId;
        var kind = item.Kind;
        var isManifest = item.IsManifestExchange;
        var timeout = duration;

        StartDetached(async () =>
        {
            try
            {
                await Task.Delay(timeout, cts.Token);
            }
            catch (Exception ex) when (ex is TaskCanceledException or ObjectDisposedException)
            {
                return;
            }

            await ExclusiveAsync(async () =>
            {
                if (cts.Token.IsCancellationRequested)
                    return;
                if (!_activeSyncByPeer.TryGetValue(peerId, out var active))
                    return;
                if (active.IsManifestExchange != isManifest
                    || active.Kind != kind
                    || !string.Equals(active.ItemId, itemId, StringComparison.Ordinal))
                    return;

                // Answerer missed onopen but the channel is live (inbound already flowing).
                // Send instead of tearing down — closing here killed the peer's in-flight notes.
                if (!_outboundSentByPeer.Contains(peerId)
                    && await _webrtc.IsDataChannelOpenAsync(peerId))
                {
                    SyncDebugLog.Info(
                        $"Sync handshake still pending after {timeout.TotalSeconds:0}s for {peerId} " +
                        $"but DataChannel is open; sending {DescribeQueueItem(active)}");
                    await SendActiveItemOnOpenChannelAsync(peerId, active);
                    return;
                }

                SyncDebugLog.Info($"Sync timed out for peer {peerId} after {timeout.TotalSeconds:0}s (active: {DescribeQueueItem(active)})");
                await FailActiveSyncAsync(peerId, "timed out waiting for peer acknowledgement");
            });
        });
    }

    private void CancelSyncTimeout(string peerId)
    {
        if (_syncTimeoutByPeer.Remove(peerId, out var cts))
        {
            try { cts.Cancel(); } catch { /* already canceled */ }
            // Dispose after Delay has observed cancel — immediate Dispose races Task.Delay.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(250);
                    cts.Dispose();
                }
                catch { /* ignore */ }
            });
        }
    }

    private async Task FailActiveSyncAsync(string peerId, string reason)
    {
        if (!_activeSyncByPeer.TryGetValue(peerId, out var failedItem))
            return;

        _activeSyncByPeer.Remove(peerId);
        _outboundSentByPeer.Remove(peerId);
        CancelSyncTimeout(peerId);
        ClearChunkAssembliesForPeer(peerId);
        _offerInFlight.Remove(peerId);
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
            SyncDebugLog.Info($"Re-queued {DescribeQueueItem(failedItem)} " +
                $"for {peerId} (retry {failedItem.RetryCount}/{MaxSyncRetries})");
        }

        await ProcessSyncQueueAsync(peerId);
        // Stale glare SDPs must not be applied after a failed round — the peer has moved on.
        if (!_activeSyncByPeer.ContainsKey(peerId)
            && (!_syncQueues.TryGetValue(peerId, out var remaining) || remaining.Count == 0))
            _deferredRemoteOffers.Remove(peerId);
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
            return $"{KindLabel(item.Kind)} delete {DescribeItemId(item)}";
        return $"{KindLabel(item.Kind)} {DescribeItemId(item)}";
    }

    /// <summary>Prefer human name for album images when available.</summary>
    private static string DescribeItemId(SyncQueueItem item)
    {
        if (item.Kind == SyncItemKind.AlbumImage
            && !string.IsNullOrWhiteSpace(item.NoteTitle)
            && !string.Equals(item.NoteTitle, item.ItemId, StringComparison.Ordinal))
            return $"\"{item.NoteTitle}\" ({item.ItemId})";
        return item.ItemId;
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

    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title, bool forceAckClear = false)
    {
        if (!AutoSyncNotes || SyncTargetDeviceIds.Count == 0)
            return;

        lock (_pendingNotePushIds)
        {
            _pendingNotePushIds.Add(noteId);
            _pendingNotePushTitles[noteId] = title ?? "";
        }

        _ = DebouncedAutoSyncAsync($"note:{noteId}", NoteAutoSyncDebounce, async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            if (forceAckClear)
                await ClearPeerAckForItemAsync(SyncItemKind.Note, noteId);

            var manifest = await _noteStore.LoadManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(n => n.Id == noteId);
            var fingerprint = entry?.ContentFingerprint;

            var entries = await _noteStore.LoadNoteAsync(noteId);
            var targets = GetOnlineSyncTargetIdsInternal().ToList();
            var enqueued = false;
            foreach (var targetId in targets)
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Note, noteId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping note {noteId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueNoteSyncAsync(targetId, noteId, title ?? "", entries);
                enqueued = true;
            }

            if (enqueued || targets.Count > 0)
                ForgetPendingNotePush(noteId);
        });
    }

    public void ScheduleAutoSyncAlbumDeleteAfterLocalDelete(string albumId, DateTime deletedAtUtc)
    {
        if (!AutoSyncGallery || _galleryStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"album-delete:{albumId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Album, albumId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    SyncDebugLog.Info($"Skipping album delete {albumId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueAlbumDeleteAsync(targetId, albumId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncAlbumMetaAfterLocalSave(string albumId, string title)
    {
        if (!AutoSyncGallery || _galleryStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"album-meta:{albumId}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _galleryStore == null)
                return;

            var manifest = await _galleryStore.LoadManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(n => n.Id == albumId);
            var fingerprint = entry?.ContentFingerprint;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.Album, albumId, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping album meta {albumId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueAlbumMetaSyncAsync(targetId, albumId, title);
            }
        });
    }

    public void ScheduleAutoSyncAlbumImageAfterLocalSave(string albumId, string imageId)
    {
        if (!AutoSyncGallery || _galleryStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        var composite = GalleryImageSyncPayload.CompositeId(albumId, imageId);
        _ = DebouncedAutoSyncAsync($"album-image:{composite}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected() || _galleryStore == null)
                return;

            var imgManifest = await _galleryStore.LoadImageManifestEntriesAsync();
            var entry = imgManifest.FirstOrDefault(n => n.Id == composite);
            var fingerprint = entry?.ContentFingerprint;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.AlbumImage, composite, fingerprint))
                {
                    SyncDebugLog.Info($"Skipping album image {composite} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueAlbumImageSyncAsync(targetId, albumId, imageId);
            }
        });
    }

    public void ScheduleAutoSyncAlbumImageDeleteAfterLocalDelete(string albumId, string imageId, DateTime deletedAtUtc)
    {
        if (!AutoSyncGallery || _galleryStore == null || SyncTargetDeviceIds.Count == 0)
            return;

        var composite = GalleryImageSyncPayload.CompositeId(albumId, imageId);
        _ = DebouncedAutoSyncAsync($"album-image-delete:{composite}", async () =>
        {
            if (EnsureConnectedAsync != null)
                await EnsureConnectedAsync();
            if (!_isHubConnected())
                return;

            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (await IsItemAcknowledgedAsync(targetId, SyncItemKind.AlbumImage, composite, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                    continue;
                await EnqueueAlbumImageDeleteAsync(targetId, albumId, imageId, deletedAtUtc);
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

    private Task DebouncedAutoSyncAsync(string key, Func<Task> action) =>
        DebouncedAutoSyncAsync(key, AutoSyncDebounce, action);

    private async Task DebouncedAutoSyncAsync(string key, TimeSpan delay, Func<Task> action)
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
            await Task.Delay(delay, cts.Token);
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

    private void ForgetPendingNotePush(string noteId)
    {
        lock (_pendingNotePushIds)
        {
            _pendingNotePushIds.Remove(noteId);
            _pendingNotePushTitles.Remove(noteId);
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
        bool includeSidebarApps = false,
        bool includeAlbums = false,
        bool includeCalendars = false)
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

        if (includeAlbums && _galleryStore != null)
        {
            var albums = await _galleryStore.LoadManifestEntriesAsync();
            if (albums.Any(a => ManifestEntryHasPendingAck(ackState, SyncItemKind.Album, a)))
                return true;
            var albumImages = await _galleryStore.LoadImageManifestEntriesAsync();
            if (albumImages.Any(a => ManifestEntryHasPendingAck(ackState, SyncItemKind.AlbumImage, a)))
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

        if (includeCalendars && _calendarStore != null)
        {
            var workflowCalIds = await GetWorkflowCalendarIdsAsync();
            var calendars = (await _calendarStore.LoadCalendarManifestEntriesAsync())
                .Where(e => !IsWorkflowCalendarId(e.Id, workflowCalIds));
            if (calendars.Any(c => ManifestEntryHasPendingAck(ackState, SyncItemKind.Calendar, c)))
                return true;

            var events = await _calendarStore.LoadEventManifestEntriesAsync();
            foreach (var evt in events)
            {
                if (await IsWorkflowScopedEventAsync(evt.Id, workflowCalIds))
                    continue;
                if (ManifestEntryHasPendingAck(ackState, SyncItemKind.CalendarEvent, evt))
                    return true;
            }
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
        var includeAlbums = AutoSyncGallery && _galleryStore != null;
        var includeCalendars = AutoSyncCalendar && _calendarStore != null;
        var anySettingsAuto = AutoSyncLocalAi || AutoSyncLemonade || AutoSyncCloudProviders || AutoSyncModelProfiles
            || AutoSyncHomeAssistant || AutoSyncTools || AutoSyncSystemPrompt
            || AutoSyncProfile || AutoSyncMemories || AutoSyncAppearance
            || AutoSyncSkills;

        if (!AutoSyncChatHistory && !AutoSyncNotes && !includeAlbums && !includeBookmarks && !includeApps && !includeCalendars && !anySettingsAuto)
            return;

        try
        {
            if (AutoSyncChatHistory || AutoSyncNotes || includeAlbums || includeBookmarks || includeApps || includeCalendars)
            {
                var hasPending = await HasPendingOutboundSyncAsync(
                        deviceId,
                        AutoSyncChatHistory,
                        AutoSyncNotes,
                        includeBookmarks,
                        includeApps,
                        includeAlbums,
                        includeCalendars);

                List<(string Id, string Title)> pendingNotes;
                lock (_pendingNotePushIds)
                {
                    pendingNotes = _pendingNotePushIds
                        .Select(id => (id, _pendingNotePushTitles.GetValueOrDefault(id) ?? ""))
                        .ToList();
                }

                if (AutoSyncNotes && pendingNotes.Count > 0)
                {
                    foreach (var (noteId, title) in pendingNotes)
                    {
                        var entries = await _noteStore.LoadNoteAsync(noteId);
                        await EnqueueNoteSyncAsync(deviceId, noteId, title, entries);
                        ForgetPendingNotePush(noteId);
                    }
                    SyncDebugLog.Info($"Pushed {pendingNotes.Count} pending note(s) to {deviceId} on peer-online");
                }

                var queueBusy = _activeSyncByPeer.ContainsKey(deviceId)
                    || (_syncQueues.TryGetValue(deviceId, out var pendingQ) && pendingQ.Count > 0);

                if (queueBusy)
                {
                    SyncDebugLog.Info($"Skipping auto-sync manifest for {deviceId} (queue busy)");
                }
                else if (!hasPending && pendingNotes.Count == 0)
                {
                    SyncDebugLog.Info($"Skipping data auto-sync for {deviceId} (all items already acknowledged)");
                }
                else
                {
                    var lastVerified = await GetLastManifestVerifiedUtcAsync(deviceId);
                    if (lastVerified.HasValue && DateTime.UtcNow - lastVerified.Value < ManifestRecheckCooldown)
                    {
                        var minutesAgo = (int)(DateTime.UtcNow - lastVerified.Value).TotalMinutes;
                        SyncDebugLog.Info($"Skipping auto-sync for {deviceId} " +
                            $"(manifest verified {minutesAgo}m ago)");
                    }
                    else
                    {
                        await EnqueueManifestExchangeAsync(
                            deviceId,
                            AutoSyncChatHistory,
                            AutoSyncNotes,
                            includeBookmarks,
                            includeApps,
                            includeAlbums,
                            includeCalendars);
                        SyncDebugLog.Info($"Auto-sync manifest queued for {deviceId}");
                    }
                }
            }

            if (anySettingsAuto)
                await MaybeAutoSyncSettingsForPeerAsync(deviceId);
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

        _outboundSentByPeer.Remove(peerId);
        CancelSyncTimeout(peerId);
        ClearChunkAssembliesForPeer(peerId);
        // Successfully completed at least one outbound item — turn fulfilled.
        _owedOutboundTurn.Remove(peerId);

        if (!_syncQueues.TryGetValue(peerId, out var queue) || queue.Count == 0)
        {
            // Keep the DataChannel up so the next local save does not need a new ICE handshake.
            _deferredRemoteOffers.Remove(peerId);
            return;
        }

        var nextItem = queue.Dequeue();
        _activeSyncByPeer[peerId] = nextItem;

        try
        {
            var channelOpen = await _webrtc.IsDataChannelOpenAsync(peerId);
            if (channelOpen)
            {
                var reuseLabel = DescribeQueueItem(nextItem);
                SyncDebugLog.Info($"Reusing open channel for {peerId}: {reuseLabel}");
                await SendActiveItemOnOpenChannelAsync(peerId, nextItem);
                return;
            }

            StartHandshakeTimeout(peerId, nextItem);
            var channelLabel = nextItem.IsManifestExchange
                ? "app-sync-manifest"
                : ChannelLabelFor(nextItem.Kind);
            var label = nextItem.IsManifestExchange
                ? "manifest"
                : DescribeQueueItem(nextItem);
            SyncDebugLog.Info($"Starting WebRTC sync for {peerId}: {label}");
            await StartWebRtcDataChannelAsync(peerId, channelLabel);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"WebRTC sync start failed: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    private Task CloseWebRtcPeerAsync(string peerId)
    {
        _offerInFlight.Remove(peerId);
        return _webrtc.CloseAsync(peerId, suppressCallbacks: true);
    }

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
                SyncItemKind.Album => new DataChannelMessage(dataType, content: contentJson, itemId: itemId),
                _ => new DataChannelMessage(dataType, content: contentJson, itemId: itemId)
            };
            return await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(msg));
        }

        var chunkType = kind switch
        {
            SyncItemKind.Conversation => "sync-chunk",
            SyncItemKind.Note => "note-sync-chunk",
            SyncItemKind.Album => "album-sync-chunk",
            SyncItemKind.AlbumImage => "album-image-sync-chunk",
            SyncItemKind.Calendar => "calendar-sync-chunk",
            SyncItemKind.CalendarEvent => "calendar-event-sync-chunk",
            SyncItemKind.Bookmark => "bookmark-sync-chunk",
            SyncItemKind.BookmarkFolder => "folder-sync-chunk",
            SyncItemKind.SidebarApp => "app-sync-chunk",
            SyncItemKind.Settings => "settings-sync-chunk",
            _ => "sync-chunk"
        };

        var chunkCount = (contentBytes.Length + chunkPayloadBytes - 1) / chunkPayloadBytes;
        SyncDebugLog.Info($"Chunking sync payload for {itemId}: {contentBytes.Length} bytes -> {chunkCount} chunk(s)");

        if (!_activeSyncByPeer.TryGetValue(peerId, out var activeForChunks))
            return false;

        // Keep the per-item timeout alive for the whole multi-chunk transfer + peer assembly.
        StartSyncTimeout(peerId, TimeoutForPayloadBytes(contentBytes.Length), activeForChunks);

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
                itemId: kind is SyncItemKind.Album or SyncItemKind.AlbumImage or SyncItemKind.Bookmark or SyncItemKind.BookmarkFolder or SyncItemKind.SidebarApp or SyncItemKind.Settings
                    ? itemId
                    : null);

            // Pace sends so the SCTP/WebRTC outbound buffer is not flooded (silent drops).
            await _webrtc.WaitForSendBufferAsync(peerId, maxBufferedBytes: Math.Max(chunkPayloadBytes * 2, 256 * 1024));

            var sent = await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(chunkMsg));
            if (!sent)
            {
                SyncDebugLog.Info($"Chunk send failed for {itemId} at {i + 1}/{chunkCount}");
                return false;
            }

            if (i == 0 || (i + 1) % 50 == 0 || i + 1 == chunkCount)
            {
                SyncDebugLog.Info($"Chunk progress for {itemId}: {i + 1}/{chunkCount} ({chunkType})");
            }
        }

        // Wait for outbound buffer to drain so the peer has actually received chunks before we
        // only wait on ack (SCTP may still be delivering after the last Send returns).
        for (var drain = 0; drain < 40; drain++)
        {
            await _webrtc.WaitForSendBufferAsync(peerId, maxBufferedBytes: 4096);
            await Task.Delay(25);
        }

        // After enqueueing all chunks, give the peer time to assemble, persist, and ack.
        if (_activeSyncByPeer.TryGetValue(peerId, out var activeAfterSend))
            StartAckTimeout(peerId, activeAfterSend, contentBytes.Length);
        SyncDebugLog.Info($"Finished sending {chunkCount} chunk(s) of {chunkType} for {itemId} ({contentBytes.Length} bytes)");
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

    /// <summary>
    /// Smaller device-id is the only side that creates offers. Stops both peers offering
    /// at once, destroying each other's RTCPeerConnection, and timing out ICE.
    /// </summary>
    private bool IsDesignatedOffererToward(string remoteDeviceId)
    {
        if (string.IsNullOrEmpty(LocalDeviceId) || string.IsNullOrEmpty(remoteDeviceId))
            return false;
        return string.Compare(LocalDeviceId, remoteDeviceId, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private async Task SendActiveItemOnOpenChannelAsync(string peerId, SyncQueueItem item)
    {
        _outboundSentByPeer.Add(peerId);

        if (item.IsManifestExchange)
        {
            var sent = await _webrtc.SendDataAsync(
                peerId,
                SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-offer", content: item.DataJson)));
            if (!sent)
                await FailActiveSyncAsync(peerId, "data channel not ready for manifest");
            else
            {
                SyncDebugLog.Info($"Sent sync-manifest-offer to {peerId}");
                StartAckTimeout(peerId, item, item.DataJson.Length);
            }
            return;
        }

        await TrySendActiveItemAsync(peerId, item);
    }

    /// <summary>
    /// Answerer often sees inbound messages before <see cref="OnDataChannelOpenAsync"/>.
    /// If we have a queued outbound item and the channel can send, start it now.
    /// </summary>
    private async Task EnsureActiveOutboundSentAsync(string peerId)
    {
        if (!_activeSyncByPeer.TryGetValue(peerId, out var item))
            return;
        if (_outboundSentByPeer.Contains(peerId))
            return;
        if (!await _webrtc.IsDataChannelOpenAsync(peerId))
            return;

        SyncDebugLog.Info($"DataChannel live for {peerId}; sending pending {DescribeQueueItem(item)}");
        await SendActiveItemOnOpenChannelAsync(peerId, item);
    }

    private async Task StartWebRtcDataChannelAsync(string targetDeviceId, string channelLabel)
    {
        if (await _webrtc.IsDataChannelOpenAsync(targetDeviceId))
            return;

        if (!IsDesignatedOffererToward(targetDeviceId))
        {
            SyncDebugLog.WebRtc($"Waiting for offer from {targetDeviceId} (they are designated offerer)");
            await _sendSignalingAsync(targetDeviceId, "webrtc-need-offer", "");
            return;
        }

        if (!_offerInFlight.Add(targetDeviceId))
        {
            SyncDebugLog.WebRtc($"Offer already in flight to {targetDeviceId}");
            return;
        }

        try
        {
            await _webrtc.CreatePeerConnectionAsync(targetDeviceId, _transportCallbacks);
            await _webrtc.CreateDataChannelAsync(targetDeviceId, channelLabel);

            var offerJson = await _webrtc.CreateOfferAsync(targetDeviceId);
            SyncDebugLog.WebRtc($"Sending offer to {targetDeviceId}");
            await _sendSignalingAsync(targetDeviceId, "webrtc-offer", offerJson ?? "");
        }
        catch
        {
            _offerInFlight.Remove(targetDeviceId);
            throw;
        }
    }

    private async Task HandleNeedOfferAsync(string fromDeviceId)
    {
        if (!IsDesignatedOffererToward(fromDeviceId))
            return;
        if (await _webrtc.IsDataChannelOpenAsync(fromDeviceId))
            return;

        var label = "app-sync";
        if (_activeSyncByPeer.TryGetValue(fromDeviceId, out var active))
            label = active.IsManifestExchange ? "app-sync-manifest" : ChannelLabelFor(active.Kind);

        SyncDebugLog.WebRtc($"Peer {fromDeviceId} requested an offer");
        await StartWebRtcDataChannelAsync(fromDeviceId, label);
    }

    private async Task HandleWebRtcOffer(string fromDeviceId, string offerJson)
    {
        SyncDebugLog.WebRtc($"Received offer from {fromDeviceId}");

        // Dual-offer is what left Chromium with "Applied answer" and no DataChannel:
        // each side created a PC, then the polite side destroyed it to answer.
        if (IsDesignatedOffererToward(fromDeviceId))
        {
            SyncDebugLog.WebRtc($"Ignoring offer from {fromDeviceId}; we are the designated offerer");
            return;
        }

        try
        {
            await ApplyRemoteOfferAsync(fromDeviceId, offerJson);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Handle offer failed: {ex.Message}");
        }
    }

    private async Task ApplyRemoteOfferAsync(string fromDeviceId, string offerJson)
    {
        await _webrtc.CreatePeerConnectionAsync(fromDeviceId, _transportCallbacks);
        await _webrtc.SetRemoteDescriptionAsync(fromDeviceId, offerJson);

        var answerJson = await _webrtc.CreateAnswerAsync(fromDeviceId);
        await _sendSignalingAsync(fromDeviceId, "webrtc-answer", answerJson ?? "");
        SyncDebugLog.WebRtc($"Sent answer to {fromDeviceId}");
    }

    /// <summary>Apply any deferred remote offer once we are no longer sending to that peer.</summary>
    private async Task FlushDeferredRemoteOfferAsync(string peerId)
    {
        if (_activeSyncByPeer.ContainsKey(peerId))
            return;
        if (_syncQueues.TryGetValue(peerId, out var q) && q.Count > 0)
            return;
        if (!_deferredRemoteOffers.Remove(peerId, out var offerJson))
            return;

        SyncDebugLog.Info($"Applying deferred remote offer from {peerId}");
        try
        {
            await ApplyRemoteOfferAsync(peerId, offerJson);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Deferred offer apply failed for {peerId}: {ex.Message}");
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
        var iceJson = TryUnwrapIcePayload(payload, out _, out var unwrapped) ? unwrapped : payload;
        SyncDebugLog.WebRtc($"ICE from {fromDeviceId}");
        await _webrtc.AddIceCandidateAsync(fromDeviceId, iceJson);
    }

    public async Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        if (!_isHubConnected())
            return;

        try
        {
            JsonElement iceEl;
            var trimmed = (candidateJson ?? "").TrimStart();
            if (trimmed.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(candidateJson);
                iceEl = doc.RootElement.Clone();
            }
            else
            {
                iceEl = JsonSerializer.SerializeToElement(new
                {
                    candidate = candidateJson,
                    sdpMid = "0",
                    sdpMLineIndex = 0
                });
            }

            var signalingPayload = JsonSerializer.Serialize(new
            {
                peerKey = peerId,
                ice = iceEl
            });

            await _sendSignalingAsync(peerId, "webrtc-ice", signalingPayload);
            SyncDebugLog.WebRtc($"ICE sent to {peerId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.WebRtc($"ICE send failed for {peerId}: {ex.Message}");
        }
    }

    public Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default) =>
        ExclusiveAsync(() => OnDataChannelOpenUnlockedAsync(peerId, ct));

    private async Task OnDataChannelOpenUnlockedAsync(string peerId, CancellationToken ct)
    {
        SyncDebugLog.Info($"DataChannel open for peer {peerId}");
        _offerInFlight.Remove(peerId);
        // Our offer won (or we are answerer on a live channel) — discard any deferred glare SDP.
        _deferredRemoteOffers.Remove(peerId);
        await EnsureActiveOutboundSentAsync(peerId);

        // Channel came up after a failed round or lid-close. Drain pending notes even if
        // a recent manifest verification would otherwise skip catch-up.
        if (!_activeSyncByPeer.ContainsKey(peerId)
            && (!_syncQueues.TryGetValue(peerId, out var openQ) || openQ.Count == 0))
        {
            ScheduleMaybeAutoSyncPeer(peerId);
        }
    }

    private async Task TrySendActiveItemAsync(string peerId, SyncQueueItem item)
    {
        try
        {
            // Mark in-flight before the first await so a reentrant DataChannel callback
            // cannot start a second overlapping send of the same item.
            _outboundSentByPeer.Add(peerId);

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
                StartAckTimeout(peerId, item, item.DataJson.Length);
                return;
            }

            var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(item.DataJson);
            SyncDebugLog.Info($"Preparing {KindLabel(item.Kind)} sync payload " +
                $"for {DescribeItemId(item)} ({payloadBytes} bytes)");

            var payloadSent = await SendSyncPayloadAsync(peerId, item.Kind, item.ItemId, item.DataJson);
            if (!payloadSent)
            {
                SyncDebugLog.Info($"webrtcSendData failed (channel not ready) for {peerId}");
                await FailActiveSyncAsync(peerId, "data channel not ready for send");
                return;
            }

            StartAckTimeout(peerId, item, payloadBytes);

            SyncDebugLog.Info($"Sent {DataTypeFor(item.Kind)} " +
                $"over DataChannel to {peerId} for {DescribeItemId(item)}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"DataChannel send failed for {peerId}: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    public void OnConnectionStateChange(string peerId, string state)
    {
        if (state is not ("failed" or "disconnected" or "closed"))
            return;

        StartDetached(() => ExclusiveAsync(async () =>
        {
            if (!_activeSyncByPeer.ContainsKey(peerId))
                return;
            await FailActiveSyncAsync(peerId, $"WebRTC connection {state}");
        }));
    }

    public Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default) =>
        ExclusiveAsync(() => OnDataReceivedUnlockedAsync(peerId, data, ct));

    private async Task OnDataReceivedUnlockedAsync(string peerId, string data, CancellationToken ct)
    {
        // Leave any JSInvokable call stack before IndexedDB/localStorage (WASM) or further JS.
        await Task.Yield();
        await EnsureActiveOutboundSentAsync(peerId);
        try
        {
            var msg = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(data);
            if (msg == null)
            {
                SyncDebugLog.Info($"Ignoring unreadable DataChannel payload from {peerId} ({data?.Length ?? 0} bytes)");
                return;
            }

            if (msg.type is "settings-sync-data" or "settings-sync-chunk" or "settings-sync-ack")
                SyncDebugLog.Info($"Incoming {msg.type} from {peerId} item={msg.itemId ?? "(none)"}");

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
                await HandleSyncAckAsync(msg.convoId, peerId);
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
                await HandleNoteSyncAckAsync(msg.noteId, peerId);
            }
            else if ((msg.type == "album-sync-data" || msg.type == "album-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "album-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.Album, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingAlbumSyncPayload(contentJson, peerId);

                var meta = GalleryAlbumMetaPayload.Deserialize(contentJson);
                if (meta?.AlbumId != null)
                {
                    var ack = new DataChannelMessage("album-sync-ack", itemId: meta.AlbumId);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "album-sync-ack" && msg.itemId != null)
            {
                await HandleAlbumSyncAckAsync(msg.itemId, peerId);
            }
            else if ((msg.type == "album-image-sync-data" || msg.type == "album-image-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "album-image-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.AlbumImage, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingAlbumImageSyncPayload(contentJson, peerId);

                var imgPayload = GalleryImageSyncPayload.Deserialize(contentJson);
                if (imgPayload?.AlbumId != null && imgPayload.Image?.Id != null)
                {
                    var composite = GalleryImageSyncPayload.CompositeId(imgPayload.AlbumId, imgPayload.Image.Id);
                    var ack = new DataChannelMessage("album-image-sync-ack", itemId: composite);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "album-image-sync-ack" && msg.itemId != null)
            {
                await HandleGenericItemAckAsync("album-image-sync-ack", msg.itemId, peerId);
            }
            else if (await TryHandleCalendarDataChannelAsync(peerId, msg.type ?? "", msg.content, msg.itemId, msg.chunkIndex, msg.chunkCount, msg.chunkData))
            {
                // calendar-sync-*, calendar-event-*, calendar-*-delete handled in partial
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
                await HandleGenericItemAckAsync("bookmark-sync-ack", msg.itemId, peerId);
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
                await HandleGenericItemAckAsync("folder-sync-ack", msg.itemId, peerId);
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
                await HandleGenericItemAckAsync("app-sync-ack", msg.itemId, peerId);
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
                await HandleConvoDeleteAckAsync(msg.convoId, peerId);
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
                await HandleNoteDeleteAckAsync(msg.noteId, peerId);
            }
            else if (msg.type == "album-delete" && msg.content != null)
            {
                await HandleIncomingAlbumDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("album-delete-ack", itemId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "album-delete-ack" && msg.itemId != null)
            {
                await HandleGenericItemAckAsync("album-delete-ack", msg.itemId, peerId);
            }
            else if (msg.type == "album-image-delete" && msg.content != null)
            {
                await HandleIncomingAlbumImageDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("album-image-delete-ack", itemId: deletePayload.Id);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "album-image-delete-ack" && msg.itemId != null)
            {
                await HandleGenericItemAckAsync("album-image-delete-ack", msg.itemId, peerId);
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
                await HandleGenericItemAckAsync("bookmark-delete-ack", msg.itemId, peerId);
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
                await HandleGenericItemAckAsync("folder-delete-ack", msg.itemId, peerId);
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
                await HandleGenericItemAckAsync("app-delete-ack", msg.itemId, peerId);
            }
            else if ((msg.type == "settings-sync-data" || msg.type == "settings-sync-chunk")
                     && (msg.content != null || msg.itemId != null))
            {
                var contentJson = msg.content;
                if (msg.type == "settings-sync-chunk")
                {
                    if (msg.itemId == null
                        || !TryAddChunk(peerId, msg.itemId, SyncItemKind.Settings, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                var appliedOrIgnored = await HandleIncomingSettingsSyncPayload(contentJson, peerId);

                var settingsPayload = SettingsSyncPayload.Deserialize(contentJson);
                if (appliedOrIgnored && settingsPayload?.Category != null)
                {
                    var ack = new DataChannelMessage("settings-sync-ack", itemId: settingsPayload.Category);
                    await _webrtc.SendDataAsync(peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "settings-sync-ack" && msg.itemId != null)
            {
                await HandleGenericItemAckAsync("settings-sync-ack", msg.itemId, peerId);
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
            {
                SyncDebugLog.Info(
                    $"Note {remote.Id} needed from peer: remoteFp={remote.ContentFingerprint} " +
                    $"localFp={local?.ContentFingerprint ?? "(none)"}");
                neededNotes.Add(remote.Id);
            }
            else
                upToDateNotes++;
        }

        var neededAlbums = new List<string>();
        var senderShouldDeleteAlbums = new List<DeleteSyncPayload>();
        var upToDateAlbums = 0;
        var appliedAlbumDeletes = 0;
        var neededAlbumImages = new List<string>();
        var senderShouldDeleteAlbumImages = new List<DeleteSyncPayload>();
        var upToDateAlbumImages = 0;
        var appliedAlbumImageDeletes = 0;
        var remoteAlbums = offer.Albums ?? [];
        var remoteAlbumImages = offer.AlbumImages ?? [];
        if (_galleryStore != null)
        {
            var localAlbums = await _galleryStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteAlbums)
            {
                var local = localAlbums.FirstOrDefault(n => string.Equals(n.Id, remote.Id, StringComparison.Ordinal));

                if (remote.IsDeleted)
                {
                    if (await _galleryStore.TryApplyRemoteAlbumDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedAlbumDeletes++;
                    upToDateAlbums++;
                    continue;
                }

                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteAlbums.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateAlbums++;
                    continue;
                }

                if (ManifestEntryNeedsSync(remote, local))
                    neededAlbums.Add(remote.Id);
                else
                    upToDateAlbums++;
            }

            var localImages = await _galleryStore.LoadImageManifestEntriesAsync();
            foreach (var remote in remoteAlbumImages)
            {
                var local = localImages.FirstOrDefault(n => string.Equals(n.Id, remote.Id, StringComparison.Ordinal));

                if (remote.IsDeleted)
                {
                    if (GalleryImageSyncPayload.TrySplitCompositeId(remote.Id, out var aId, out var iId)
                        && await _galleryStore.TryApplyRemoteImageDeleteAsync(aId, iId, remote.DeletedAtTicks!.Value))
                        appliedAlbumImageDeletes++;
                    upToDateAlbumImages++;
                    continue;
                }

                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteAlbumImages.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateAlbumImages++;
                    continue;
                }

                if (ManifestEntryNeedsSync(remote, local))
                    neededAlbumImages.Add(remote.Id);
                else
                    upToDateAlbumImages++;
            }
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

        var neededCalendars = new List<string>();
        var senderShouldDeleteCalendars = new List<DeleteSyncPayload>();
        var upToDateCalendars = 0;
        var appliedCalendarDeletes = 0;
        var neededCalendarEvents = new List<string>();
        var senderShouldDeleteCalendarEvents = new List<DeleteSyncPayload>();
        var upToDateCalendarEvents = 0;
        var appliedCalendarEventDeletes = 0;
        var remoteCalendars = offer.Calendars ?? [];
        var remoteCalendarEvents = offer.CalendarEvents ?? [];
        if (_calendarStore != null)
        {
            var workflowCalIds = await GetWorkflowCalendarIdsAsync();
            var localCals = await _calendarStore.LoadCalendarManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteCalendars)
            {
                // Never request/apply Workflows system calendar from peers.
                if (IsWorkflowCalendarId(remote.Id, workflowCalIds))
                {
                    upToDateCalendars++;
                    continue;
                }
                var local = localCals.FirstOrDefault(n => string.Equals(n.Id, remote.Id, StringComparison.Ordinal));
                if (remote.IsDeleted)
                {
                    if (await _calendarStore.TryApplyRemoteCalendarDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedCalendarDeletes++;
                    upToDateCalendars++;
                    continue;
                }
                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteCalendars.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateCalendars++;
                    continue;
                }
                if (ManifestEntryNeedsSync(remote, local))
                    neededCalendars.Add(remote.Id);
                else
                    upToDateCalendars++;
            }

            var localEvents = await _calendarStore.LoadEventManifestEntriesAsync(backfillMissingFingerprints: true);
            foreach (var remote in remoteCalendarEvents)
            {
                if (await IsWorkflowScopedEventAsync(remote.Id, workflowCalIds))
                {
                    upToDateCalendarEvents++;
                    continue;
                }
                var local = localEvents.FirstOrDefault(n => string.Equals(n.Id, remote.Id, StringComparison.Ordinal));
                if (remote.IsDeleted)
                {
                    if (await _calendarStore.TryApplyRemoteEventDeleteAsync(remote.Id, remote.DeletedAtTicks!.Value))
                        appliedCalendarEventDeletes++;
                    upToDateCalendarEvents++;
                    continue;
                }
                if (local != null && LocalDeleteShouldWinOverRemote(remote, local))
                {
                    senderShouldDeleteCalendarEvents.Add(new DeleteSyncPayload(remote.Id, local.DeletedAtTicks!.Value));
                    upToDateCalendarEvents++;
                    continue;
                }
                if (ManifestEntryNeedsSync(remote, local))
                    neededCalendarEvents.Add(remote.Id);
                else
                    upToDateCalendarEvents++;
            }
        }

        if (appliedConvoDeletes > 0)
            Raise(OnConversationsChanged);
        if (appliedNoteDeletes > 0)
            Raise(OnNotesChanged);
        if (appliedAlbumDeletes > 0)
            Raise(OnGalleryChanged);
        if (appliedCalendarDeletes + appliedCalendarEventDeletes > 0)
            Raise(OnCalendarsChanged);
        if (appliedBookmarkDeletes + appliedFolderDeletes > 0)
            Raise(OnBookmarksChanged);
        if (appliedAppDeletes > 0)
            Raise(OnInstalledAppsChanged);

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
            senderShouldDeleteApps,
            neededAlbums,
            upToDateAlbums,
            senderShouldDeleteAlbums,
            neededAlbumImages,
            upToDateAlbumImages,
            senderShouldDeleteAlbumImages,
            neededCalendars,
            upToDateCalendars,
            senderShouldDeleteCalendars,
            neededCalendarEvents,
            upToDateCalendarEvents,
            senderShouldDeleteCalendarEvents);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(response);
        await _webrtc.SendDataAsync(
            peerId,
            SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-response", content: responseJson)));

        SyncDebugLog.Info($"Manifest offer from {peerId}: " +
            $"{upToDateConvos}/{offer.Convos.Count} convos, {upToDateNotes}/{offer.Notes.Count} notes, " +
            $"{upToDateAlbums}/{remoteAlbums.Count} albums, {upToDateCalendars}/{remoteCalendars.Count} calendars, " +
            $"{upToDateCalendarEvents}/{remoteCalendarEvents.Count} cal-events up to date");
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

        var appliedAlbumDeletes = 0;
        var appliedAlbumImageDeletes = 0;
        if (_galleryStore != null)
        {
            foreach (var del in response.SenderShouldDeleteAlbums ?? [])
            {
                if (await _galleryStore.TryApplyRemoteAlbumDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedAlbumDeletes++;
            }
            foreach (var del in response.SenderShouldDeleteAlbumImages ?? [])
            {
                if (GalleryImageSyncPayload.TrySplitCompositeId(del.Id, out var aId, out var iId)
                    && await _galleryStore.TryApplyRemoteImageDeleteAsync(aId, iId, del.DeletedAtTicks))
                    appliedAlbumImageDeletes++;
            }
        }

        var appliedCalendarDeletes = 0;
        var appliedCalendarEventDeletes = 0;
        if (_calendarStore != null)
        {
            foreach (var del in response.SenderShouldDeleteCalendars ?? [])
            {
                if (await _calendarStore.TryApplyRemoteCalendarDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedCalendarDeletes++;
            }
            foreach (var del in response.SenderShouldDeleteCalendarEvents ?? [])
            {
                if (await _calendarStore.TryApplyRemoteEventDeleteAsync(del.Id, del.DeletedAtTicks))
                    appliedCalendarEventDeletes++;
            }
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
            Raise(OnConversationsChanged);
        if (appliedNoteDeletes > 0)
            Raise(OnNotesChanged);
        if (appliedAlbumDeletes > 0)
            Raise(OnGalleryChanged);
        if (appliedCalendarDeletes + appliedCalendarEventDeletes > 0)
            Raise(OnCalendarsChanged);
        if (appliedBookmarkDeletes + appliedFolderDeletes > 0)
            Raise(OnBookmarksChanged);
        if (appliedAppDeletes > 0)
            Raise(OnInstalledAppsChanged);

        var queued = await QueueNeededItemsFromManifestAsync(peerId, response);
        var noNeeded =
            response.NeededConvos.Count == 0
            && response.NeededNotes.Count == 0
            && (response.NeededAlbums?.Count ?? 0) == 0
            && (response.NeededAlbumImages?.Count ?? 0) == 0
            && (response.NeededBookmarks?.Count ?? 0) == 0
            && (response.NeededBookmarkFolders?.Count ?? 0) == 0
            && (response.NeededSidebarApps?.Count ?? 0) == 0;
        if (noNeeded)
            await RecordManifestVerifiedAsync(peerId);
        if (queued == 0)
            SyncDebugLog.Info($"Delta sync complete for {peerId} - peer is up to date");
        await AdvanceSyncQueueAsync(peerId);
    }

    private async Task TryAdvanceOnMatchingAckAsync(string type, string itemId, string peerId)
    {
        if (!_activeSyncByPeer.TryGetValue(peerId, out var item))
        {
            SyncDebugLog.Info($"Ignoring {type} for {itemId} from {peerId} (no active sync)");
            return;
        }

        if (item.IsManifestExchange
            || !string.Equals(item.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
        {
            SyncDebugLog.Info($"Ignoring {type} for {itemId} (active is {DescribeQueueItem(item)})");
            return;
        }

        SyncDebugLog.Info($"Received {type} for {itemId} from peer {peerId}");
        await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private async Task HandleSyncAckAsync(string convoId, string peerId)
    {
        await TryAdvanceOnMatchingAckAsync("sync-ack", convoId, peerId);
        Raise(OnSyncAckReceived, convoId, peerId);
    }

    private async Task HandleNoteSyncAckAsync(string noteId, string peerId)
    {
        await TryAdvanceOnMatchingAckAsync("note-sync-ack", noteId, peerId);
        Raise(OnNoteSyncAckReceived, noteId, peerId);
    }

    private Task HandleGenericItemAckAsync(string type, string itemId, string peerId) =>
        TryAdvanceOnMatchingAckAsync(type, itemId, peerId);

    private Task HandleConvoDeleteAckAsync(string convoId, string peerId) =>
        TryAdvanceOnMatchingAckAsync("convo-delete-ack", convoId, peerId);

    private Task HandleNoteDeleteAckAsync(string noteId, string peerId) =>
        TryAdvanceOnMatchingAckAsync("note-delete-ack", noteId, peerId);

    private async Task HandleIncomingNoteSyncPayload(string json, string fromDeviceId)
    {
        try
        {
            var payload = NoteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.NoteId))
                return;

            var remoteEntries = ChatMessageHelper.NormalizeAll(payload.Entries);
            if (!await _noteStore.ShouldAcceptIncomingContentAsync(payload.NoteId, remoteEntries))
            {
                SyncDebugLog.Info($"Ignoring stale note sync for {payload.NoteId} (local delete is newer)");
                return;
            }

            var localEntries = await _noteStore.LoadNoteAsync(payload.NoteId);
            var merge = NoteSyncMerger.Merge(localEntries, remoteEntries);

            var index = await _noteStore.LoadIndexAsync();
            var localMeta = index.FirstOrDefault(n =>
                string.Equals(n.Id, payload.NoteId, StringComparison.OrdinalIgnoreCase));
            var localTitle = localMeta?.Title ?? await _noteStore.GetMetaTitleAsync(payload.NoteId);
            var titleChanged = ChatMessageHelper.TryResolveIncomingNoteTitle(
                payload.Title,
                payload.TitleChangedTicks,
                localTitle,
                localMeta?.TitleChangedTicks ?? 0,
                payload.NoteId,
                out var title,
                out var titleTicks);

            // Persist when the merge changed local content (or note was empty and remote arrived).
            if (merge.DiffersFromLocal || localEntries.Count == 0)
            {
                await _noteStore.SaveNoteAsync(payload.NoteId, merge.Entries);
                await _noteStore.UpdateIndexAfterSaveAsync(
                    payload.NoteId, title, merge.Entries, titleChanged ? titleTicks : null);
            }
            else if (titleChanged)
            {
                await _noteStore.UpdateIndexAfterSaveAsync(
                    payload.NoteId, title, merge.Entries, titleTicks);
            }
            else
            {
                // Identical body: still rewrite the stored fingerprint so a serializer
                // change cannot keep the note "needed" on every manifest.
                await _noteStore.UpdateIndexAfterSaveAsync(
                    payload.NoteId, title, merge.Entries, bumpLastUpdated: false);
            }

            // Versioned protection merge: never demote a local lock from stale remote false.
            await ApplyIncomingNoteProtectionAsync(payload);

            Raise(OnNoteSyncPayloadReceived, payload.NoteId, json, fromDeviceId);
            Raise(OnNotesChanged);

            // If we kept local-only entries (or LWW chose local versions), push the merged
            // notebook back so the peer converges instead of staying on a partial overwrite.
            if (merge.DiffersFromRemote)
            {
                SyncDebugLog.Info(
                    $"Note {payload.NoteId} merge kept local-only or newer local entries " +
                    $"(localOnly={merge.LocalOnlyCount}, remoteOnly={merge.RemoteOnlyCount}, " +
                    $"conflicts={merge.ResolvedConflicts}); scheduling push-back");
                ScheduleAutoSyncNoteAfterLocalSave(payload.NoteId, title);
            }

            SyncDebugLog.Info(
                $"Merged incoming note sync for {payload.NoteId} from {fromDeviceId} " +
                $"(remote={remoteEntries.Count}, local={localEntries.Count}, merged={merge.Entries.Count}, " +
                $"localOnly={merge.LocalOnlyCount}, remoteOnly={merge.RemoteOnlyCount}, " +
                $"entryConflicts={merge.ResolvedConflicts}, protected={payload.IsPasswordProtected?.ToString() ?? "omit"}, " +
                $"proticks={payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming note sync payload: {ex.Message}");
        }
    }

    private async Task HandleAlbumSyncAckAsync(string albumId, string peerId)
    {
        await TryAdvanceOnMatchingAckAsync("album-sync-ack", albumId, peerId);
        Raise(OnAlbumSyncAckReceived, albumId, peerId);
    }

    private async Task HandleIncomingAlbumSyncPayload(string json, string fromDeviceId)
    {
        if (_galleryStore == null)
            return;

        try
        {
            // Prefer meta payload (small); fall back not supported for legacy whole-album.
            var meta = GalleryAlbumMetaPayload.Deserialize(json);
            if (meta == null || string.IsNullOrEmpty(meta.AlbumId))
            {
                SyncDebugLog.Info("Ignoring unrecognized album sync payload (expect album meta)");
                return;
            }

            await _galleryStore.ApplyRemoteAlbumMetaAsync(
                meta.AlbumId,
                meta.Title,
                meta.IsPasswordProtected,
                meta.ProtectionChangedTicks);

            Raise(OnAlbumSyncPayloadReceived, meta.AlbumId, json, fromDeviceId);
            NotifyGalleryChangedDebounced();
            SyncDebugLog.Info($"Applied album meta for {meta.AlbumId} from {fromDeviceId} (images refs={meta.Images?.Count ?? 0})");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming album meta: {ex.Message}");
        }
    }

    private async Task HandleIncomingAlbumImageSyncPayload(string json, string fromDeviceId)
    {
        if (_galleryStore == null)
            return;

        try
        {
            var payload = GalleryImageSyncPayload.Deserialize(json);
            if (payload?.Image == null || string.IsNullOrEmpty(payload.AlbumId) || string.IsNullOrEmpty(payload.Image.Id))
                return;

            if (!await _galleryStore.ShouldAcceptIncomingImageAsync(payload.AlbumId, payload.Image))
            {
                SyncDebugLog.Info($"Ignoring stale album image {payload.AlbumId}/{payload.Image.Id}");
                return;
            }

            // Ensure album exists
            var title = await _galleryStore.GetMetaTitleAsync(payload.AlbumId);
            if (title == null)
                await _galleryStore.CreateAlbumAsync(payload.AlbumId, "(empty)");

            var normalized = GallerySyncMerger.NormalizeAll(new[] { payload.Image })[0];
            if (_storageQuota != null && !string.IsNullOrEmpty(normalized.DataBase64))
            {
                var need = (long)(normalized.DataBase64.Length * 0.75);
                if (!await _storageQuota.CanAcceptBytesAsync(need))
                {
                    SyncDebugLog.Info(
                        $"Skipped album image {payload.AlbumId}/{normalized.Id} from {fromDeviceId}: storage limit");
                    // Still ack so sender does not loop forever; user can raise limit and re-sync later.
                    return;
                }
            }

            await _galleryStore.UpsertImageAsync(payload.AlbumId, normalized);
            Raise(OnAlbumSyncPayloadReceived, payload.AlbumId, json, fromDeviceId);
            // Debounce: multi-image sync was re-rendering the whole album after every image.
            NotifyGalleryChangedDebounced();
            SyncDebugLog.Info($"Applied album image {payload.AlbumId}/{payload.Image.Id} from {fromDeviceId} size={payload.Image.Size}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist album image: {ex.Message}");
        }
    }

    private async Task HandleIncomingAlbumDeleteAsync(string json, string fromDeviceId)
    {
        if (_galleryStore == null)
            return;

        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _galleryStore.TryApplyRemoteAlbumDeleteAsync(payload.Id, payload.DeletedAtTicks))
            {
                Raise(OnGalleryChanged);
                SyncDebugLog.Info($"Applied remote album delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply album delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingAlbumImageDeleteAsync(string json, string fromDeviceId)
    {
        if (_galleryStore == null)
            return;

        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;
            if (!GalleryImageSyncPayload.TrySplitCompositeId(payload.Id, out var albumId, out var imageId))
                return;

            if (await _galleryStore.TryApplyRemoteImageDeleteAsync(albumId, imageId, payload.DeletedAtTicks))
            {
                Raise(OnGalleryChanged);
                SyncDebugLog.Info($"Applied remote album image delete {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to apply album image delete: {ex.Message}");
        }
    }
    private async Task ApplyIncomingNoteProtectionAsync(NoteSyncPayload payload)
    {
        var index = await _noteStore.LoadIndexAsync();
        var local = index.FirstOrDefault(n => n.Id == payload.NoteId);
        var localProtected = local?.IsPasswordProtected == true;
        var localTicks = local?.ProtectionChangedTicks ?? 0;

        if (!PasswordProtectionSync.TryResolve(
                payload.IsPasswordProtected,
                payload.ProtectionChangedTicks,
                localProtected,
                localTicks,
                out var applyProtected,
                out var applyTicks))
        {
            SyncDebugLog.Info(
                $"Note {payload.NoteId} protection unchanged " +
                $"(local={localProtected}/{localTicks}, remote={payload.IsPasswordProtected?.ToString() ?? "omit"}/{payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
            return;
        }

        await _noteStore.SetPasswordProtectedAsync(payload.NoteId, applyProtected, applyTicks);
        SyncDebugLog.Info(
            $"Note {payload.NoteId} protection applied: {localProtected}/{localTicks} -> {applyProtected}/{applyTicks} " +
            $"(remote={payload.IsPasswordProtected}/{payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
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
            Raise(OnBookmarksChanged);
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
            Raise(OnBookmarksChanged);
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
            Raise(OnInstalledAppsChanged);
            SyncDebugLog.Info($"Applied incoming sidebar app sync for {payload.App.Id} from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming sidebar app sync: {ex.Message}");
        }
    }

    /// <returns>True when the payload was applied or rejected as stale (ack). False on hard failure (no ack).</returns>
    private async Task<bool> HandleIncomingSettingsSyncPayload(string json, string fromDeviceId)
    {
        if (_settingsStore == null)
        {
            SyncDebugLog.Warn($"Incoming settings sync from {fromDeviceId} ignored — no ISettingsSyncStore.");
            return true;
        }

        try
        {
            var payload = SettingsSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Category))
            {
                SyncDebugLog.Info($"Ignoring unreadable settings payload from {fromDeviceId}");
                return true;
            }

            if (!await _settingsStore.ShouldAcceptIncomingAsync(payload))
            {
                SyncDebugLog.Info(
                    $"Ignoring stale settings sync for {payload.Category} from {fromDeviceId} " +
                    $"(remoteTicks={payload.UpdatedTicks})");
                return true;
            }

            await _settingsStore.ApplyAsync(payload);
            Raise(OnSettingsChanged);
            SyncDebugLog.Info(
                $"Applied incoming settings sync for {payload.Category} from {fromDeviceId} " +
                $"(remoteTicks={payload.UpdatedTicks})");
            return true;
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming settings sync: {ex.Message}");
            return false;
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
                Raise(OnConversationsChanged);
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
                Raise(OnNotesChanged);
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
                Raise(OnBookmarksChanged);
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
                Raise(OnBookmarksChanged);
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
                Raise(OnInstalledAppsChanged);
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

            // Versioned protection merge: never demote a local lock from stale remote false.
            await ApplyIncomingConvoProtectionAsync(payload);

            Raise(OnSyncPayloadReceived, convoId, json, fromDeviceId);
            Raise(OnConversationsChanged);

            SyncDebugLog.Info($"Auto-saved incoming sync for convo {convoId} from {fromDeviceId} " +
                $"({msgs.Count} messages, title=\"{incomingTitle ?? ""}\", custom={incomingTitleIsCustom}, " +
                $"protected={payload.IsPasswordProtected?.ToString() ?? "omit"}, " +
                $"proticks={payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Failed to persist incoming sync payload: {ex.Message}");
        }
    }

    private async Task ApplyIncomingConvoProtectionAsync(ConvoSyncPayload payload)
    {
        var index = await _conversationStore.LoadIndexAsync();
        var local = index.FirstOrDefault(c => c.Id == payload.ConvoId);
        var localProtected = local?.IsPasswordProtected == true;
        var localTicks = local?.ProtectionChangedTicks ?? 0;

        if (!PasswordProtectionSync.TryResolve(
                payload.IsPasswordProtected,
                payload.ProtectionChangedTicks,
                localProtected,
                localTicks,
                out var applyProtected,
                out var applyTicks))
        {
            SyncDebugLog.Info(
                $"Convo {payload.ConvoId} protection unchanged " +
                $"(local={localProtected}/{localTicks}, remote={payload.IsPasswordProtected?.ToString() ?? "omit"}/{payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
            return;
        }

        await _conversationStore.SetPasswordProtectedAsync(payload.ConvoId, applyProtected, applyTicks);
        SyncDebugLog.Info(
            $"Convo {payload.ConvoId} protection applied: {localProtected}/{localTicks} -> {applyProtected}/{applyTicks} " +
            $"(remote={payload.IsPasswordProtected}/{payload.ProtectionChangedTicks?.ToString() ?? "omit"})");
    }

    public void OnDataChannelClose(string peerId)
    {
        SyncDebugLog.Info($"DataChannel closed for {peerId}");

        StartDetached(() => ExclusiveAsync(async () =>
        {
            // Queue progression is owned by AdvanceSyncQueueAsync / FailActiveSyncAsync.
            // Only treat a close as failure when a sync transfer is still marked active.
            if (_activeSyncByPeer.ContainsKey(peerId))
            {
                await FailActiveSyncAsync(peerId, "data channel closed before acknowledgement");
                return;
            }

            // After polite glare we answer as the passive peer (no active outbound). When the
            // remote finishes and closes, resume any re-queued outbound work.
            await ResumeQueueAfterPassiveChannelCloseAsync(peerId);
        }));
    }

    private async Task ResumeQueueAfterPassiveChannelCloseAsync(string peerId)
    {
        try
        {
            if (_activeSyncByPeer.ContainsKey(peerId))
                return;
            if (!_syncQueues.TryGetValue(peerId, out var q) || q.Count == 0)
            {
                _owedOutboundTurn.Remove(peerId);
                await FlushDeferredRemoteOfferAsync(peerId);
                return;
            }

            // Next glare: keep our offer so we can push local albums that the peer never requested.
            _owedOutboundTurn.Add(peerId);
            SyncDebugLog.Info($"Resuming outbound queue for {peerId} after passive channel close ({q.Count} pending, turn claimed)");
            await ProcessSyncQueueAsync(peerId);
        }
        catch (Exception ex)
        {
            SyncDebugLog.Info($"Resume after passive close failed for {peerId}: {ex.Message}");
        }
    }

    private void NotifyGalleryChangedDebounced()
    {
        try { _galleryChangedDebounceCts?.Cancel(); } catch { /* ignore */ }
        _galleryChangedDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _galleryChangedDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(GalleryChangedDebounce, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                    Raise(OnGalleryChanged);
            }
            catch (TaskCanceledException) { }
        });
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

        try { _gate.Dispose(); } catch { /* ignore */ }
    }
}
