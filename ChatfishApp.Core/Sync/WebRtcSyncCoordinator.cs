using System.Text.Json;
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
        IWebRtcTransportCallbacks? transportCallbacks = null)
    {
        _webrtc = webrtc;
        _conversationStore = conversationStore;
        _noteStore = noteStore;
        _prefs = prefs;
        _sendSignalingAsync = sendSignalingAsync;
        _isHubConnected = isHubConnected;
        _transportCallbacks = transportCallbacks ?? this;
    }

    public bool AutoSyncChatHistory { get; set; }
    public bool AutoSyncNotes { get; set; }
    public IReadOnlyCollection<string> SyncTargetDeviceIds { get; set; } = Array.Empty<string>();
    public Func<string, bool>? IsSelf { get; set; }
    public Func<bool>? IsAuthenticated { get; set; }
    public Func<Task>? EnsureConnectedAsync { get; set; }
    public Func<IReadOnlyList<SyncDeviceInfo>>? GetDevices { get; set; }

    public event Action? OnConversationsChanged;
    public event Action? OnNotesChanged;
    public event Action<string, string, string>? OnSyncPayloadReceived;
    public event Action<string, string>? OnSyncAckReceived;
    public event Action<string, string, string>? OnNoteSyncPayloadReceived;
    public event Action<string, string>? OnNoteSyncAckReceived;

    public Task HandleReceiveSignalingAsync(string fromDeviceId, string type, string payload)
    {
        if (type is "webrtc-offer" or "webrtc-offer-ai")
            Console.WriteLine($"[WebRTC] Received signaling '{type}' from {fromDeviceId}");

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
        public required bool IsNote { get; init; }
        public required string ItemId { get; init; }
        public required int ChunkCount { get; init; }
        public required string[] Parts { get; init; }
        public int PartsReceived { get; set; }
    }

    private sealed class SyncQueueItem
    {
        public required bool IsNote { get; init; }
        public required string ItemId { get; init; }
        public string? NoteTitle { get; init; }
        public required string DataJson { get; init; }
        public string? ContentFingerprint { get; init; }
        public bool IsDelete { get; init; }
        public long? DeletedAtTicks { get; init; }
        public bool IsManifestExchange { get; init; }
        public bool IncludeConvosInManifest { get; init; }
        public bool IncludeNotesInManifest { get; init; }
        public int RetryCount { get; set; }
    }

    private record SyncManifestOffer(
        List<SyncManifestEntry> Convos,
        List<SyncManifestEntry> Notes);

    private record SyncManifestResponse(
        List<string> NeededConvos,
        List<string> NeededNotes,
        int UpToDateConvos,
        int UpToDateNotes,
        List<DeleteSyncPayload>? SenderShouldDeleteConvos = null,
        List<DeleteSyncPayload>? SenderShouldDeleteNotes = null);

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
            Console.WriteLine($"[WebRtcSyncCoordinator] Cannot enqueue convo {convoId}: hub not connected.");
            return;
        }

        var titleInfo = await _conversationStore.GetMetaTitleInfoAsync(convoId);
        title ??= titleInfo.Title;
        titleIsCustom ??= titleInfo.TitleIsCustom;
        var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom);
        var item = new SyncQueueItem
        {
            IsNote = false,
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
            Console.WriteLine($"[WebRtcSyncCoordinator] Cannot enqueue note {noteId}: hub not connected.");
            return;
        }

        var dataJson = NoteSyncPayload.Serialize(noteId, title, entries);
        var item = new SyncQueueItem
        {
            IsNote = true,
            ItemId = noteId,
            NoteTitle = title,
            DataJson = dataJson,
            ContentFingerprint = SyncFingerprint.ForNote(noteId, title, entries)
        };
        await EnqueueSyncAsync(targetDeviceId, item);
    }

    /// <summary>
    /// Exchanges a lightweight manifest with each target, then only queues items the peer still needs.
    /// </summary>
    public async Task<int> StartDeltaSyncToDevicesAsync(
        IEnumerable<string> targetDeviceIds,
        bool includeConvos,
        bool includeNotes)
    {
        var targets = targetDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0 || !_isHubConnected())
            return 0;

        foreach (var targetId in targets)
            await EnqueueManifestExchangeAsync(targetId, includeConvos, includeNotes);

        return targets.Count;
    }

    public Task<int> SyncAllConversationsToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: true, includeNotes: false);

    public Task<int> SyncAllNotesToDevicesAsync(IEnumerable<string> targetDeviceIds) =>
        StartDeltaSyncToDevicesAsync(targetDeviceIds, includeConvos: false, includeNotes: true);

    private async Task EnqueueManifestExchangeAsync(string targetDeviceId, bool includeConvos, bool includeNotes)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || !_isHubConnected())
            return;

        if (!includeConvos && !includeNotes)
            return;

        var manifest = await BuildLocalManifestAsync(includeConvos, includeNotes);
        var item = new SyncQueueItem
        {
            IsManifestExchange = true,
            IsNote = false,
            ItemId = "__manifest__",
            DataJson = System.Text.Json.JsonSerializer.Serialize(manifest),
            IncludeConvosInManifest = includeConvos,
            IncludeNotesInManifest = includeNotes
        };

        if (IsManifestSyncPending(targetDeviceId))
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Manifest already pending for {targetDeviceId}, skipping duplicate");
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

    private async Task<SyncManifestOffer> BuildLocalManifestAsync(bool includeConvos, bool includeNotes)
    {
        var convos = includeConvos
            ? await _conversationStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true)
            : new List<SyncManifestEntry>();
        var notes = includeNotes
            ? await _noteStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true)
            : new List<SyncManifestEntry>();
        return new SyncManifestOffer(convos, notes);
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

    private static string GetAckItemKey(bool isNote, string itemId) =>
        isNote ? $"n:{itemId}" : $"c:{itemId}";

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

    private async Task<bool> IsItemAcknowledgedAsync(string peerId, bool isNote, string itemId, string? fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return false;

        var state = await LoadPeerAckStateAsync(peerId);
        var key = GetAckItemKey(isNote, itemId);
        return state.TryGetValue(key, out var ackFp)
               && string.Equals(ackFp, fingerprint, StringComparison.Ordinal);
    }

    private async Task RecordSuccessfulSyncAsync(string peerId, SyncQueueItem item)
    {
        if (item.IsManifestExchange || string.IsNullOrEmpty(item.ContentFingerprint))
            return;

        var state = await LoadPeerAckStateAsync(peerId);
        state[GetAckItemKey(item.IsNote, item.ItemId)] = item.ContentFingerprint;
        await SavePeerAckStateAsync(peerId, state);
    }

    private async Task ClearPeerAckForItemAsync(bool isNote, string itemId)
    {
        try
        {
            var json = await _prefs.GetStringAsync(SyncAckStateKey);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json);
            if (all == null || all.Count == 0)
                return;

            var key = GetAckItemKey(isNote, itemId);
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
            IsNote = false,
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
            IsNote = true,
            IsDelete = true,
            ItemId = noteId,
            DataJson = DeleteSyncPayload.Serialize(noteId, deletedAtUtc.Ticks),
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
            if (await IsItemAcknowledgedAsync(peerId, isNote: false, convoId, fingerprint))
            {
                Console.WriteLine($"[WebRtcSyncCoordinator] Skipping convo {convoId} for {peerId} (peer already has current version)");
                continue;
            }

            var item = new SyncQueueItem
            {
                IsNote = false,
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
            if (await IsItemAcknowledgedAsync(peerId, isNote: true, noteId, fingerprint))
            {
                Console.WriteLine($"[WebRtcSyncCoordinator] Skipping note {noteId} for {peerId} (peer already has current version)");
                continue;
            }

            var item = new SyncQueueItem
            {
                IsNote = true,
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

        Console.WriteLine(
            $"[WebRtcSyncCoordinator] Manifest result for {peerId}: " +
            $"{response.UpToDateConvos} convo(s) and {response.UpToDateNotes} note(s) up to date; " +
            $"queued {queued} item(s)");

        return queued;
    }

    private async Task EnqueueSyncAsync(string targetDeviceId, SyncQueueItem item, bool allowDuplicate = false)
    {
        if (!allowDuplicate && IsAlreadyQueuedOrActive(targetDeviceId, item))
        {
            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Skipping duplicate {(item.IsNote ? "note" : "convo")} " +
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
                ? $"{(item.IsNote ? "note" : "convo")} delete {item.ItemId}"
                : $"{(item.IsNote ? "note" : "convo")} {item.ItemId}";
        Console.WriteLine(
            $"[WebRtcSyncCoordinator] Enqueued {itemLabel} for {targetDeviceId} (queue depth: {queue.Count})");
        await ProcessSyncQueueAsync(targetDeviceId);
    }

    private async Task ProcessSyncQueueAsync(string targetDeviceId)
    {
        if (_activeSyncByPeer.TryGetValue(targetDeviceId, out var active))
        {
            var pending = _syncQueues.TryGetValue(targetDeviceId, out var q) ? q.Count : 0;
            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Sync queue for {targetDeviceId} waiting " +
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
                : item.IsNote ? "chatfish-note-sync" : "chatfish-sync";
            var label = item.IsManifestExchange
                ? "manifest"
                : item.IsDelete
                    ? $"{(item.IsNote ? "note" : "convo")} delete {item.ItemId}"
                    : $"{(item.IsNote ? "note" : "convo")} {item.ItemId}";
            Console.WriteLine($"[WebRtcSyncCoordinator] Starting WebRTC sync for {targetDeviceId}: {label}");
            await StartWebRtcDataChannelAsync(targetDeviceId, channelLabel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] WebRTC sync start failed: {ex.Message}");
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
                    Console.WriteLine($"[WebRtcSyncCoordinator] Sync timed out for peer {peerId} after {SyncItemTimeout.TotalSeconds}s");
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
        Console.WriteLine($"[WebRtcSyncCoordinator] Active sync failed for {peerId}: {reason}");
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
            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Re-queued {(failedItem.IsNote ? "note" : "convo")} {failedItem.ItemId} " +
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
            return $"{(item.IsNote ? "note" : "convo")} delete {item.ItemId}";
        return $"{(item.IsNote ? "note" : "convo")} {item.ItemId}";
    }

    private static bool ItemsMatchForDedup(SyncQueueItem a, SyncQueueItem b)
    {
        if (a.IsManifestExchange || b.IsManifestExchange)
            return a.IsManifestExchange && b.IsManifestExchange;

        return a.IsNote == b.IsNote
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
                await ClearPeerAckForItemAsync(isNote: false, convoId);

            var manifest = await _conversationStore.LoadManifestEntriesAsync();
            var entry = manifest.FirstOrDefault(c => c.Id == convoId);
            var fingerprint = entry?.ContentFingerprint;

            var messages = await _conversationStore.LoadConversationAsync(convoId);
            foreach (var targetId in GetOnlineSyncTargetIdsInternal())
            {
                if (!forceTitleSync
                    && await IsItemAcknowledgedAsync(targetId, isNote: false, convoId, fingerprint))
                {
                    Console.WriteLine($"[WebRtcSyncCoordinator] Skipping convo {convoId} for {targetId} (unchanged since last ack)");
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
                if (await IsItemAcknowledgedAsync(targetId, isNote: false, convoId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    Console.WriteLine($"[WebRtcSyncCoordinator] Skipping convo delete {convoId} for {targetId} (already acknowledged)");
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
                if (await IsItemAcknowledgedAsync(targetId, isNote: true, noteId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    Console.WriteLine($"[WebRtcSyncCoordinator] Skipping note delete {noteId} for {targetId} (already acknowledged)");
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
                if (await IsItemAcknowledgedAsync(targetId, isNote: true, noteId, fingerprint))
                {
                    Console.WriteLine($"[WebRtcSyncCoordinator] Skipping note {noteId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueNoteSyncAsync(targetId, noteId, title, entries);
            }
        });
    }

    private IEnumerable<string> GetOnlineSyncTargetIdsInternal() =>
        (GetDevices?.Invoke() ?? Array.Empty<SyncDeviceInfo>())
            .Where(d => d.IsOnline
                        && IsSelf?.Invoke(d.DeviceId) == false
                        && SyncTargetDeviceIds.Any(id => string.Equals(id, d.DeviceId, StringComparison.OrdinalIgnoreCase)))
            .Select(d => d.DeviceId);

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

    private async Task<bool> HasPendingOutboundSyncAsync(string peerId, bool includeConvos, bool includeNotes)
    {
        var ackState = await LoadPeerAckStateAsync(peerId);

        if (includeConvos)
        {
            var convos = await _conversationStore.LoadManifestEntriesAsync();
            foreach (var convo in convos)
            {
                var key = GetAckItemKey(isNote: false, convo.Id);
                var expectedAck = convo.IsDeleted && convo.DeletedAtTicks.HasValue
                    ? DeleteSyncPayload.AckValue(convo.DeletedAtTicks.Value)
                    : convo.ContentFingerprint;

                if (string.IsNullOrEmpty(expectedAck))
                {
                    if (!ackState.ContainsKey(key))
                        return true;
                    continue;
                }

                if (!ackState.TryGetValue(key, out var ackFp)
                    || !string.Equals(ackFp, expectedAck, StringComparison.Ordinal))
                    return true;
            }
        }

        if (includeNotes)
        {
            var notes = await _noteStore.LoadManifestEntriesAsync();
            foreach (var note in notes)
            {
                var key = GetAckItemKey(isNote: true, note.Id);
                var expectedAck = note.IsDeleted && note.DeletedAtTicks.HasValue
                    ? DeleteSyncPayload.AckValue(note.DeletedAtTicks.Value)
                    : note.ContentFingerprint;

                if (string.IsNullOrEmpty(expectedAck))
                {
                    if (!ackState.ContainsKey(key))
                        return true;
                    continue;
                }

                if (!ackState.TryGetValue(key, out var ackFp)
                    || !string.Equals(ackFp, expectedAck, StringComparison.Ordinal))
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

        if (!AutoSyncChatHistory && !AutoSyncNotes)
            return;

        try
        {
            if (!await HasPendingOutboundSyncAsync(deviceId, AutoSyncChatHistory, AutoSyncNotes))
            {
                Console.WriteLine($"[WebRtcSyncCoordinator] Skipping auto-sync for {deviceId} (all items already acknowledged)");
                return;
            }

            var lastVerified = await GetLastManifestVerifiedUtcAsync(deviceId);
            if (lastVerified.HasValue && DateTime.UtcNow - lastVerified.Value < ManifestRecheckCooldown)
            {
                var minutesAgo = (int)(DateTime.UtcNow - lastVerified.Value).TotalMinutes;
                Console.WriteLine(
                    $"[WebRtcSyncCoordinator] Skipping auto-sync for {deviceId} " +
                    $"(manifest verified {minutesAgo}m ago)");
                return;
            }

            await EnqueueManifestExchangeAsync(deviceId, AutoSyncChatHistory, AutoSyncNotes);
            Console.WriteLine($"[WebRtcSyncCoordinator] Auto-sync manifest queued for {deviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Auto-sync failed for {deviceId}: {ex.Message}");
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
                Console.WriteLine($"[WebRtcSyncCoordinator] Reusing open channel for {peerId}: {reuseLabel}");

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
                : nextItem.IsNote ? "chatfish-note-sync" : "chatfish-sync";
            var label = nextItem.IsManifestExchange
                ? "manifest"
                : $"{(nextItem.IsNote ? "note" : "convo")} {nextItem.ItemId}";
            Console.WriteLine($"[WebRtcSyncCoordinator] Starting WebRTC sync for {peerId}: {label}");
            await StartWebRtcDataChannelAsync(peerId, channelLabel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] WebRTC sync start failed: {ex.Message}");
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

    private async Task<bool> SendSyncPayloadAsync(string peerId, bool isNote, string itemId, string contentJson)
    {
        var maxMessageSize = await _webrtc.GetMaxMessageSizeAsync(peerId);
        var chunkPayloadBytes = Math.Max(4096, (int)(maxMessageSize * 0.7) - 256);
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(contentJson);

        if (contentBytes.Length <= chunkPayloadBytes)
        {
            var msg = isNote
                ? new DataChannelMessage("note-sync-data", content: contentJson)
                : new DataChannelMessage("sync-data", itemId, contentJson);
            return await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(msg));
        }

        var chunkCount = (contentBytes.Length + chunkPayloadBytes - 1) / chunkPayloadBytes;
        var chunkType = isNote ? "note-sync-chunk" : "sync-chunk";
        Console.WriteLine(
            $"[WebRtcSyncCoordinator] Chunking sync payload for {itemId}: {contentBytes.Length} bytes -> {chunkCount} chunk(s)");

        for (var i = 0; i < chunkCount; i++)
        {
            var offset = i * chunkPayloadBytes;
            var length = Math.Min(chunkPayloadBytes, contentBytes.Length - offset);
            var slice = System.Text.Encoding.UTF8.GetString(contentBytes, offset, length);

            var chunkMsg = new DataChannelMessage(
                chunkType,
                convoId: isNote ? null : itemId,
                noteId: isNote ? itemId : null,
                chunkIndex: i,
                chunkCount: chunkCount,
                chunkData: slice);

            var sent = await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(chunkMsg));
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
        bool isNote,
        int? chunkIndex,
        int? chunkCount,
        string? chunkData,
        out string? completeJson)
    {
        completeJson = null;
        if (chunkIndex is null or < 0 || chunkCount is null or < 1 || string.IsNullOrEmpty(chunkData))
            return false;

        var key = $"{peerId}:{(isNote ? "note" : "convo")}:{itemId}";
        if (!_chunkAssemblies.TryGetValue(key, out var assembly))
        {
            assembly = new ChunkAssembly
            {
                IsNote = isNote,
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
        Console.WriteLine($"[WebRTC] Sending offer to {targetDeviceId}");
        await _sendSignalingAsync(targetDeviceId, "webrtc-offer", offerJson ?? "");
    }

    private async Task HandleWebRtcOffer(string fromDeviceId, string offerJson)
    {
                Console.WriteLine($"[WebRTC] Received offer from {fromDeviceId}");

        try
        {
            await _webrtc.CreatePeerConnectionAsync(fromDeviceId, _transportCallbacks);
            await _webrtc.SetRemoteDescriptionAsync(fromDeviceId, offerJson);

            var answerJson = await _webrtc.CreateAnswerAsync(fromDeviceId);
            await _sendSignalingAsync(fromDeviceId, "webrtc-answer", answerJson ?? "");
            Console.WriteLine($"[WebRTC] Sent answer to {fromDeviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Handle offer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswer(string fromDeviceId, string answerJson)
    {
        try
        {
            var applied = await _webrtc.SetRemoteDescriptionAsync(fromDeviceId, answerJson);
            if (applied)
                Console.WriteLine($"[WebRTC] Applied answer from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Handle answer failed: {ex.Message}");
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
        Console.WriteLine($"[WebRtcSyncCoordinator] DataChannel open for peer {peerId}");

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
                    Console.WriteLine($"[WebRtcSyncCoordinator] Sent sync-manifest-offer to {peerId}");
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
                var deleteType = item.IsNote ? "note-delete" : "convo-delete";
                Console.WriteLine(
                    $"[WebRtcSyncCoordinator] Sending {(item.IsNote ? "note" : "convo")} delete for {item.ItemId} to {peerId}");
                var msg = new DataChannelMessage(deleteType, content: item.DataJson);
                var sent = await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(msg));
                if (!sent)
                {
                    await FailActiveSyncAsync(peerId, "data channel not ready for delete");
                    return;
                }

                Console.WriteLine($"[WebRtcSyncCoordinator] Sent {deleteType} to {peerId} for {item.ItemId}");
                return;
            }

            var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(item.DataJson);
            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Preparing {(item.IsNote ? "note" : "convo")} sync payload " +
                $"for {item.ItemId} ({payloadBytes} bytes)");

            var payloadSent = await SendSyncPayloadAsync(peerId, item.IsNote, item.ItemId, item.DataJson);
            if (!payloadSent)
            {
                Console.WriteLine($"[WebRtcSyncCoordinator] webrtcSendData failed (channel not ready) for {peerId}");
                await FailActiveSyncAsync(peerId, "data channel not ready for send");
                return;
            }

            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Sent {(item.IsNote ? "note-sync-data" : "sync-data")} " +
                $"over DataChannel to {peerId} for {item.ItemId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] DataChannel send failed for {peerId}: {ex.Message}");
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
                    if (!TryAddChunk(peerId, msg.convoId, isNote: false, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingSyncPayload(msg.convoId, contentJson, peerId);

                var ack = new DataChannelMessage("sync-ack", msg.convoId);
                await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(ack));
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
                        || !TryAddChunk(peerId, noteId, isNote: true, msg.chunkIndex, msg.chunkCount, msg.chunkData, out contentJson))
                        return;
                }

                if (contentJson == null)
                    return;

                await HandleIncomingNoteSyncPayload(contentJson, peerId);

                var payload = NoteSyncPayload.Deserialize(contentJson);
                if (payload?.NoteId != null)
                {
                    var ack = new DataChannelMessage("note-sync-ack", noteId: payload.NoteId);
                    await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "note-sync-ack" && msg.noteId != null)
            {
                HandleNoteSyncAck(msg.noteId, peerId);
            }
            else if (msg.type == "convo-delete" && msg.content != null)
            {
                await HandleIncomingConvoDeleteAsync(msg.content, peerId);
                var deletePayload = DeleteSyncPayload.Deserialize(msg.content);
                if (deletePayload != null)
                {
                    var ack = new DataChannelMessage("convo-delete-ack", convoId: deletePayload.Id);
                    await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(ack));
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
                    await _webrtc.SendDataAsync( peerId, SerializeDataChannelMessage(ack));
                }
            }
            else if (msg.type == "note-delete-ack" && msg.noteId != null)
            {
                HandleNoteDeleteAck(msg.noteId, peerId);
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
            Console.WriteLine($"[WebRtcSyncCoordinator] Failed to parse DataChannel message: {ex.Message}");
        }
    }

    private async Task HandleManifestOfferAsync(string peerId, string offerJson)
    {
        var offer = System.Text.Json.JsonSerializer.Deserialize<SyncManifestOffer>(offerJson);
        if (offer == null)
            return;

        var localConvos = await _conversationStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);
        var localNotes = await _noteStore.LoadManifestEntriesAsync(backfillMissingFingerprints: true);

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

        if (appliedConvoDeletes > 0)
            OnConversationsChanged?.Invoke();
        if (appliedNoteDeletes > 0)
            OnNotesChanged?.Invoke();

        var response = new SyncManifestResponse(
            neededConvos,
            neededNotes,
            upToDateConvos,
            upToDateNotes,
            senderShouldDeleteConvos,
            senderShouldDeleteNotes);
        var responseJson = System.Text.Json.JsonSerializer.Serialize(response);
        await _webrtc.SendDataAsync(
            peerId,
            SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-response", content: responseJson)));

        Console.WriteLine(
            $"[WebRtcSyncCoordinator] Manifest offer from {peerId}: " +
            $"{upToDateConvos}/{offer.Convos.Count} convos and {upToDateNotes}/{offer.Notes.Count} notes up to date" +
            (appliedConvoDeletes + appliedNoteDeletes > 0
                ? $" (applied {appliedConvoDeletes} convo and {appliedNoteDeletes} note delete(s))"
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

        if (appliedConvoDeletes > 0)
            OnConversationsChanged?.Invoke();
        if (appliedNoteDeletes > 0)
            OnNotesChanged?.Invoke();

        var queued = await QueueNeededItemsFromManifestAsync(peerId, response);
        if (response.NeededConvos.Count == 0 && response.NeededNotes.Count == 0)
            await RecordManifestVerifiedAsync(peerId);
        if (queued == 0)
            Console.WriteLine($"[WebRtcSyncCoordinator] Delta sync complete for {peerId} - peer is up to date");
        await AdvanceSyncQueueAsync(peerId);
    }

    private async Task HandleSyncAckAsync(string convoId, string peerId)
    {
        Console.WriteLine($"[WebRtcSyncCoordinator] Received sync-ack for convo {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnSyncAckReceived?.Invoke(convoId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleSyncAck(string convoId, string peerId) =>
        _ = HandleSyncAckAsync(convoId, peerId);

    private async Task HandleNoteSyncAckAsync(string noteId, string peerId)
    {
        Console.WriteLine($"[WebRtcSyncCoordinator] Received note-sync-ack for note {noteId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnNoteSyncAckReceived?.Invoke(noteId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleNoteSyncAck(string noteId, string peerId) =>
        _ = HandleNoteSyncAckAsync(noteId, peerId);

    private async Task HandleConvoDeleteAckAsync(string convoId, string peerId)
    {
        Console.WriteLine($"[WebRtcSyncCoordinator] Received convo-delete-ack for {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleConvoDeleteAck(string convoId, string peerId) =>
        _ = HandleConvoDeleteAckAsync(convoId, peerId);

    private async Task HandleNoteDeleteAckAsync(string noteId, string peerId)
    {
        Console.WriteLine($"[WebRtcSyncCoordinator] Received note-delete-ack for {noteId} from peer {peerId}");
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
                Console.WriteLine($"[WebRtcSyncCoordinator] Ignoring stale note sync for {payload.NoteId} (local delete is newer)");
                return;
            }

            var localTitle = await _noteStore.GetMetaTitleAsync(payload.NoteId);
            var title = ChatMessageHelper.ResolveIncomingNoteTitle(payload.Title, localTitle);

            await _noteStore.SaveNoteAsync(payload.NoteId, entries);
            await _noteStore.UpdateIndexAfterSaveAsync(payload.NoteId, title, entries);

            OnNoteSyncPayloadReceived?.Invoke(payload.NoteId, json, fromDeviceId);
            OnNotesChanged?.Invoke();

            Console.WriteLine($"[WebRtcSyncCoordinator] Auto-saved incoming note sync for {payload.NoteId} from {fromDeviceId} ({payload.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Failed to persist incoming note sync payload: {ex.Message}");
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
                Console.WriteLine($"[WebRtcSyncCoordinator] Applied remote convo delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Failed to apply convo delete: {ex.Message}");
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
                Console.WriteLine($"[WebRtcSyncCoordinator] Applied remote note delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Failed to apply note delete: {ex.Message}");
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
                Console.WriteLine($"[WebRtcSyncCoordinator] Ignoring invalid convo sync payload for {convoId}");
                return;
            }

            convoId = payload.ConvoId;
            msgs = ChatMessageHelper.NormalizeAll(payload.Messages);
            incomingTitle = payload.Title;
            incomingTitleIsCustom = payload.TitleIsCustom == true;

            if (!await _conversationStore.ShouldAcceptIncomingContentAsync(convoId, msgs))
            {
                Console.WriteLine($"[WebRtcSyncCoordinator] Ignoring stale convo sync for {convoId} (local delete is newer)");
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

            Console.WriteLine(
                $"[WebRtcSyncCoordinator] Auto-saved incoming sync for convo {convoId} from {fromDeviceId} " +
                $"({msgs.Count} messages, title=\"{incomingTitle ?? ""}\", custom={incomingTitleIsCustom})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebRtcSyncCoordinator] Failed to persist incoming sync payload: {ex.Message}");
        }
    }

    public void OnDataChannelClose(string peerId)
    {
        Console.WriteLine($"[WebRtcSyncCoordinator] DataChannel closed for {peerId}");

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
        string? chunkData = null);

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
