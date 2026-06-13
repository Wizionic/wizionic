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
    private readonly WasmAiProviderService _aiProvider;
    private readonly WasmKeyStore _keyStore;
    private readonly WasmChatCompletionService _chatCompletion;

    private HubConnection? _hub;
    private bool _initialized;

    private const string DeviceIdKey = "chatfish-device-id";
    private const string DeviceNameKey = "chatfish-device-name";
    private const string AiServerDeviceIdKey = "chatfish-ai-server-device-id";
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

    public event Action? OnChanged;

    /// <summary>
    /// Fired whenever a conversation is added or updated via incoming sync (background or foreground).
    /// Pages like Chat can subscribe to this to refresh their conversation list / sidebar.
    /// </summary>
    public event Action? OnConversationsChanged;

    public WasmSyncService(
        IJSRuntime js,
        HttpClient http,
        WasmAuthService auth,
        WasmConversationStore conversationStore,
        WasmAiProviderService aiProvider,
        WasmKeyStore keyStore,
        WasmChatCompletionService chatCompletion)
    {
        _js = js;
        _http = http;
        _auth = auth;
        _conversationStore = conversationStore;
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
                Devices = list ?? Array.Empty<DeviceInfo>();
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
            var json = System.Text.Json.JsonSerializer.Serialize(messages);
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

    private readonly Dictionary<string, (string ConvoId, string DataJson)> _pendingSyncData = new();

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
                Console.WriteLine($"[WebRTC] Received ICE candidate from {fromDeviceId}");
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

    public async Task StartWebRtcSyncAsync(string targetDeviceId, string convoId, List<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(targetDeviceId) || _hub?.State != HubConnectionState.Connected)
            return;

        var dataJson = System.Text.Json.JsonSerializer.Serialize(messages);
        _pendingSyncData[targetDeviceId] = (convoId, dataJson);

        try
        {
            var objRef = DotNetObjectReference.Create(this);
            await _js.InvokeVoidAsync("webrtcCreatePeerConnection", objRef, targetDeviceId);
            await _js.InvokeVoidAsync("webrtcCreateDataChannel", targetDeviceId, "chatfish-sync");

            var offerJson = await _js.InvokeAsync<string>("webrtcCreateOffer", targetDeviceId);
            Console.WriteLine($"[WebRTC] Sending offer to {targetDeviceId}");
            await _hub.InvokeAsync("SendToDevice", targetDeviceId, "webrtc-offer", offerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] WebRTC sync start failed: {ex.Message}");
            _pendingSyncData.Remove(targetDeviceId);
        }
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

        Console.WriteLine($"[WebRTC] Received answer from {fromDeviceId}");

        try
        {
            await _js.InvokeVoidAsync("webrtcSetRemoteDescription", fromDeviceId, answerJson);
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
            Console.WriteLine($"[WebRTC] Sending ICE candidate to {targetDeviceId} (peer {peerId})");

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                peerKey = peerId,
                ice = System.Text.Json.JsonDocument.Parse(candidateJson).RootElement
            });

            await _hub.InvokeAsync("SendToDevice", targetDeviceId, "webrtc-ice", payload);
        }
    }

    [JSInvokable]
    public void OnDataChannelOpen(string peerId)
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

        if (_pendingSyncData.TryGetValue(peerId, out var data))
        {
            var msg = new DataChannelMessage("sync-data", data.ConvoId, data.DataJson);
            var toSend = System.Text.Json.JsonSerializer.Serialize(msg);
            _ = _js.InvokeVoidAsync("webrtcSendData", peerId, toSend);

            Console.WriteLine($"[WasmSyncService] Sent sync-data over DataChannel to {peerId} for {data.ConvoId}");
        }
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

            if (msg.type == "sync-data" && msg.convoId != null && msg.content != null)
            {
                // This will save the data (even in background) and fire the event
                await HandleIncomingSyncPayload(msg.convoId, msg.content, peerId);

                // Send acknowledgement back over the same DataChannel
                var ack = new DataChannelMessage("sync-ack", msg.convoId);
                var ackJson = System.Text.Json.JsonSerializer.Serialize(ack);
                _ = _js.InvokeVoidAsync("webrtcSendData", peerId, ackJson);

                // Close after a short delay to let the ack go through
                _ = Task.Delay(300).ContinueWith(_ =>
                    _js.InvokeVoidAsync("webrtcClose", peerId));
            }
            else if (msg.type == "sync-ack" && msg.convoId != null)
            {
                HandleSyncAck(msg.convoId, peerId);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WasmSyncService] Failed to parse DataChannel message: {ex.Message}");
        }
    }

    private void HandleSyncAck(string convoId, string peerId)
    {
        Console.WriteLine($"[WasmSyncService] Received sync-ack for convo {convoId} from peer {peerId}");

        _pendingSyncData.Remove(peerId);

        // Close the connection now that we have confirmation
        _ = _js.InvokeVoidAsync("webrtcClose", peerId);

        // Notify listeners (page can update status if it wants)
        OnSyncAckReceived?.Invoke(convoId, peerId);
    }

    /// <summary>
    /// Fired when we (as the initiator) receive a sync-ack over the DataChannel.
    /// </summary>
    public event Action<string, string>? OnSyncAckReceived; // convoId, fromPeerId

    /// <summary>
    /// Central handler for incoming sync payloads (from either the old relay or WebRTC DataChannel).
    /// Persists the conversation automatically so the user gets the data even if they
    /// are not currently on the /sync page (as long as any WASM page is loaded).
    /// </summary>
    private async Task HandleIncomingSyncPayload(string convoId, string json, string fromDeviceId)
    {
        try
        {
            var msgs = System.Text.Json.JsonSerializer.Deserialize<List<ChatMessage>>(json) ?? new();

            // Persist the content (the store will encrypt it at rest using the user's key)
            await _conversationStore.SaveConversationAsync(_js, convoId, msgs);

            // Update the meta/index so the conversation appears in chat lists / sidebar
            // even if the user never opens the sync page.
            var currentIndex = await _conversationStore.LoadIndexAsync(_js);
            await _conversationStore.UpdateIndexAfterSaveAsync(_js, convoId, msgs, currentIndex);

            // Notify any listeners (e.g. the sync page if it is currently open) so they
            // can refresh their lists and show a "new sync received" message.
            OnSyncPayloadReceived?.Invoke(convoId, json, fromDeviceId);

            // Lightweight event so pages that show the conversation list (Chat sidebar, etc.)
            // can automatically refresh when a sync arrives in the background.
            OnConversationsChanged?.Invoke();

            Console.WriteLine($"[WasmSyncService] Auto-saved incoming sync for convo {convoId} from {fromDeviceId} ({msgs.Count} messages)");
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

        _pendingSyncData.Remove(peerId);
        _ = _js.InvokeVoidAsync("webrtcClose", peerId);
    }

    private record DataChannelMessage(string type, string? convoId = null, string? content = null);

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
