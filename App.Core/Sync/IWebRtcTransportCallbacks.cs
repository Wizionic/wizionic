namespace App.Core.Sync;

/// <summary>
/// Callbacks from a platform WebRTC transport (browser JS, SIPSorcery, etc.).
/// </summary>
public interface IWebRtcTransportCallbacks
{
    Task OnIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default);
    Task OnDataChannelOpenAsync(string peerId, CancellationToken ct = default);
    void OnConnectionStateChange(string peerId, string state);
    Task OnDataReceivedAsync(string peerId, string data, CancellationToken ct = default);
    void OnDataChannelClose(string peerId);
}