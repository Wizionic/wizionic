using App.Core.Auth;
using App.Core.Chat;
using App.Core.Storage;
using App.Core.Sync;

namespace App.Shared.Services;

/// <summary>
/// WebRTC data-channel relay for routing chat completions through a peer device (AI server).
/// Shared by WASM and MAUI sync services.
/// </summary>
public sealed class AiProxyRelay
{
    public const string DataChannelLabel = "app-ai-proxy";

    private readonly IWebRtcTransport _webrtc;
    private readonly IKeyStore _keyStore;
    private readonly ChatModelCatalogService _modelCatalog;
    private readonly ChatCompletionService _chatCompletion;
    private readonly IAuthService _auth;
    private readonly Func<string, string, string, Task> _sendToDeviceAsync;
    private readonly Func<bool> _isHubConnected;
    private readonly Func<string?> _getAiServerDeviceName;
    private readonly Action _notifyChanged;

    private bool _connecting;
    private CancellationTokenSource? _reconnectCts;
    private TaskCompletionSource<bool>? _modelsListTcs;
    private readonly Dictionary<string, TaskCompletionSource<ChatResponsePayload>> _pendingChatRequests = new(StringComparer.OrdinalIgnoreCase);

    public string? AiServerDeviceId { get; set; }
    public bool IsConnected { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyList<SyncModelInfo> RemoteModels { get; private set; } = Array.Empty<SyncModelInfo>();

    public AiProxyRelay(
        IWebRtcTransport webrtc,
        IKeyStore keyStore,
        ChatModelCatalogService modelCatalog,
        ChatCompletionService chatCompletion,
        IAuthService auth,
        Func<string, string, string, Task> sendToDeviceAsync,
        Func<bool> isHubConnected,
        Func<string?> getAiServerDeviceName,
        Action notifyChanged)
    {
        _webrtc = webrtc;
        _keyStore = keyStore;
        _modelCatalog = modelCatalog;
        _chatCompletion = chatCompletion;
        _auth = auth;
        _sendToDeviceAsync = sendToDeviceAsync;
        _isHubConnected = isHubConnected;
        _getAiServerDeviceName = getAiServerDeviceName;
        _notifyChanged = notifyChanged;
    }

    public static string GetPeerKey(string deviceId) => $"ai:{deviceId}";

    public static bool IsAiPeer(string peerKey) =>
        peerKey.StartsWith("ai:", StringComparison.Ordinal);

    public static string GetDeviceIdFromPeerKey(string peerKey) =>
        IsAiPeer(peerKey) ? peerKey[3..] : peerKey;

    public async Task SetAiServerDeviceAsync(string? deviceId, Func<Task> persistClearAsync, Func<string, Task> persistSetAsync, Func<string, bool> isSelf)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            AiServerDeviceId = null;
            await persistClearAsync();
            await CloseConnectionAsync();
            RemoteModels = Array.Empty<SyncModelInfo>();
            _notifyChanged();
            return;
        }

        if (isSelf(deviceId))
            return;

        AiServerDeviceId = deviceId;
        await persistSetAsync(deviceId);
        RemoteModels = Array.Empty<SyncModelInfo>();
        IsConnected = false;
        Error = null;
        _notifyChanged();

        await EnsureConnectionAsync();
    }

    public async Task EnsureConnectionAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId) || !_isHubConnected())
            return;

        if (IsConnected || _connecting)
            return;

        _connecting = true;
        _reconnectCts?.Cancel();

        try
        {
            var peerKey = GetPeerKey(AiServerDeviceId);
            await _webrtc.CloseAsync(peerKey);
            await _webrtc.CreatePeerConnectionAsync(peerKey, new AiProxyPeerCallbacks(this));

            await _webrtc.CreateDataChannelAsync(peerKey, DataChannelLabel);

            var offerJson = await _webrtc.CreateOfferAsync(peerKey);
            if (!string.IsNullOrEmpty(offerJson))
                await _sendToDeviceAsync(AiServerDeviceId, "webrtc-offer-ai", offerJson);

            Console.WriteLine($"[AiProxyRelay] Sent AI proxy offer to {AiServerDeviceId}");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25));
                    if (_connecting && !IsConnected)
                    {
                        _connecting = false;
                        Error = "Timed out connecting to AI server device.";
                        _notifyChanged();
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            _connecting = false;
            Error = $"Failed to connect AI proxy: {ex.Message}";
            Console.WriteLine($"[AiProxyRelay] Connect failed: {ex.Message}");
            _notifyChanged();
        }
    }

    private sealed class AiProxyPeerCallbacks(AiProxyRelay relay) : IWebRtcTransportCallbacks
    {
        public Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default) =>
            relay.OnIceCandidateAsync(peerId, candidateJson, ct);

        public Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default) =>
            relay.OnDataChannelOpenAsync(peerId, ct);

        public void OnConnectionStateChange(string peerId, string state) =>
            relay.OnConnectionStateChange(peerId, state);

        public Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default) =>
            relay.OnDataReceivedAsync(peerId, data, ct);

        public void OnDataChannelClose(string peerId) =>
            relay.OnDataChannelClose(peerId);
    }

    public async Task<bool> TryHandleSignalingAsync(string fromDeviceId, string type, string payload)
    {
        if (type == "webrtc-offer-ai")
        {
            await HandleOfferAsync(fromDeviceId, payload);
            return true;
        }

        if (type == "webrtc-answer-ai")
        {
            await HandleAnswerAsync(fromDeviceId, payload);
            return true;
        }

        if (type == "webrtc-ice" && TryUnwrapIcePayload(payload, out var senderPeerKey, out var iceJson)
            && IsAiPeer(senderPeerKey))
        {
            var localPeerKey = GetPeerKey(fromDeviceId);
            await _webrtc.AddIceCandidateAsync(localPeerKey, iceJson);
            return true;
        }

        return false;
    }

    public Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        if (!IsAiPeer(peerId) || !_isHubConnected())
            return Task.CompletedTask;

        var targetDeviceId = GetDeviceIdFromPeerKey(peerId);
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            peerKey = peerId,
            ice = System.Text.Json.JsonDocument.Parse(candidateJson).RootElement
        });
        return _sendToDeviceAsync(targetDeviceId, "webrtc-ice", payload);
    }

    public Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default)
    {
        if (!IsAiPeer(peerId) || string.IsNullOrEmpty(AiServerDeviceId))
            return Task.CompletedTask;

        _connecting = false;
        IsConnected = true;
        Error = null;
        _notifyChanged();
        _ = RequestRemoteModelsAsync();
        return Task.CompletedTask;
    }

    public void OnConnectionStateChange(string peerId, string state) { }

    public Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default)
    {
        if (!IsAiPeer(peerId))
            return Task.CompletedTask;

        return HandleDataReceivedAsync(peerId, data);
    }

    public void OnDataChannelClose(string peerId)
    {
        if (!IsAiPeer(peerId) || string.IsNullOrEmpty(AiServerDeviceId))
            return;

        IsConnected = false;
        Error = "AI proxy connection closed.";
        _notifyChanged();
        _ = ScheduleReconnectAsync();
    }

    public async Task RequestRemoteModelsAsync()
    {
        if (!IsConnected || string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _modelsListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peerKey = GetPeerKey(AiServerDeviceId);
        var msg = new DataChannelMessage("models-request");
        await _webrtc.SendDataAsync(peerKey, System.Text.Json.JsonSerializer.Serialize(msg));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _modelsListTcs.Task.WaitAsync(cts.Token);
        }
        catch
        {
            Error = "Timed out waiting for remote model list.";
            _notifyChanged();
        }
    }

    public async Task<(string Text, string ToolTrace)> SendChatRequestAsync(
        string modelId,
        List<ChatMessage> messages,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return ("", "");

        if (!IsConnected)
        {
            await EnsureConnectionAsync();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!IsConnected && DateTime.UtcNow < deadline)
                await Task.Delay(250, ct);
        }

        if (!IsConnected)
            return ($"Not connected to AI server device. {Error ?? "Try again from the Sync page."}", "");

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ChatResponsePayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingChatRequests[requestId] = tcs;

        try
        {
            var payload = new ChatRequestPayload(requestId, modelId, messages);
            var json = System.Text.Json.JsonSerializer.Serialize(
                new DataChannelMessage("chat-request", content: System.Text.Json.JsonSerializer.Serialize(payload)));

            var peerKey = GetPeerKey(AiServerDeviceId);
            await _webrtc.SendDataAsync(peerKey, json);

            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseCts.CancelAfter(TimeSpan.FromMinutes(10));
            var response = await tcs.Task.WaitAsync(responseCts.Token);

            if (!string.IsNullOrEmpty(response.Error))
                return ($"Remote AI error: {response.Error}", response.ToolTrace ?? "");

            return (response.Text ?? "No response.", response.ToolTrace ?? "");
        }
        catch (Exception ex)
        {
            return ($"Remote AI request failed: {ex.Message}", "");
        }
        finally
        {
            _pendingChatRequests.Remove(requestId);
        }
    }

    public async Task CloseConnectionAsync()
    {
        _reconnectCts?.Cancel();
        _reconnectCts = null;

        if (!string.IsNullOrEmpty(AiServerDeviceId))
        {
            try { await _webrtc.CloseAsync(GetPeerKey(AiServerDeviceId)); } catch { }
        }

        IsConnected = false;
        _connecting = false;
    }

    private async Task HandleOfferAsync(string fromDeviceId, string offerJson)
    {
        try
        {
            var peerKey = GetPeerKey(fromDeviceId);
            await _webrtc.CreatePeerConnectionAsync(peerKey, new AiProxyPeerCallbacks(this));
            await _webrtc.SetRemoteDescriptionAsync(peerKey, offerJson);

            var answerJson = await _webrtc.CreateAnswerAsync(peerKey);
            if (!string.IsNullOrEmpty(answerJson))
                await _sendToDeviceAsync(fromDeviceId, "webrtc-answer-ai", answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiProxyRelay] Handle offer failed: {ex.Message}");
        }
    }

    private async Task HandleAnswerAsync(string fromDeviceId, string answerJson)
    {
        try
        {
            var peerKey = GetPeerKey(fromDeviceId);
            await _webrtc.SetRemoteDescriptionAsync(peerKey, answerJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiProxyRelay] Handle answer failed: {ex.Message}");
        }
    }

    private async Task HandleDataReceivedAsync(string peerKey, string data)
    {
        var msg = System.Text.Json.JsonSerializer.Deserialize<DataChannelMessage>(data);
        if (msg == null) return;

        if (msg.type == "models-request")
        {
            await _keyStore.LoadAsync();
            await _modelCatalog.RefreshAsync();
            var models = _modelCatalog.GetAvailableModels().Select(ToSyncModel).ToList();
            var listMsg = new DataChannelMessage("models-list", content: System.Text.Json.JsonSerializer.Serialize(models));
            await _webrtc.SendDataAsync(peerKey, System.Text.Json.JsonSerializer.Serialize(listMsg));
            return;
        }

        if (msg.type == "models-list" && msg.content != null)
        {
            var models = System.Text.Json.JsonSerializer.Deserialize<List<SyncModelInfo>>(msg.content)
                         ?? new List<SyncModelInfo>();
            var serverName = _getAiServerDeviceName() ?? "remote device";
            RemoteModels = models.Select(m => m with { Label = $"{m.Label} (via {serverName})" }).ToList();
            _modelsListTcs?.TrySetResult(true);
            _notifyChanged();
            return;
        }

        if (msg.type == "chat-request" && msg.content != null)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<ChatRequestPayload>(msg.content);
            if (request == null) return;

            var result = await _chatCompletion.CompleteAsync(
                request.ModelId,
                request.Messages,
                currentUser: _auth.Email,
                ct: default);

            var response = new ChatResponsePayload(request.RequestId, result.Text, result.ToolTrace, result.Error);
            var responseMsg = new DataChannelMessage("chat-response", content: System.Text.Json.JsonSerializer.Serialize(response));
            await _webrtc.SendDataAsync(peerKey, System.Text.Json.JsonSerializer.Serialize(responseMsg));
            return;
        }

        if (msg.type == "chat-response" && msg.content != null)
        {
            var response = System.Text.Json.JsonSerializer.Deserialize<ChatResponsePayload>(msg.content);
            if (response?.RequestId != null && _pendingChatRequests.TryGetValue(response.RequestId, out var tcs))
                tcs.TrySetResult(response);
        }
    }

    private async Task ScheduleReconnectAsync()
    {
        if (string.IsNullOrEmpty(AiServerDeviceId))
            return;

        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        try
        {
            await Task.Delay(3000, token);
            if (!token.IsCancellationRequested)
                await EnsureConnectionAsync();
        }
        catch (TaskCanceledException) { }
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

    private static SyncModelInfo ToSyncModel(ChatModelInfo model) =>
        new(model.Id, model.Label, model.Icon, model.ProviderId, model.ProviderName,
            model.SupportsTools, model.SupportsVision, model.IsOllamaBackend,
            model.ContextSize, model.VisionProxyModelId);

    private record DataChannelMessage(
        string type,
        string? convoId = null,
        string? content = null,
        string? noteId = null,
        int? chunkIndex = null,
        int? chunkCount = null,
        string? chunkData = null);

    private record ChatRequestPayload(string RequestId, string ModelId, List<ChatMessage> Messages);
    private record ChatResponsePayload(string RequestId, string? Text, string? ToolTrace, string? Error);
}