using App.Core.Sync;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace App.Client.Services;

/// <summary>
/// Browser WebRTC transport via global helpers in Components/App.razor.
/// </summary>
public sealed class JsWebRtcTransport : IWebRtcTransport, IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly Dictionary<string, PeerEntry> _peers = new(StringComparer.OrdinalIgnoreCase);

    public JsWebRtcTransport(IJSRuntime js) => _js = js;

    public async Task CreatePeerConnectionAsync(string peerId, IWebRtcTransportCallbacks callbacks, CancellationToken ct = default)
    {
        await CloseAsync(peerId, suppressCallbacks: true, ct);

        var bridge = new JsWebRtcCallbackBridge(callbacks);
        var objRef = DotNetObjectReference.Create(bridge);
        _peers[peerId] = new PeerEntry(bridge, objRef);
        await _js.InvokeVoidAsync("webrtcCreatePeerConnection", ct, objRef, peerId);
    }

    public Task CreateDataChannelAsync(string peerId, string label, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("webrtcCreateDataChannel", ct, peerId, label).AsTask();

    public Task<string?> CreateOfferAsync(string peerId, CancellationToken ct = default) =>
        _js.InvokeAsync<string?>("webrtcCreateOffer", ct, peerId).AsTask();

    public Task<string?> CreateAnswerAsync(string peerId, CancellationToken ct = default) =>
        _js.InvokeAsync<string?>("webrtcCreateAnswer", ct, peerId).AsTask();

    public Task<bool> SetRemoteDescriptionAsync(string peerId, string sdpJson, CancellationToken ct = default) =>
        _js.InvokeAsync<bool>("webrtcSetRemoteDescription", ct, peerId, sdpJson).AsTask();

    public Task AddIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("webrtcAddIceCandidate", ct, peerId, candidateJson).AsTask();

    public Task<bool> SendDataAsync(string peerId, string data, CancellationToken ct = default) =>
        _js.InvokeAsync<bool>("webrtcSendData", ct, peerId, data).AsTask();

    public Task<bool> IsDataChannelOpenAsync(string peerId, CancellationToken ct = default) =>
        _js.InvokeAsync<bool>("webrtcIsDataChannelOpen", ct, peerId).AsTask();

    public Task<int> GetMaxMessageSizeAsync(string peerId, CancellationToken ct = default) =>
        _js.InvokeAsync<int>("webrtcGetMaxMessageSize", ct, peerId).AsTask();

    public Task WaitForSendBufferAsync(string peerId, int maxBufferedBytes = 256 * 1024, CancellationToken ct = default) =>
        _js.InvokeVoidAsync("webrtcWaitForSendBuffer", ct, peerId, maxBufferedBytes).AsTask();

    public async Task CloseAsync(string peerId, bool suppressCallbacks = false, CancellationToken ct = default)
    {
        if (_peers.Remove(peerId, out var entry))
            entry.ObjRef.Dispose();

        await _js.InvokeVoidAsync("webrtcClose", ct, peerId, new { suppressDotNetCallbacks = suppressCallbacks });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peerId in _peers.Keys.ToList())
            await CloseAsync(peerId, suppressCallbacks: true);

        _peers.Clear();
    }

    private sealed record PeerEntry(JsWebRtcCallbackBridge Bridge, DotNetObjectReference<JsWebRtcCallbackBridge> ObjRef);

    public sealed class JsWebRtcCallbackBridge
    {
        private readonly IWebRtcTransportCallbacks _callbacks;

        public JsWebRtcCallbackBridge(IWebRtcTransportCallbacks callbacks) => _callbacks = callbacks;

        [JSInvokable]
        public Task OnIceCandidate(string peerId, string candidateJson) =>
            _callbacks.OnIceCandidateAsync(peerId, candidateJson);

        [JSInvokable]
        public Task OnDataChannelOpen(string peerId) =>
            _callbacks.OnDataChannelOpenAsync(peerId);

        [JSInvokable]
        public void OnWebRtcConnectionStateChange(string peerId, string state) =>
            _callbacks.OnConnectionStateChange(peerId, state);

        [JSInvokable]
        public Task OnDataReceived(string peerId, string data) =>
            _callbacks.OnDataReceivedAsync(peerId, data);

        [JSInvokable]
        public void OnDataChannelClose(string peerId) =>
            _callbacks.OnDataChannelClose(peerId);
    }
}