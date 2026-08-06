using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Sync;
using SIPSorcery.Net;

namespace App.Maui.Services;

/// <summary>
/// MAUI WebRTC transport via SIPSorcery. SDP/ICE JSON matches browser RTCSessionDescriptionInit / RTCIceCandidateInit.
/// </summary>
public sealed class SipsorceryWebRtcTransport : IWebRtcTransport, IAsyncDisposable
{
    private const string StunUrl = "stun:stun.l.google.com:19302";
    private const int DefaultMaxMessageSize = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ConcurrentDictionary<string, PeerEntry> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _peerGate = new(1, 1);

    public async Task CreatePeerConnectionAsync(string peerId, IWebRtcTransportCallbacks callbacks, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            await ClosePeerInternalAsync(peerId, suppressCallbacks: true, ct);

            var config = new RTCConfiguration
            {
                iceServers = [new RTCIceServer { urls = StunUrl }]
            };

            var pc = new RTCPeerConnection(config);
            var entry = new PeerEntry(pc, callbacks);
            _peers[peerId] = entry;

            pc.onicecandidate += iceCandidate =>
            {
                if (iceCandidate == null || entry.SuppressCallbacks) return;
                try
                {
                    var json = iceCandidate.toJSON();
                    _ = callbacks.OnIceCandidateAsync(peerId, json, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SipsorceryWebRtc] ICE callback failed for {peerId}: {ex.Message}");
                }
            };

            pc.ondatachannel += dc =>
            {
                entry.DataChannel = dc;
                WireDataChannel(peerId, entry, dc);
            };

            pc.onconnectionstatechange += state =>
            {
                if (entry.SuppressCallbacks) return;

                var mapped = state switch
                {
                    RTCPeerConnectionState.failed => "failed",
                    RTCPeerConnectionState.disconnected => "disconnected",
                    RTCPeerConnectionState.closed => "closed",
                    _ => null
                };

                if (mapped != null)
                    callbacks.OnConnectionStateChange(peerId, mapped);
            };
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task CreateDataChannelAsync(string peerId, string label, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            if (!_peers.TryGetValue(peerId, out var entry))
                throw new InvalidOperationException($"No peer connection for {peerId}");

            var dc = await entry.Pc.createDataChannel(label, null);
            entry.DataChannel = dc;
            WireDataChannel(peerId, entry, dc);
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task<string?> CreateOfferAsync(string peerId, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            if (!_peers.TryGetValue(peerId, out var entry))
                return null;

            var offer = entry.Pc.createOffer(null);
            await entry.Pc.setLocalDescription(offer);
            return SerializeSessionDescription(offer);
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task<string?> CreateAnswerAsync(string peerId, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            if (!_peers.TryGetValue(peerId, out var entry))
                return null;

            var answer = entry.Pc.createAnswer();
            await entry.Pc.setLocalDescription(answer);
            return SerializeSessionDescription(answer);
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task<bool> SetRemoteDescriptionAsync(string peerId, string sdpJson, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            if (!_peers.TryGetValue(peerId, out var entry))
                return false;

            var desc = JsonSerializer.Deserialize<RTCSessionDescriptionInit>(sdpJson, JsonOpts);
            if (desc == null)
                return false;

            if (desc.type == RTCSdpType.answer
                && entry.Pc.signalingState == RTCSignalingState.stable
                && entry.RemoteDescSet)
            {
                return false;
            }

            var result = entry.Pc.setRemoteDescription(desc);
            if (result != SetDescriptionResultEnum.OK)
            {
                Console.WriteLine($"[SipsorceryWebRtc] setRemoteDescription failed for {peerId}: {result}");
                return false;
            }

            entry.RemoteDescSet = true;
            await FlushIceQueueAsync(entry);
            return true;
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async Task AddIceCandidateAsync(string peerId, string candidateJson, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            if (!_peers.TryGetValue(peerId, out var entry))
            {
                Console.WriteLine($"[SipsorceryWebRtc] addIceCandidate: no connection for {peerId}");
                return;
            }

            if (!entry.RemoteDescSet)
            {
                entry.IceQueue.Add(candidateJson);
                return;
            }

            await AddIceCandidateInternalAsync(entry, candidateJson);
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public Task<bool> SendDataAsync(string peerId, string data, CancellationToken ct = default)
    {
        if (!_peers.TryGetValue(peerId, out var entry))
            return Task.FromResult(false);

        var dc = entry.DataChannel;
        if (dc == null || !dc.IsOpened)
            return Task.FromResult(false);

        try
        {
            dc.send(data);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SipsorceryWebRtc] send failed for {peerId}: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsDataChannelOpenAsync(string peerId, CancellationToken ct = default) =>
        Task.FromResult(_peers.TryGetValue(peerId, out var entry) && entry.DataChannel?.IsOpened == true);

    public Task<int> GetMaxMessageSizeAsync(string peerId, CancellationToken ct = default)
    {
        if (_peers.TryGetValue(peerId, out var entry))
        {
            var max = entry.Pc.sctp?.maxMessageSize ?? 0;
            if (max > 0)
                return Task.FromResult((int)max);
        }

        return Task.FromResult(DefaultMaxMessageSize);
    }

    public async Task CloseAsync(string peerId, bool suppressCallbacks = false, CancellationToken ct = default)
    {
        await _peerGate.WaitAsync(ct);
        try
        {
            await ClosePeerInternalAsync(peerId, suppressCallbacks, ct);
        }
        finally
        {
            _peerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _peerGate.WaitAsync();
        try
        {
            foreach (var peerId in _peers.Keys.ToList())
                await ClosePeerInternalAsync(peerId, suppressCallbacks: true, CancellationToken.None);

            _peers.Clear();
        }
        finally
        {
            _peerGate.Release();
            _peerGate.Dispose();
        }
    }

    private void WireDataChannel(string peerId, PeerEntry entry, RTCDataChannel dc)
    {
        dc.onopen += () =>
        {
            if (!entry.SuppressCallbacks)
                _ = entry.Callbacks.OnDataChannelOpenAsync(peerId, CancellationToken.None);
        };

        dc.onmessage += (channel, proto, data) =>
        {
            if (!entry.SuppressCallbacks
                && proto is DataChannelPayloadProtocols.WebRTC_String or DataChannelPayloadProtocols.WebRTC_String_Empty)
            {
                var text = Encoding.UTF8.GetString(data);
                _ = entry.Callbacks.OnDataReceivedAsync(peerId, text, CancellationToken.None);
            }
        };

        dc.onclose += () =>
        {
            if (!entry.SuppressCallbacks)
                entry.Callbacks.OnDataChannelClose(peerId);
        };
    }

    private static string SerializeSessionDescription(RTCSessionDescriptionInit desc) =>
        JsonSerializer.Serialize(desc, JsonOpts);

    private static async Task AddIceCandidateInternalAsync(PeerEntry entry, string candidateJson)
    {
        try
        {
            var candidate = JsonSerializer.Deserialize<RTCIceCandidateInit>(candidateJson, JsonOpts);
            if (candidate != null)
                entry.Pc.addIceCandidate(candidate);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SipsorceryWebRtc] addIceCandidate parse failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task FlushIceQueueAsync(PeerEntry entry)
    {
        if (entry.IceQueue.Count == 0)
            return;

        var queued = entry.IceQueue.ToList();
        entry.IceQueue.Clear();

        foreach (var candJson in queued)
            await AddIceCandidateInternalAsync(entry, candJson);
    }

    private Task ClosePeerInternalAsync(string peerId, bool suppressCallbacks, CancellationToken ct)
    {
        if (!_peers.TryRemove(peerId, out var entry))
            return Task.CompletedTask;

        entry.SuppressCallbacks = suppressCallbacks;

        try
        {
            entry.DataChannel?.close();
        }
        catch { }

        try
        {
            if (!entry.Pc.IsClosed)
                entry.Pc.Close("sync closed");
        }
        catch { }

        return Task.CompletedTask;
    }

    private sealed class PeerEntry(RTCPeerConnection pc, IWebRtcTransportCallbacks callbacks)
    {
        public RTCPeerConnection Pc { get; } = pc;
        public IWebRtcTransportCallbacks Callbacks { get; } = callbacks;
        public RTCDataChannel? DataChannel { get; set; }
        public bool RemoteDescSet { get; set; }
        public List<string> IceQueue { get; } = [];
        public bool SuppressCallbacks { get; set; }
    }
}