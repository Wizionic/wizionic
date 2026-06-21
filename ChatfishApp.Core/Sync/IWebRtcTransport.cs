namespace ChatfishApp.Core.Sync;

/// <summary>
/// Platform-specific WebRTC peer connection + data channel operations.
/// Signaling (offer/answer/ICE relay) stays in the sync service; only media transport is abstracted.
/// </summary>
public interface IWebRtcTransport
{
    Task CreatePeerConnectionAsync(string peerId, IWebRtcTransportCallbacks callbacks, CancellationToken ct = default);
    Task CreateDataChannelAsync(string peerId, string label, CancellationToken ct = default);
    Task<string?> CreateOfferAsync(string peerId, CancellationToken ct = default);
    Task<string?> CreateAnswerAsync(string peerId, CancellationToken ct = default);
    Task<bool> SetRemoteDescriptionAsync(string peerId, string sdpJson, CancellationToken ct = default);
    Task AddIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default);
    Task<bool> SendDataAsync(string peerId, string data, CancellationToken ct = default);
    Task<bool> IsDataChannelOpenAsync(string peerId, CancellationToken ct = default);
    Task<int> GetMaxMessageSizeAsync(string peerId, CancellationToken ct = default);
    Task CloseAsync(string peerId, bool suppressCallbacks = false, CancellationToken ct = default);
}