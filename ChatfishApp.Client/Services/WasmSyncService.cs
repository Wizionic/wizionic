using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using static ChatfishApp.Client.Services.WasmConversationStore;

namespace ChatfishApp.Client.Services;

/// <summary>
/// Client-side service that maintains a SignalR connection to the server's SyncHub.
/// 
/// Responsibilities for Phase 1:
/// - Generate/persist a stable per-browser DeviceId (localStorage).
/// - Choose and persist a friendly DeviceName (user can rename later from the Sync page).
/// - When the user is authenticated (email login), connect to /sync-hub and register.
/// - Maintain a live list of other devices for the same user, with IsOnline + LastActive.
/// - Raise OnChanged so pages (Sync.razor) can re-render.
/// 
/// The same connection will later carry WebRTC signaling messages (Phase 2) without
/// ever sending actual chat history blobs through the server.
/// 
/// Only users who have logged in with email (WasmAuthService.IsAuthenticated) get
/// a meaningful device list. Guest sessions do not participate in cross-device sync.
/// </summary>
public class WasmSyncService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly HttpClient _http; // only used to resolve the base address for the hub
    private readonly WasmAuthService _auth;
    private readonly WasmConversationStore _conversationStore;
    private readonly WasmNoteStore _noteStore;
    private readonly WasmAiProviderService _aiProvider;
    private readonly WasmKeyStore _keyStore;
    private readonly WasmChatCompletionService _chatCompletion;

    private HubConnection? _hub;
    private bool _initialized;

    private const string DeviceIdKey = "chatfish-device-id";
    private const string DeviceNameKey = "chatfish-device-name";
    private const string AiServerDeviceIdKey = "chatfish-ai-server-device-id";
    private const string SyncTargetDevicesKey = "chatfish-sync-target-devices";
    private const string AutoSyncChatKey = "chatfish-auto-sync-chat";
    private const string AutoSyncNotesKey = "chatfish-auto-sync-notes";
    private const string AiProxyDataChannelLabel = "chatfish-ai-proxy";

    public string? MyDeviceId { get; private set; }
    public string MyDeviceName { get; private set; } = "This browser";

    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = Array.Empty<DeviceInfo>();

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>Device ID of the peer browser that handles AI completions for this client.</summary>
    public string? AiServerDeviceId { get; private set; }

    /// <summary>Models available on the selected AI server device (populated over WebRTC).</summary>
    public IReadOnlyList<WasmAiProviderService.ModelInfo> RemoteModels { get; private set; } = Array.Empty<WasmAiProviderService.ModelInfo>();

    public bool IsAiProxyConnected { get; private set; }

    public string? AiProxyError { get; private set; }

    public bool AutoSyncChatHistory { get; private set; }
    public bool AutoSyncNotes { get; private set; }
    public IReadOnlyCollection<string> SyncTargetDeviceIds => _syncTargetDeviceIds;

    public event Action? OnChanged;

    /// <summary>
    /// Fired whenever a conversation is added or updated via incoming sync (background or foreground).
    /// Pages like Chat can subscribe to this to refresh their conversation list / sidebar.
    /// </summary>
    public event Action? OnConversationsChanged;

    /// <summary>
    /// Fired when a note is added or updated via incoming sync (background or foreground).
    /// </summary>
    public event Action? OnNotesChanged;

    public WasmSyncService(
        IJSRuntime js,
        HttpClient http,
        WasmAuthService auth,
        WasmConversationStore conversationStore,
        WasmNoteStore noteStore,
        WasmAiProviderService aiProvider,
        WasmKeyStore keyStore,
        WasmChatCompletionService chatCompletion)
    {
        _js = js;
        _http = http;
        _auth = auth;
        _conversationStore = conversationStore;
        _noteStore = noteStore;
        _aiProvider = aiProvider;
        _keyStore = keyStore;
        _chatCompletion = chatCompletion;

        _auth.OnChanged += OnAuthChanged;
    }

    private async void OnAuthChanged()
    {
        // If the user just logged in (or out), react.
        if (_auth.IsAuthenticated)
        {
            try { await EnsureConnectedAndRegisteredAsync(); }
            catch { /* ignore transient */ }
        }
        else
        {
            await StopAsync();
            Devices = Array.Empty<DeviceInfo>();
            OnChanged?.Invoke();
        }
    }

    private async Task StopAsync()
    {
        if (_hub != null)
        {
            try
            {
                await _hub.StopAsync();
            }
            catch { /* best effort */ }

            try
            {
                await _hub.DisposeAsync();
            }
            catch { /* best effort */ }

            _hub = null;
        }

        _devicesSnapshotInitialized = false;
        IsConnectedChanged();
    }

    /// <summary>
    /// Must be called early (e.g. from the Sync page OnInitializedAsync).
    /// Loads or creates the local device identity and prepares (but does not yet connect) the hub.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        // Load or create stable device identity (persisted in this browser profile).
        MyDeviceId = await _js.InvokeAsync<string?>("localStorage.getItem", DeviceIdKey);
        if (string.IsNullOrWhiteSpace(MyDeviceId))
        {
            MyDeviceId = Guid.NewGuid().ToString("N");
            await _js.InvokeVoidAsync("localStorage.setItem", DeviceIdKey, MyDeviceId);
        }

        var savedName = await _js.InvokeAsync<string?>("localStorage.getItem", DeviceNameKey);
        if (!string.IsNullOrWhiteSpace(savedName))
        {
            MyDeviceName = savedName;
        }
        else
        {
            // First time: try to give a slightly nicer default using navigator info.
            try
            {
                var ua = await _js.InvokeAsync<string>("eval", "navigator.userAgent || ''");
                MyDeviceName = DeriveFriendlyName(ua);
                await _js.InvokeVoidAsync("localStorage.setItem", DeviceNameKey, MyDeviceName);
            }
            catch
            {
                MyDeviceName = "This browser";
            }
        }

        // Restore persisted AI server preference (if any).
        AiServerDeviceId = await _js.InvokeAsync<string?>("localStorage.getItem", AiServerDeviceIdKey);
        if (string.IsNullOrWhiteSpace(AiServerDeviceId))
            AiServerDeviceId = null;

        await LoadSyncPreferencesAsync();

        OnChanged?.Invoke();
    }

    private async Task LoadSyncPreferencesAsync()
    {
        try
        {
            _syncTargetDeviceIds.Clear();
            var targetsJson = await _js.InvokeAsync<string?>("localStorage.getItem", SyncTargetDevicesKey);
            if (!string.IsNullOrWhiteSpace(targetsJson))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<List<string>>(targetsJson);
                if (ids != null)
                {
                    foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)))
                        _syncTargetDeviceIds.Add(id);
                }
            }

            AutoSyncChatHistory = string.Equals(
                await _js.InvokeAsync<string?>("localStorage.getItem", AutoSyncChatKey),
                "true",
                StringComparison.Ordinal);
            AutoSyncNotes = string.Equals(
                await _js.InvokeAsync<string?>("localStorage.getItem", AutoSyncNotesKey),
                "true",
                StringComparison.Ordinal);
        }
        catch
        {
            // Ignore preference load errors.
        }
    }

    public async Task SetSyncTargetDevicesAsync(IEnumerable<string> deviceIds)
    {
        _syncTargetDeviceIds.Clear();
        foreach (var id in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)))
            _syncTargetDeviceIds.Add(id);

        await _js.InvokeVoidAsync(
            "localStorage.setItem",
            SyncTargetDevicesKey,
            System.Text.Json.JsonSerializer.Serialize(_syncTargetDeviceIds.ToList()));
        OnChanged?.Invoke();
    }

    public async Task SetAutoSyncChatHistoryAsync(bool enabled)
    {
        AutoSyncChatHistory = enabled;
        await _js.InvokeVoidAsync("localStorage.setItem", AutoSyncChatKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
    }

    public async Task SetAutoSyncNotesAsync(bool enabled)
    {
        AutoSyncNotes = enabled;
        await _js.InvokeVoidAsync("localStorage.setItem", AutoSyncNotesKey, enabled ? "true" : "false");
        OnChanged?.Invoke();
    }

    private static string DeriveFriendlyName(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return "This browser";

        string os = "Device";
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) os = "Windows";
        else if (ua.Contains("Mac", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)) os = "Mac/iOS";
        else if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) os = "Android";
        else if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) os = "Linux";

        string browser = "Browser";
        if (ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) browser = "Edge";
        else if (ua.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Edg/", StringComparison.OrdinalIgnoreCase)) browser = "Chrome";
        else if (ua.Contains("Firefox/", StringComparison.OrdinalIgnoreCase)) browser = "Firefox";
        else if (ua.Contains("Safari/", StringComparison.OrdinalIgnoreCase) && !ua.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) browser = "Safari";

        return $"{browser} • {os}";
    }

    /// <summary>
    /// Connects (if not already) and registers this device with the server.
    /// Safe to call multiple times; it is idempotent for an already-registered session.
    /// </summary>
    public async Task EnsureConnectedAndRegisteredAsync()
    {
        await InitializeAsync();

        if (!_auth.IsAuthenticated || string.IsNullOrEmpty(_auth.Email))
        {
            // Guests do not get a live device list.
            return;
        }

        if (_hub == null)
        {
            // Build the connection. We append deviceId as a query string so the hub can see it early if desired.
            var baseUri = _http.BaseAddress?.ToString().TrimEnd('/') ?? "";
            var hubUrl = $"{baseUri}/sync-hub?deviceId={Uri.EscapeDataString(MyDeviceId ?? "")}";

            _hub = new HubConnectionBuilder()
                .WithUrl(hubUrl, options =>
                {
                    // Cookies are sent automatically for same-origin; no extra config needed.
                    // If we ever switch to JWT we would add options.AccessTokenProvider here.
                })
                .WithAutomaticReconnect()
                .Build();

            _hub.On<IReadOnlyList<DeviceInfo>>("DevicesUpdated", list =>
            {
                var prevOnline = Devices
                    .Where(d => d.IsOnline)
                    .Select(d => d.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                Devices = list ?? Array.Empty<DeviceInfo>();

                if (!_devicesSnapshotInitialized)
                {
                    _devicesSnapshotInitialized = true;
                    OnChanged?.Invoke();
                    return;
                }

                foreach (var d in Devices)
                {
                    if (d.IsOnline && !IsSelf(d.DeviceId) && !prevOnline.Contains(d.DeviceId))
                        ScheduleMaybeAutoSyncPeer(d.DeviceId);
                }

                OnChanged?.Invoke();
            });

            WireSyncHandlers();

            _hub.Closed += async (ex) =>
            {
                IsConnectedChanged();
                // AutomaticReconnect will try to come back; when it does we will re-register in Reconnected.
            };

            _hub.Reconnected += async (connectionId) =>
            {
                // Re-register so the server knows this connection belongs to our device.
                await SafeRegisterAsync();
                IsConnectedChanged();
            };

            _hub.Reconnecting += _ =>
            {
                IsConnectedChanged();
                return Task.CompletedTask;
            };
        }

        if (_hub.State == HubConnectionState.Disconnected)
        {
            try
            {
                await _hub.StartAsync();
                IsConnectedChanged();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmSyncService] Hub connect failed: {ex.Message}");
                IsConnectedChanged();
                return;
            }
        }

        await SafeRegisterAsync();
        await PublishAiCapabilitiesAsync();
    }

    /// <summary>
    /// Reports how many AI models this browser can serve to peers (for the device list UI).
    /// </summary>
    public async Task PublishAiCapabilitiesAsync()
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(MyDeviceId))
            return;

        try
        {
            await _keyStore.LoadAsync(_js);
            await _aiProvider.RefreshProxiedProvidersAsync();
            var count = _aiProvider.GetAvailableModels().Count;
            await _hub.InvokeAsync("UpdateAiCapabilities", MyDeviceId, count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] UpdateAiCapabilities failed: {ex.Message}");
        }
    }

    private async Task SafeRegisterAsync()
    {
        if (_hub == null || _hub.State != HubConnectionState.Connected) return;

        try
        {
            await _hub.InvokeAsync("RegisterDevice", MyDeviceId ?? "", MyDeviceName);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] RegisterDevice failed: {ex.Message}");
        }
    }

    private void IsConnectedChanged()
    {
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Rename this device (persisted locally and sent to server).
    /// </summary>
    public async Task SetDeviceNameAsync(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;

        MyDeviceName = newName.Trim();
        await _js.InvokeVoidAsync("localStorage.setItem", DeviceNameKey, MyDeviceName);

        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
        {
            try
            {
                await _hub.InvokeAsync("UpdateDeviceName", MyDeviceId, MyDeviceName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WasmSyncService] UpdateDeviceName RPC failed: {ex.Message}");
            }
        }

        OnChanged?.Invoke();
    }

    /// <summary>
    /// Ask the server for a fresh snapshot (useful after reconnect or manual refresh).
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_hub?.State == HubConnectionState.Connected && !string.IsNullOrEmpty(MyDeviceId))
        {
            await SafeRegisterAsync();
        }
        else if (_auth.IsAuthenticated)
        {
            await EnsureConnectedAndRegisteredAsync();
        }
    }

    /// <summary>
    /// Legacy relay path (kept for reference / fallback). Real Phase 2 data now uses WebRTC DataChannel
    /// via StartWebRtcSyncAsync (signaling only goes through the hub).
    /// </summary>
    public async Task SendSyncPayloadAsync(string targetDeviceId, string convoId, List<ChatMessage> messages)
    {
        if (_hub?.State != HubConnectionState.Connected || string.IsNullOrEmpty(targetDeviceId))
            return;

        try
        {
            var (title, titleIsCustom) = await _conversationStore.GetMetaTitleInfoAsync(_js, convoId);
            var json = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom);
            await _hub.InvokeAsync("SendSyncPayload", targetDeviceId, convoId, json, MyDeviceId ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] SendSyncPayload (relay) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Raised when this device receives a sync payload from a peer (via DataChannel or old relay).
    /// Handlers should deserialize and persist via the conversation store.
    /// </summary>
    public event Action<string, string, string>? OnSyncPayloadReceived; // convoId, encryptedJson, fromDeviceId

    private readonly HashSet<string> _syncTargetDeviceIds = new(StringComparer.OrdinalIgnoreCase);
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
    private bool _devicesSnapshotInitialized;

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
        List<WasmConversationStore.SyncManifestEntry> Convos,
        List<WasmNoteStore.SyncManifestEntry> Notes);

    private record SyncManifestResponse(
        List<string> NeededConvos,
        List<string> NeededNotes,
        int UpToDateConvos,
        int UpToDateNotes,
        List<DeleteSyncPayload>? SenderShouldDeleteConvos = null,
        List<DeleteSyncPayload>? SenderShouldDeleteNotes = null);

    private const string SyncAckStateKey = "chatfish-sync-ack-state";
    private const string SyncManifestVerifiedKey = "chatfish-sync-manifest-verified";

    // AI proxy: persistent WebRTC channel to a peer that runs local AI.
    private readonly Dictionary<string, TaskCompletionSource<ChatResponsePayload>> _pendingChatRequests = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _modelsListTcs;
    private bool _aiProxyConnecting;
    private CancellationTokenSource? _aiProxyReconnectCts;

    private void WireSyncHandlers()
    {
        if (_hub == null) return;

        _hub.On<string, string, string>("SyncPayloadReceived", async (convoId, json, fromDeviceId) =>
        {
            await HandleIncomingSyncPayload(convoId, json, fromDeviceId);
        });

        _hub.On<object>("SyncPayloadSent", (data) =>
        {
            Console.WriteLine("[WasmSyncService] Sync payload acknowledged as sent to peer.");
        });

        // WebRTC signaling (offer/answer/ice) routed through the hub
        _hub.On<string, string, string>("ReceiveSignaling", async (fromDeviceId, type, payload) =>
        {
            if (_hub == null) return;

            if (type is "webrtc-offer" or "webrtc-offer-ai")
                Console.WriteLine($"[WebRTC] Received signaling '{type}' from {fromDeviceId}");

            if (type == "webrtc-offer")
                await HandleWebRtcOffer(fromDeviceId, payload);
            else if (type == "webrtc-answer")
                await HandleWebRtcAnswer(fromDeviceId, payload);
            else if (type == "webrtc-offer-ai")
                await HandleWebRtcOfferAi(fromDeviceId, payload);
            else if (type == "webrtc-answer-ai")
                await HandleWebRtcAnswerAi(fromDeviceId, payload);
            else if (type == "webrtc-ice")
            {
                if (TryUnwrapIcePayload(payload, out var senderPeerKey, out var iceJson))
                {
                    // AI proxy uses asymmetric local keys (client: ai:serverId, server: ai:clientId).
                    // Always map incoming ICE to our local peer key for this session.
                    var localPeerKey = IsAiProxyPeerKey(senderPeerKey)
                        ? GetAiProxyPeerKey(fromDeviceId)
                        : fromDeviceId;
                    await _js.InvokeVoidAsync("webrtcAddIceCandidate", localPeerKey, iceJson);
                }
                else
                {
                    await _js.InvokeVoidAsync("webrtcAddIceCandidate", fromDeviceId, payload);
                }
            }
        });
    }

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

        if (_hub?.State != HubConnectionState.Connected)
        {
            Console.WriteLine($"[WasmSyncService] Cannot enqueue convo {convoId}: hub not connected.");
            return;
        }

        var titleInfo = await _conversationStore.GetMetaTitleInfoAsync(_js, convoId);
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

        if (_hub?.State != HubConnectionState.Connected)
        {
            Console.WriteLine($"[WasmSyncService] Cannot enqueue note {noteId}: hub not connected.");
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
        if (targets.Count == 0 || _hub?.State != HubConnectionState.Connected)
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
        if (string.IsNullOrEmpty(targetDeviceId) || _hub?.State != HubConnectionState.Connected)
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
            Console.WriteLine($"[WasmSyncService] Manifest already pending for {targetDeviceId}, skipping duplicate");
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
            ? await _conversationStore.LoadManifestEntriesAsync(_js, backfillMissingFingerprints: true)
            : new List<WasmConversationStore.SyncManifestEntry>();
        var notes = includeNotes
            ? await _noteStore.LoadManifestEntriesAsync(_js, backfillMissingFingerprints: true)
            : new List<WasmNoteStore.SyncManifestEntry>();
        return new SyncManifestOffer(convos, notes);
    }

    private static bool ManifestEntryNeedsSync(
        WasmConversationStore.SyncManifestEntry remote,
        WasmConversationStore.SyncManifestEntry? local)
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

    private static bool ManifestEntryNeedsSync(
        WasmNoteStore.SyncManifestEntry remote,
        WasmNoteStore.SyncManifestEntry? local)
    {
        if (remote.IsDeleted)
            return false;

        if (local == null)
            return true;

        if (local.IsDeleted)
            return remote.LastUpdatedTicks > local.DeletedAtTicks!.Value;

        if (!string.IsNullOrEmpty(remote.ContentFingerprint) && !string.IsNullOrEmpty(local.ContentFingerprint))
            return !string.Equals(remote.ContentFingerprint, local.ContentFingerprint, StringComparison.Ordinal);

        return remote.LastUpdatedTicks != local.LastUpdatedTicks;
    }

    private static bool LocalDeleteShouldWinOverRemote(
        WasmConversationStore.SyncManifestEntry remote,
        WasmConversationStore.SyncManifestEntry local) =>
        !remote.IsDeleted
        && local.IsDeleted
        && local.DeletedAtTicks!.Value > remote.LastUpdatedTicks;

    private static bool LocalDeleteShouldWinOverRemote(
        WasmNoteStore.SyncManifestEntry remote,
        WasmNoteStore.SyncManifestEntry local) =>
        !remote.IsDeleted
        && local.IsDeleted
        && local.DeletedAtTicks!.Value > remote.LastUpdatedTicks;

    private static string GetAckItemKey(bool isNote, string itemId) =>
        isNote ? $"n:{itemId}" : $"c:{itemId}";

    private async Task<Dictionary<string, string>> LoadPeerAckStateAsync(string peerId)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SyncAckStateKey);
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
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SyncAckStateKey);
            if (string.IsNullOrWhiteSpace(json))
                all = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            else
                all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                      ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            all[peerId] = peerState;
            await _js.InvokeVoidAsync(
                "localStorage.setItem",
                SyncAckStateKey,
                System.Text.Json.JsonSerializer.Serialize(all));
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
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SyncAckStateKey);
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
                await _js.InvokeVoidAsync(
                    "localStorage.setItem",
                    SyncAckStateKey,
                    System.Text.Json.JsonSerializer.Serialize(all));
            }
        }
        catch { }
    }

    public async Task EnqueueConvoDeleteAsync(string targetDeviceId, string convoId, DateTime deletedAtUtc)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _hub?.State != HubConnectionState.Connected)
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
        if (string.IsNullOrEmpty(targetDeviceId) || _hub?.State != HubConnectionState.Connected)
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
            var messages = await _conversationStore.LoadConversationAsync(_js, convoId);
            var (title, titleIsCustom) = await _conversationStore.GetMetaTitleInfoAsync(_js, convoId);
            var dataJson = ConvoSyncPayload.Serialize(convoId, title, messages, titleIsCustom);
            var fingerprint = SyncFingerprint.ForConversation(convoId, title, messages);
            if (await IsItemAcknowledgedAsync(peerId, isNote: false, convoId, fingerprint))
            {
                Console.WriteLine($"[WasmSyncService] Skipping convo {convoId} for {peerId} (peer already has current version)");
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
            var index = await _noteStore.LoadIndexAsync(_js);
            var title = index.FirstOrDefault(n => n.Id == noteId)?.Title ?? noteId;
            var entries = await _noteStore.LoadNoteAsync(_js, noteId);
            var dataJson = NoteSyncPayload.Serialize(noteId, title, entries);
            var fingerprint = SyncFingerprint.ForNote(noteId, title, entries);
            if (await IsItemAcknowledgedAsync(peerId, isNote: true, noteId, fingerprint))
            {
                Console.WriteLine($"[WasmSyncService] Skipping note {noteId} for {peerId} (peer already has current version)");
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
            $"[WasmSyncService] Manifest result for {peerId}: " +
            $"{response.UpToDateConvos} convo(s) and {response.UpToDateNotes} note(s) up to date; " +
            $"queued {queued} item(s)");

        return queued;
    }

    private async Task EnqueueSyncAsync(string targetDeviceId, SyncQueueItem item, bool allowDuplicate = false)
    {
        if (!allowDuplicate && IsAlreadyQueuedOrActive(targetDeviceId, item))
        {
            Console.WriteLine(
                $"[WasmSyncService] Skipping duplicate {(item.IsNote ? "note" : "convo")} " +
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
            $"[WasmSyncService] Enqueued {itemLabel} for {targetDeviceId} (queue depth: {queue.Count})");
        await ProcessSyncQueueAsync(targetDeviceId);
    }

    private async Task ProcessSyncQueueAsync(string targetDeviceId)
    {
        if (_activeSyncByPeer.TryGetValue(targetDeviceId, out var active))
        {
            var pending = _syncQueues.TryGetValue(targetDeviceId, out var q) ? q.Count : 0;
            Console.WriteLine(
                $"[WasmSyncService] Sync queue for {targetDeviceId} waiting " +
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
            Console.WriteLine($"[WasmSyncService] Starting WebRTC sync for {targetDeviceId}: {label}");
            await StartWebRtcDataChannelAsync(targetDeviceId, channelLabel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] WebRTC sync start failed: {ex.Message}");
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
                    Console.WriteLine($"[WasmSyncService] Sync timed out for peer {peerId} after {SyncItemTimeout.TotalSeconds}s");
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
        Console.WriteLine($"[WasmSyncService] Active sync failed for {peerId}: {reason}");
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
                $"[WasmSyncService] Re-queued {(failedItem.IsNote ? "note" : "convo")} {failedItem.ItemId} " +
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
        if (!AutoSyncChatHistory || _syncTargetDeviceIds.Count == 0)
            return;

        var forceTitleSync = !string.IsNullOrWhiteSpace(title);

        _ = DebouncedAutoSyncAsync($"convo:{convoId}", async () =>
        {
            await EnsureConnectedAndRegisteredAsync();
            if (_hub?.State != HubConnectionState.Connected)
                return;

            if (forceTitleSync)
                await ClearPeerAckForItemAsync(isNote: false, convoId);

            var manifest = await _conversationStore.LoadManifestEntriesAsync(_js);
            var entry = manifest.FirstOrDefault(c => c.Id == convoId);
            var fingerprint = entry?.ContentFingerprint;

            var messages = await _conversationStore.LoadConversationAsync(_js, convoId);
            foreach (var targetId in GetOnlineSyncTargetIds())
            {
                if (!forceTitleSync
                    && await IsItemAcknowledgedAsync(targetId, isNote: false, convoId, fingerprint))
                {
                    Console.WriteLine($"[WasmSyncService] Skipping convo {convoId} for {targetId} (unchanged since last ack)");
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
        if (!AutoSyncChatHistory || _syncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"convo-delete:{convoId}", async () =>
        {
            await EnsureConnectedAndRegisteredAsync();
            if (_hub?.State != HubConnectionState.Connected)
                return;

            foreach (var targetId in GetOnlineSyncTargetIds())
            {
                if (await IsItemAcknowledgedAsync(targetId, isNote: false, convoId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    Console.WriteLine($"[WasmSyncService] Skipping convo delete {convoId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueConvoDeleteAsync(targetId, convoId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncNoteDeleteAfterLocalDelete(string noteId, DateTime deletedAtUtc)
    {
        if (!AutoSyncNotes || _syncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"note-delete:{noteId}", async () =>
        {
            await EnsureConnectedAndRegisteredAsync();
            if (_hub?.State != HubConnectionState.Connected)
                return;

            foreach (var targetId in GetOnlineSyncTargetIds())
            {
                if (await IsItemAcknowledgedAsync(targetId, isNote: true, noteId, DeleteSyncPayload.AckValue(deletedAtUtc.Ticks)))
                {
                    Console.WriteLine($"[WasmSyncService] Skipping note delete {noteId} for {targetId} (already acknowledged)");
                    continue;
                }

                await EnqueueNoteDeleteAsync(targetId, noteId, deletedAtUtc);
            }
        });
    }

    public void ScheduleAutoSyncNoteAfterLocalSave(string noteId, string title)
    {
        if (!AutoSyncNotes || _syncTargetDeviceIds.Count == 0)
            return;

        _ = DebouncedAutoSyncAsync($"note:{noteId}", async () =>
        {
            await EnsureConnectedAndRegisteredAsync();
            if (_hub?.State != HubConnectionState.Connected)
                return;

            var manifest = await _noteStore.LoadManifestEntriesAsync(_js);
            var entry = manifest.FirstOrDefault(n => n.Id == noteId);
            var fingerprint = entry?.ContentFingerprint;

            var entries = await _noteStore.LoadNoteAsync(_js, noteId);
            foreach (var targetId in GetOnlineSyncTargetIds())
            {
                if (await IsItemAcknowledgedAsync(targetId, isNote: true, noteId, fingerprint))
                {
                    Console.WriteLine($"[WasmSyncService] Skipping note {noteId} for {targetId} (unchanged since last ack)");
                    continue;
                }

                await EnqueueNoteSyncAsync(targetId, noteId, title, entries);
            }
        });
    }

    private IEnumerable<string> GetOnlineSyncTargetIds() =>
        Devices
            .Where(d => d.IsOnline && !IsSelf(d.DeviceId) && _syncTargetDeviceIds.Contains(d.DeviceId))
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
            var convos = await _conversationStore.LoadManifestEntriesAsync(_js);
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
            var notes = await _noteStore.LoadManifestEntriesAsync(_js);
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
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SyncManifestVerifiedKey);
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
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", SyncManifestVerifiedKey);
            if (string.IsNullOrWhiteSpace(json))
                all = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            else
                all = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(json)
                      ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            all[peerId] = DateTime.UtcNow.Ticks;
            await _js.InvokeVoidAsync(
                "localStorage.setItem",
                SyncManifestVerifiedKey,
                System.Text.Json.JsonSerializer.Serialize(all));
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
        if (!_auth.IsAuthenticated || IsSelf(deviceId) || !_syncTargetDeviceIds.Contains(deviceId))
            return;

        if (!AutoSyncChatHistory && !AutoSyncNotes)
            return;

        try
        {
            if (!await HasPendingOutboundSyncAsync(deviceId, AutoSyncChatHistory, AutoSyncNotes))
            {
                Console.WriteLine($"[WasmSyncService] Skipping auto-sync for {deviceId} (all items already acknowledged)");
                return;
            }

            var lastVerified = await GetLastManifestVerifiedUtcAsync(deviceId);
            if (lastVerified.HasValue && DateTime.UtcNow - lastVerified.Value < ManifestRecheckCooldown)
            {
                var minutesAgo = (int)(DateTime.UtcNow - lastVerified.Value).TotalMinutes;
                Console.WriteLine(
                    $"[WasmSyncService] Skipping auto-sync for {deviceId} " +
                    $"(manifest verified {minutesAgo}m ago)");
                return;
            }

            await EnqueueManifestExchangeAsync(deviceId, AutoSyncChatHistory, AutoSyncNotes);
            Console.WriteLine($"[WasmSyncService] Auto-sync manifest queued for {deviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Auto-sync failed for {deviceId}: {ex.Message}");
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
            var channelOpen = await _js.InvokeAsync<bool>("webrtcIsDataChannelOpen", peerId);
            if (channelOpen)
            {
                var reuseLabel = DescribeQueueItem(nextItem);
                Console.WriteLine($"[WasmSyncService] Reusing open channel for {peerId}: {reuseLabel}");

                if (nextItem.IsManifestExchange)
                {
                    var sent = await _js.InvokeAsync<bool>(
                        "webrtcSendData",
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
            Console.WriteLine($"[WasmSyncService] Starting WebRTC sync for {peerId}: {label}");
            await StartWebRtcDataChannelAsync(peerId, channelLabel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] WebRTC sync start failed: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    private async Task CloseWebRtcPeerAsync(string peerId)
    {
        await _js.InvokeVoidAsync("webrtcClose", peerId, new { suppressDotNetCallbacks = true });
    }

    private void ClearChunkAssembliesForPeer(string peerId)
    {
        var prefix = $"{peerId}:";
        foreach (var key in _chunkAssemblies.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _chunkAssemblies.Remove(key);
    }

    private async Task<bool> SendSyncPayloadAsync(string peerId, bool isNote, string itemId, string contentJson)
    {
        var maxMessageSize = await _js.InvokeAsync<int>("webrtcGetMaxMessageSize", peerId);
        var chunkPayloadBytes = Math.Max(4096, (int)(maxMessageSize * 0.7) - 256);
        var contentBytes = System.Text.Encoding.UTF8.GetBytes(contentJson);

        if (contentBytes.Length <= chunkPayloadBytes)
        {
            var msg = isNote
                ? new DataChannelMessage("note-sync-data", content: contentJson)
                : new DataChannelMessage("sync-data", itemId, contentJson);
            return await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(msg));
        }

        var chunkCount = (contentBytes.Length + chunkPayloadBytes - 1) / chunkPayloadBytes;
        var chunkType = isNote ? "note-sync-chunk" : "sync-chunk";
        Console.WriteLine(
            $"[WasmSyncService] Chunking sync payload for {itemId}: {contentBytes.Length} bytes -> {chunkCount} chunk(s)");

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

            var sent = await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(chunkMsg));
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
        var objRef = DotNetObjectReference.Create(this);
        await _js.InvokeVoidAsync("webrtcCreatePeerConnection", objRef, targetDeviceId);
        await _js.InvokeVoidAsync("webrtcCreateDataChannel", targetDeviceId, channelLabel);

        var offerJson = await _js.InvokeAsync<string>("webrtcCreateOffer", targetDeviceId);
        Console.WriteLine($"[WebRTC] Sending offer to {targetDeviceId}");
        await _hub!.InvokeAsync("SendToDevice", targetDeviceId, "webrtc-offer", offerJson);
    }

    private async Task HandleWebRtcOffer(string fromDeviceId, string offerJson)
    {
        if (_hub == null) return;

        Console.WriteLine($"[WebRTC] Received offer from {fromDeviceId}");

        try
        {
            var objRef = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("webrtcCreatePeerConnection", objRef, fromDeviceId);
            await _js.InvokeVoidAsync("webrtcSetRemoteDescription", fromDeviceId, offerJson);

            var answerJson = await _js.InvokeAsync<string>("webrtcCreateAnswer", fromDeviceId);
            await _hub.InvokeAsync("SendToDevice", fromDeviceId, "webrtc-answer", answerJson);
            Console.WriteLine($"[WebRTC] Sent answer to {fromDeviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle offer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswer(string fromDeviceId, string answerJson)
    {
        if (_hub == null) return;

        try
        {
            var applied = await _js.InvokeAsync<bool>("webrtcSetRemoteDescription", fromDeviceId, answerJson);
            if (applied)
                Console.WriteLine($"[WebRTC] Applied answer from {fromDeviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle answer failed: {ex.Message}");
        }
    }

    [JSInvokable]
    public async Task OnIceCandidate(string peerId, string candidateJson)
    {
        if (_hub is { State: HubConnectionState.Connected })
        {
            var targetDeviceId = GetDeviceIdFromPeerKey(peerId);

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                peerKey = peerId,
                ice = System.Text.Json.JsonDocument.Parse(candidateJson).RootElement
            });

            await _hub.InvokeAsync("SendToDevice", targetDeviceId, "webrtc-ice", payload);
        }
    }

    [JSInvokable]
    public async Task OnDataChannelOpen(string peerId)
    {
        Console.WriteLine($"[WasmSyncService] DataChannel open for peer {peerId}");

        if (IsAiProxyPeerKey(peerId))
        {
            // Only the browser that selected a remote AI server acts as the client.
            if (!string.IsNullOrEmpty(AiServerDeviceId))
            {
                _aiProxyConnecting = false;
                IsAiProxyConnected = true;
                AiProxyError = null;
                OnChanged?.Invoke();
                _ = RequestRemoteModelsAsync();
            }
            return;
        }

        if (!_activeSyncByPeer.TryGetValue(peerId, out var item))
            return;

        if (item.IsManifestExchange)
        {
            try
            {
                var sent = await _js.InvokeAsync<bool>(
                    "webrtcSendData",
                    peerId,
                    SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-offer", content: item.DataJson)));
                if (!sent)
                    await FailActiveSyncAsync(peerId, "data channel not ready for manifest");
                else
                    Console.WriteLine($"[WasmSyncService] Sent sync-manifest-offer to {peerId}");
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
                    $"[WasmSyncService] Sending {(item.IsNote ? "note" : "convo")} delete for {item.ItemId} to {peerId}");
                var msg = new DataChannelMessage(deleteType, content: item.DataJson);
                var sent = await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(msg));
                if (!sent)
                {
                    await FailActiveSyncAsync(peerId, "data channel not ready for delete");
                    return;
                }

                Console.WriteLine($"[WasmSyncService] Sent {deleteType} to {peerId} for {item.ItemId}");
                return;
            }

            var payloadBytes = System.Text.Encoding.UTF8.GetByteCount(item.DataJson);
            Console.WriteLine(
                $"[WasmSyncService] Preparing {(item.IsNote ? "note" : "convo")} sync payload " +
                $"for {item.ItemId} ({payloadBytes} bytes)");

            var payloadSent = await SendSyncPayloadAsync(peerId, item.IsNote, item.ItemId, item.DataJson);
            if (!payloadSent)
            {
                Console.WriteLine($"[WasmSyncService] webrtcSendData failed (channel not ready) for {peerId}");
                await FailActiveSyncAsync(peerId, "data channel not ready for send");
                return;
            }

            Console.WriteLine(
                $"[WasmSyncService] Sent {(item.IsNote ? "note-sync-data" : "sync-data")} " +
                $"over DataChannel to {peerId} for {item.ItemId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] DataChannel send failed for {peerId}: {ex.Message}");
            await FailActiveSyncAsync(peerId, ex.Message);
        }
    }

    [JSInvokable]
    public void OnWebRtcConnectionStateChange(string peerId, string state)
    {
        if (IsAiProxyPeerKey(peerId))
            return;

        if (!_activeSyncByPeer.ContainsKey(peerId))
            return;

        if (state is "failed" or "disconnected" or "closed")
            _ = FailActiveSyncAsync(peerId, $"WebRTC connection {state}");
    }

    [JSInvokable]
    public async void OnDataReceived(string peerId, string data)
    {
        try
        {
            if (IsAiProxyPeerKey(peerId))
            {
                await HandleAiProxyDataReceived(peerId, data);
                return;
            }

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
                await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(ack));
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
                    await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(ack));
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
                    await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(ack));
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
                    await _js.InvokeAsync<bool>("webrtcSendData", peerId, SerializeDataChannelMessage(ack));
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
            Console.WriteLine($"[WasmSyncService] Failed to parse DataChannel message: {ex.Message}");
        }
    }

    private async Task HandleManifestOfferAsync(string peerId, string offerJson)
    {
        var offer = System.Text.Json.JsonSerializer.Deserialize<SyncManifestOffer>(offerJson);
        if (offer == null)
            return;

        var localConvos = await _conversationStore.LoadManifestEntriesAsync(_js, backfillMissingFingerprints: true);
        var localNotes = await _noteStore.LoadManifestEntriesAsync(_js, backfillMissingFingerprints: true);

        var neededConvos = new List<string>();
        var senderShouldDeleteConvos = new List<DeleteSyncPayload>();
        var upToDateConvos = 0;
        var appliedConvoDeletes = 0;
        foreach (var remote in offer.Convos)
        {
            var local = localConvos.FirstOrDefault(c => string.Equals(c.Id, remote.Id, StringComparison.Ordinal));

            if (remote.IsDeleted)
            {
                if (await _conversationStore.TryApplyRemoteDeleteAsync(_js, remote.Id, remote.DeletedAtTicks!.Value))
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
                if (await _noteStore.TryApplyRemoteDeleteAsync(_js, remote.Id, remote.DeletedAtTicks!.Value))
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
        await _js.InvokeAsync<bool>(
            "webrtcSendData",
            peerId,
            SerializeDataChannelMessage(new DataChannelMessage("sync-manifest-response", content: responseJson)));

        Console.WriteLine(
            $"[WasmSyncService] Manifest offer from {peerId}: " +
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
            if (await _conversationStore.TryApplyRemoteDeleteAsync(_js, del.Id, del.DeletedAtTicks))
                appliedConvoDeletes++;
        }

        var appliedNoteDeletes = 0;
        foreach (var del in response.SenderShouldDeleteNotes ?? [])
        {
            if (await _noteStore.TryApplyRemoteDeleteAsync(_js, del.Id, del.DeletedAtTicks))
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
            Console.WriteLine($"[WasmSyncService] Delta sync complete for {peerId} — peer is up to date");
        await AdvanceSyncQueueAsync(peerId);
    }

    private async Task HandleSyncAckAsync(string convoId, string peerId)
    {
        Console.WriteLine($"[WasmSyncService] Received sync-ack for convo {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnSyncAckReceived?.Invoke(convoId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleSyncAck(string convoId, string peerId) =>
        _ = HandleSyncAckAsync(convoId, peerId);

    private async Task HandleNoteSyncAckAsync(string noteId, string peerId)
    {
        Console.WriteLine($"[WasmSyncService] Received note-sync-ack for note {noteId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        OnNoteSyncAckReceived?.Invoke(noteId, peerId);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleNoteSyncAck(string noteId, string peerId) =>
        _ = HandleNoteSyncAckAsync(noteId, peerId);

    private async Task HandleConvoDeleteAckAsync(string convoId, string peerId)
    {
        Console.WriteLine($"[WasmSyncService] Received convo-delete-ack for {convoId} from peer {peerId}");
        if (_activeSyncByPeer.TryGetValue(peerId, out var item))
            await RecordSuccessfulSyncAsync(peerId, item);
        await AdvanceSyncQueueAsync(peerId);
    }

    private void HandleConvoDeleteAck(string convoId, string peerId) =>
        _ = HandleConvoDeleteAckAsync(convoId, peerId);

    private async Task HandleNoteDeleteAckAsync(string noteId, string peerId)
    {
        Console.WriteLine($"[WasmSyncService] Received note-delete-ack for {noteId} from peer {peerId}");
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
            if (!await _noteStore.ShouldAcceptIncomingContentAsync(_js, payload.NoteId, entries))
            {
                Console.WriteLine($"[WasmSyncService] Ignoring stale note sync for {payload.NoteId} (local delete is newer)");
                return;
            }

            var localTitle = await _noteStore.GetMetaTitleAsync(_js, payload.NoteId);
            var title = ChatMessageHelper.ResolveIncomingNoteTitle(payload.Title, localTitle);

            await _noteStore.SaveNoteAsync(_js, payload.NoteId, entries);
            await _noteStore.UpdateIndexAfterSaveAsync(_js, payload.NoteId, title, entries);

            OnNoteSyncPayloadReceived?.Invoke(payload.NoteId, json, fromDeviceId);
            OnNotesChanged?.Invoke();

            Console.WriteLine($"[WasmSyncService] Auto-saved incoming note sync for {payload.NoteId} from {fromDeviceId} ({payload.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Failed to persist incoming note sync payload: {ex.Message}");
        }
    }

    /// <summary>
    /// Fired when we (as the initiator) receive a sync-ack over the DataChannel.
    /// </summary>
    public event Action<string, string>? OnSyncAckReceived; // convoId, fromPeerId

    /// <summary>
    /// Raised when this device receives a note sync payload from a peer.
    /// </summary>
    public event Action<string, string, string>? OnNoteSyncPayloadReceived; // noteId, json, fromDeviceId

    /// <summary>
    /// Fired when we (as the initiator) receive a note-sync-ack over the DataChannel.
    /// </summary>
    public event Action<string, string>? OnNoteSyncAckReceived; // noteId, fromPeerId

    /// <summary>
    /// Central handler for incoming sync payloads (from either the old relay or WebRTC DataChannel).
    /// Persists the conversation automatically so the user gets the data even if they
    /// are not currently on the /sync page (as long as any WASM page is loaded).
    /// </summary>
    private async Task HandleIncomingConvoDeleteAsync(string json, string fromDeviceId)
    {
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _conversationStore.TryApplyRemoteDeleteAsync(_js, payload.Id, payload.DeletedAtTicks))
            {
                OnConversationsChanged?.Invoke();
                Console.WriteLine($"[WasmSyncService] Applied remote convo delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Failed to apply convo delete: {ex.Message}");
        }
    }

    private async Task HandleIncomingNoteDeleteAsync(string json, string fromDeviceId)
    {
        try
        {
            var payload = DeleteSyncPayload.Deserialize(json);
            if (payload == null || string.IsNullOrEmpty(payload.Id))
                return;

            if (await _noteStore.TryApplyRemoteDeleteAsync(_js, payload.Id, payload.DeletedAtTicks))
            {
                OnNotesChanged?.Invoke();
                Console.WriteLine($"[WasmSyncService] Applied remote note delete for {payload.Id} from {fromDeviceId}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Failed to apply note delete: {ex.Message}");
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
                Console.WriteLine($"[WasmSyncService] Ignoring invalid convo sync payload for {convoId}");
                return;
            }

            convoId = payload.ConvoId;
            msgs = ChatMessageHelper.NormalizeAll(payload.Messages);
            incomingTitle = payload.Title;
            incomingTitleIsCustom = payload.TitleIsCustom == true;

            if (!await _conversationStore.ShouldAcceptIncomingContentAsync(_js, convoId, msgs))
            {
                Console.WriteLine($"[WasmSyncService] Ignoring stale convo sync for {convoId} (local delete is newer)");
                return;
            }

            await _conversationStore.SaveConversationAsync(_js, convoId, msgs);

            if (incomingTitleIsCustom)
            {
                var localTitleInfo = await _conversationStore.GetMetaTitleInfoAsync(_js, convoId);
                var resolvedTitle = ChatMessageHelper.ResolveIncomingConvoTitle(
                    incomingTitle,
                    localTitleInfo.Title,
                    incomingTitleIsCustom: true,
                    localTitleInfo.TitleIsCustom);
                await _conversationStore.SetConversationTitleAsync(_js, convoId, resolvedTitle);
            }
            else
            {
                var currentIndex = await _conversationStore.LoadIndexAsync(_js);
                await _conversationStore.UpdateIndexAfterSaveAsync(_js, convoId, msgs, currentIndex);
            }

            OnSyncPayloadReceived?.Invoke(convoId, json, fromDeviceId);
            OnConversationsChanged?.Invoke();

            Console.WriteLine(
                $"[WasmSyncService] Auto-saved incoming sync for convo {convoId} from {fromDeviceId} " +
                $"({msgs.Count} messages, title=\"{incomingTitle ?? ""}\", custom={incomingTitleIsCustom})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Failed to persist incoming sync payload: {ex.Message}");
        }
    }

    [JSInvokable]
    public void OnDataChannelClose(string peerId)
    {
        Console.WriteLine($"[WasmSyncService] DataChannel closed for {peerId}");

        if (IsAiProxyPeerKey(peerId))
        {
            if (!string.IsNullOrEmpty(AiServerDeviceId))
            {
                IsAiProxyConnected = false;
                AiProxyError = "AI proxy connection closed.";
                OnChanged?.Invoke();
                _ = ScheduleAiProxyReconnectAsync();
            }
            return;
        }

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


    // --- AI proxy (remote chat via peer browser) ---

    private static string GetAiProxyPeerKey(string deviceId) => $"ai:{deviceId}";

    private static bool IsAiProxyPeerKey(string peerKey) =>
        peerKey.StartsWith("ai:", StringComparison.Ordinal);

    private static string GetDeviceIdFromPeerKey(string peerKey) =>
        IsAiProxyPeerKey(peerKey) ? peerKey.Substring(3) : peerKey;

    public string? GetAiServerDeviceName()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId)) return null;
        return Devices.FirstOrDefault(d => IsSelf(d.DeviceId) == false && string.Equals(d.DeviceId, AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name
               ?? Devices.FirstOrDefault(d => string.Equals(d.DeviceId, AiServerDeviceId, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public async Task SetAiServerDeviceAsync(string? deviceId)
    {
        await InitializeAsync();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            AiServerDeviceId = null;
            await _js.InvokeVoidAsync("localStorage.removeItem", AiServerDeviceIdKey);
            await CloseAiProxyConnectionAsync();
            RemoteModels = Array.Empty<WasmAiProviderService.ModelInfo>();
            OnChanged?.Invoke();
            return;
        }

        if (IsSelf(deviceId))
            return;

        AiServerDeviceId = deviceId;
        await _js.InvokeVoidAsync("localStorage.setItem", AiServerDeviceIdKey, deviceId);
        RemoteModels = Array.Empty<WasmAiProviderService.ModelInfo>();
        IsAiProxyConnected = false;
        AiProxyError = null;
        OnChanged?.Invoke();

        await EnsureAiProxyConnectionAsync();
    }

    public async Task EnsureAiProxyConnectionAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId) || _hub?.State != HubConnectionState.Connected)
            return;

        if (IsAiProxyConnected || _aiProxyConnecting)
            return;

        _aiProxyConnecting = true;
        _aiProxyReconnectCts?.Cancel();

        try
        {
            var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
            await _js.InvokeVoidAsync("webrtcClose", peerKey);

            var objRef = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("webrtcCreatePeerConnection", objRef, peerKey);
            await _js.InvokeVoidAsync("webrtcCreateDataChannel", peerKey, AiProxyDataChannelLabel);

            var offerJson = await _js.InvokeAsync<string>("webrtcCreateOffer", peerKey);
            await _hub.InvokeAsync("SendToDevice", AiServerDeviceId, "webrtc-offer-ai", offerJson);
            Console.WriteLine($"[WasmSyncService] Sent AI proxy offer to {AiServerDeviceId}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25));
                    if (_aiProxyConnecting && !IsAiProxyConnected)
                    {
                        _aiProxyConnecting = false;
                        AiProxyError = "Timed out connecting to AI server device.";
                        OnChanged?.Invoke();
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            _aiProxyConnecting = false;
            AiProxyError = $"Failed to connect AI proxy: {ex.Message}";
            Console.WriteLine($"[WasmSyncService] AI proxy connect failed: {ex.Message}");
            OnChanged?.Invoke();
        }
    }

    private async Task CloseAiProxyConnectionAsync()
    {
        _aiProxyReconnectCts?.Cancel();
        _aiProxyReconnectCts = null;

        if (!string.IsNullOrEmpty(AiServerDeviceId))
        {
            try { await _js.InvokeVoidAsync("webrtcClose", GetAiProxyPeerKey(AiServerDeviceId)); } catch { }
        }

        IsAiProxyConnected = false;
    }

    private async Task ScheduleAiProxyReconnectAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _aiProxyReconnectCts?.Cancel();
        _aiProxyReconnectCts = new CancellationTokenSource();
        var token = _aiProxyReconnectCts.Token;

        try
        {
            await Task.Delay(3000, token);
            if (!token.IsCancellationRequested)
                await EnsureAiProxyConnectionAsync();
        }
        catch (TaskCanceledException) { }
    }

    private async Task HandleWebRtcOfferAi(string fromDeviceId, string offerJson)
    {
        if (_hub == null) return;

        try
        {
            var peerKey = GetAiProxyPeerKey(fromDeviceId);
            var objRef = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("webrtcCreatePeerConnection", objRef, peerKey);
            await _js.InvokeVoidAsync("webrtcSetRemoteDescription", peerKey, offerJson);

            var answerJson = await _js.InvokeAsync<string>("webrtcCreateAnswer", peerKey);
            await _hub.InvokeAsync("SendToDevice", fromDeviceId, "webrtc-answer-ai", answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle AI offer failed: {ex.Message}");
        }
    }

    private async Task HandleWebRtcAnswerAi(string fromDeviceId, string answerJson)
    {
        try
        {
            var peerKey = GetAiProxyPeerKey(fromDeviceId);
            await _js.InvokeVoidAsync("webrtcSetRemoteDescription", peerKey, answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Handle AI answer failed: {ex.Message}");
        }
    }

    public async Task RequestRemoteModelsAsync()
    {
        if (!IsAiProxyConnected || string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _modelsListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
        var msg = new DataChannelMessage("models-request");
        Console.WriteLine($"[AI Proxy] Requesting model list from {AiServerDeviceId}");
        await _js.InvokeVoidAsync("webrtcSendData", peerKey, System.Text.Json.JsonSerializer.Serialize(msg));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _modelsListTcs.Task.WaitAsync(cts.Token);
        }
        catch
        {
            Console.WriteLine("[AI Proxy] Timed out waiting for remote model list.");
            AiProxyError = "Timed out waiting for remote model list.";
            OnChanged?.Invoke();
        }
    }

    public async Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<WasmConversationStore.ChatMessage> messages,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return ("", "");

        if (!IsAiProxyConnected)
        {
            await EnsureAiProxyConnectionAsync();
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(20));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!IsAiProxyConnected && DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, waitCts.Token);
            }
        }

        if (!IsAiProxyConnected)
            return ($"Not connected to AI server device. {AiProxyError ?? "Try again from the Sync page."}", "");

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ChatResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingChatRequests[requestId] = tcs;

        try
        {
            var payload = new ChatRequestPayload(requestId, modelId, messages);
            var json = System.Text.Json.JsonSerializer.Serialize(new DataChannelMessage("chat-request", content: System.Text.Json.JsonSerializer.Serialize(payload)));
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
            if (byteCount > 2_000_000)
                Console.WriteLine($"[WasmSyncService] Large chat-request payload: {byteCount} bytes");

            var peerKey = GetAiProxyPeerKey(AiServerDeviceId);
            Console.WriteLine($"[AI Proxy] → chat-request model={modelId} messages={messages.Count} id={requestId[..8]}");
            await _js.InvokeVoidAsync("webrtcSendData", peerKey, json);

            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(TimeSpan.FromMinutes(10));
            var response = await tcs.Task.WaitAsync(responseCts.Token);

            if (!string.IsNullOrEmpty(response.Error))
            {
                Console.WriteLine($"[AI Proxy] ← chat-response id={requestId[..8]} error: {response.Error}");
                return ($"Remote AI error: {response.Error}", response.ToolTrace ?? "");
            }

            var replyLen = response.Text?.Length ?? 0;
            Console.WriteLine($"[AI Proxy] ← chat-response id={requestId[..8]} ok ({replyLen} chars)");
            return (response.Text ?? "No response.", response.ToolTrace ?? "");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI Proxy] ← chat-request id={requestId[..8]} failed: {ex.Message}");
            return ($"Remote AI request failed: {ex.Message}", "");
        }
        finally
        {
            _pendingChatRequests.Remove(requestId);
        }
    }

    private async Task HandleAiProxyDataReceived(string peerKey, string data)
    {
        var msg = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(data);
        if (msg == null) return;

        if (msg.type == "models-request")
        {
            await _keyStore.LoadAsync(_js);
            await _aiProvider.RefreshProxiedProvidersAsync();
            var models = _aiProvider.GetAvailableModels();
            Console.WriteLine($"[AI Proxy] Serving model list ({models.Count} models) to {GetDeviceIdFromPeerKey(peerKey)}");
            var listMsg = new DataChannelMessage("models-list", content: System.Text.Json.JsonSerializer.Serialize(models));
            await _js.InvokeVoidAsync("webrtcSendData", peerKey, System.Text.Json.JsonSerializer.Serialize(listMsg));
            return;
        }

        if (msg.type == "models-list" && msg.content != null)
        {
            var models = System.Text.Json.JsonSerializer.Deserialize<List<WasmAiProviderService.ModelInfo>>(msg.content)
                         ?? new List<WasmAiProviderService.ModelInfo>();
            var serverName = GetAiServerDeviceName() ?? "remote device";
            RemoteModels = models
                .Select(m => m with { Label = $"{m.Label} (via {serverName})" })
                .ToList();
            Console.WriteLine($"[AI Proxy] Received model list ({RemoteModels.Count} models via {serverName})");
            _modelsListTcs?.TrySetResult(true);
            OnChanged?.Invoke();
            return;
        }

        if (msg.type == "chat-request" && msg.content != null)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ChatRequestPayload>(msg.content);
            if (request == null) return;

            var reqIdShort = request.RequestId.Length >= 8 ? request.RequestId[..8] : request.RequestId;
            Console.WriteLine($"[AI Proxy] ← chat-request from {GetDeviceIdFromPeerKey(peerKey)} model={request.ModelId} messages={request.Messages.Count} id={reqIdShort}");
            var started = DateTime.UtcNow;

            var result = await _chatCompletion.CompleteAsync(
                request.ModelId,
                request.Messages,
                currentUser: _auth.Email,
                ct: default);

            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var response = new ChatResponsePayload(
                request.RequestId,
                result.Text,
                result.ToolTrace,
                result.Error);

            if (!string.IsNullOrEmpty(result.Error))
                Console.WriteLine($"[AI Proxy] → chat-response id={reqIdShort} error ({elapsed:F1}s): {result.Error}");
            else
                Console.WriteLine($"[AI Proxy] → chat-response id={reqIdShort} ok ({result.Text?.Length ?? 0} chars, {elapsed:F1}s)");

            var responseMsg = new DataChannelMessage("chat-response", content: System.Text.Json.JsonSerializer.Serialize(response));
            await _js.InvokeVoidAsync("webrtcSendData", peerKey, System.Text.Json.JsonSerializer.Serialize(responseMsg));
            return;
        }

        if (msg.type == "chat-response" && msg.content != null)
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<ChatResponsePayload>(msg.content);
            if (response?.RequestId != null && _pendingChatRequests.TryGetValue(response.RequestId, out var tcs))
                tcs.TrySetResult(response);
        }
    }

    private record ChatRequestPayload(string RequestId, string ModelId, List<WasmConversationStore.ChatMessage> Messages);
    private record ChatResponsePayload(string RequestId, string? Text, string? ToolTrace, string? Error);

    /// <summary>
    /// Returns true if the given deviceId matches the one belonging to this browser instance.
    /// </summary>
    public bool IsSelf(string? deviceId) =>
        !string.IsNullOrEmpty(deviceId) &&
        string.Equals(deviceId, MyDeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns only the devices that are *not* this one (for "sync to these peers" checkboxes).
    /// </summary>
    public IEnumerable<DeviceInfo> GetOtherDevices() =>
        Devices.Where(d => !IsSelf(d.DeviceId));

    public async ValueTask DisposeAsync()
    {
        _auth.OnChanged -= OnAuthChanged;

        if (_hub != null)
        {
            try { await _hub.DisposeAsync(); } catch { }
        }
    }

    /// <summary>
    /// Lightweight public shape for UI binding (matches what the server broadcasts).
    /// </summary>
    public record DeviceInfo(
        string DeviceId,
        string Name,
        DateTime LastActiveUtc,
        bool IsOnline,
        bool CanRelayAi = false,
        int AiModelCount = 0);
}
