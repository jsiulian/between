using System.Text.Json;
using SIPSorcery.Net;

namespace chatxp.core;

/// <summary>
/// Manages the peer connection, data channel, and signaling handshake — a port of
/// chat/WebRTCManager.swift on SIPSorcery (pure C# WebRTC; no native deps).
///
/// Flow mirrored from the Swift original:
///   - Local id = our public key identity (the CryptoKit rawRepresentation Base64).
///   - connect(to): fresh RTCPeerConnection + "events" data channel (ordered), create offer,
///     setLocalDescription, send offer over signaling. 15s timeout while connecting.
///   - Incoming offer: exposed via OnIncomingConnection (app decides accept/reject; the
///     manager auto-rejects by sending "reject" only for blocked peers — caller checks).
///   - Answer: setRemoteDescription(offer), createAnswer, send answer.
///   - Candidates: forwarded as they are generated (onicecandidate); remote candidates
///     buffered until the remote description is set, then drained.
///   - Once the ICE/data path is live the signaling socket is closed (drop the server).
///   - Events (send/delete/typing/endCall) queue while the channel is opening and flush
///     when it opens. Received frames are decoded as Event and raised via OnMessage.
/// </summary>
public sealed class WebRtcManager : IAsyncDisposable
{
    /// <summary>STUN server, mirrors WebRTCManager.swift (iceServers field).</summary>
    private const string StunServer = "stun:stun.l.google.com:19302";
    private const string DataChannelLabel = "events";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly SignalingClient _signaling;
    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _dataChannel;
    private string? _targetId;
    private readonly List<RTCIceCandidateInit> _pendingRemoteCandidates = new();
    private readonly List<Event> _pendingEvents = new();
    private CancellationTokenSource? _connectTimeoutCts;
    private bool _isPeerConnected;
    private readonly object _stateLock = new();

    /// <summary>Our identity on the signaling server (the Base64 public key).</summary>
    public string LocalId { get; }

    /// <summary>Data channel established and open — mirrors isPeerConnected.</summary>
    public bool IsPeerConnected => Volatile.Read(ref _isPeerConnected);

    /// <summary>ICE connected AND data channel open — mirrors isReadyToSend.
    /// (SIPSorcery has no "completed" ICE state; "connected" covers it.)</summary>
    public bool IsReadyToSend =>
        _peerConnection is not null &&
        _peerConnection.iceConnectionState == RTCIceConnectionState.connected &&
        _dataChannel is not null && _dataChannel.readyState == RTCDataChannelState.open;

    /// <summary>Raised for every Event received on the data channel (decrypted payloads
    /// still need handling by the app via E2eeCrypto). Background thread.</summary>
    public event Action<Event>? OnMessage;

    /// <summary>Raised with the state name ("connecting", "connected", "failed",
    /// "disconnected", "closed", "not_found", "rejected", "timeout").</summary>
    public event Action<string>? OnConnectionStateChanged;

    /// <summary>An offer arrived from fromId. The app must call AcceptIncoming(fromId) or
    /// RejectIncoming(fromId). Mirrors the Swift connectionDecisionCallback + alert flow.</summary>
    public event Action<string, string>? OnIncomingConnection;

    /// <summary>The connection failed / was rejected / the peer went away.</summary>
    public event Action<string>? OnDisconnected;

    public WebRtcManager(string localId, SignalingClient signaling)
    {
        LocalId = localId;
        _signaling = signaling;
        _signaling.OnMessage += HandleSignaling;
    }

    /// <summary>Connects and registers on the signaling server (mirrors register()).</summary>
    public Task RegisterAsync(CancellationToken cancellationToken = default) =>
        _signaling.EnsureConnectedAndRegisteredAsync(LocalId, cancellationToken);

    /// <summary>Sets the signaling server URL. Mirrors setSignalingServer(_:): if the
    /// URL actually changed, the old socket is closed so the next RegisterAsync
    /// connects to the new server (the Swift app closes the session on change too).</summary>
    public void SetSignalingServer(string url)
    {
        var changed = !string.Equals(
            url.TrimEnd('/'), _signaling.ServerUri.ToString().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
        _signaling.SetSignalingServer(url);
        if (changed)
        {
            // Old socket belongs to the old server; drop it (register reconnects).
            _ = _signaling.CloseSocketAsync();
        }
    }

    /// <summary>
    /// Initiates a P2P connection to userId. Mirrors connect(toUserId:): fresh connection
    /// state, offer, 15s timeout.
    /// </summary>
    public async Task ConnectToAsync(string userId, CancellationToken cancellationToken = default)
    {
        CancelConnectTimeout();
        lock (_stateLock)
        {
            TeardownConnection(keepSignaling: true);
            _targetId = userId;
        }

        OnConnectionStateChanged?.Invoke("connecting");

        await RegisterAsync(cancellationToken).ConfigureAwait(false);

        _peerConnection = CreatePeerConnection();
        _dataChannel = await _peerConnection.createDataChannel(
            DataChannelLabel, new RTCDataChannelInit { ordered = true }).ConfigureAwait(false);
        HookDataChannel(_dataChannel);

        // 15s timeout (Swift uses DispatchQueue.asyncAfter +5s/15s; fail → handleConnectionFailed).
        _connectTimeoutCts = new CancellationTokenSource();
        _ = Task.Delay(ConnectTimeout, _connectTimeoutCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !IsPeerConnected)
                HandleConnectionFailed("Connection attempt timed out");
        }, TaskScheduler.Default);

        var offer = _peerConnection.createOffer(null);
        await _peerConnection.setLocalDescription(offer).ConfigureAwait(false);
        await _signaling.SendAsync(
            SignalingMessage.Offer(LocalId, userId, offer.sdp), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accepts a pending incoming connection (mirrors acceptOffer).</summary>
    public async Task AcceptIncomingAsync(string fromId, CancellationToken cancellationToken = default)
    {
        var offerSdp = _pendingOfferSdps[fromId];
        _pendingOfferSdps.Remove(fromId);

        lock (_stateLock)
        {
            TeardownConnection(keepSignaling: true);
            _targetId = fromId;
        }
        OnConnectionStateChanged?.Invoke("connecting");

        await RegisterAsync(cancellationToken).ConfigureAwait(false);

        _peerConnection = CreatePeerConnection();
        _peerConnection.ondatachannel += ch => HookDataChannel(ch);

        var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp,
        });
        if (result != SetDescriptionResultEnum.OK)
        {
            HandleConnectionFailed("Invalid offer");
            return;
        }

        DrainPendingCandidates();

        var answer = _peerConnection.createAnswer(null);
        await _peerConnection.setLocalDescription(answer).ConfigureAwait(false);
        await _signaling.SendAsync(SignalingMessage.Answer(LocalId, fromId, answer.sdp),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rejects a pending incoming connection (mirrors the reject alert action).</summary>
    public Task RejectIncomingAsync(string fromId, CancellationToken cancellationToken = default)
    {
        _pendingOfferSdps.Remove(fromId);
        return _signaling.SendAsync(SignalingMessage.Reject(LocalId, fromId), cancellationToken);
    }

    private readonly Dictionary<string, string> _pendingOfferSdps = new();

    /// <summary>Sends an Event to the peer; queues it while the channel is opening
    /// (mirrors send(_:)).</summary>
    public void Send(Event evt)
    {
        lock (_stateLock)
        {
            if (_dataChannel is not { readyState: RTCDataChannelState.open })
            {
                _pendingEvents.Add(evt);
                return;
            }
        }

        var json = JsonSerializer.Serialize(evt);
        _dataChannel!.send(json);
    }

    /// <summary>Tears down the peer connection and signaling (mirrors disconnect()).</summary>
    public async Task DisconnectAsync()
    {
        CancelConnectTimeout();
        lock (_stateLock)
        {
            TeardownConnection(keepSignaling: false);
            _targetId = null;
        }
        _isPeerConnected = false;
        OnDisconnected?.Invoke("disconnected");
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- private

    private RTCPeerConnection CreatePeerConnection()
    {
        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = StunServer },
            },
        };
        var pc = new RTCPeerConnection(config);

        pc.onicecandidate += candidate =>
        {
            if (_targetId is not null && candidate is not null)
            {
                _ = _signaling.SendAsync(SignalingMessage.Candidate(
                    LocalId, _targetId,
                    candidate.candidate, candidate.sdpMid ?? "0",
                    candidate.sdpMLineIndex));
            }
        };

        pc.onconnectionstatechange += state =>
        {
            OnConnectionStateChanged?.Invoke(state.ToString().ToLowerInvariant());
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    HandlePeerBecameReachable();
                    break;
                case RTCPeerConnectionState.failed:
                case RTCPeerConnectionState.closed:
                    _isPeerConnected = false;
                    break;
                // disconnected: can be temporary (network switch); don't boot (mirrors Swift).
            }
        };

        pc.oniceconnectionstatechange += state =>
        {
            if (state == RTCIceConnectionState.connected)
                HandlePeerBecameReachable();
        };

        return pc;
    }

    private void HookDataChannel(RTCDataChannel channel)
    {
        _dataChannel = channel;
        channel.onopen += () =>
        {
            HandlePeerBecameReachable();
            // Flush the queue of Events that piled up while connecting (mirrors Swift).
            Event[] queued;
            lock (_stateLock)
            {
                queued = _pendingEvents.ToArray();
                _pendingEvents.Clear();
            }
            foreach (var evt in queued)
                Send(evt);
        };
        channel.onmessage += (dc, protocol, data) =>
        {
            try
            {
                // Swift sends the Event JSON as a string frame; we accept both string
                // and binary payloads (both are UTF-8 JSON here).
                var json = System.Text.Encoding.UTF8.GetString(data, 0, data.Length);
                var evt = JsonSerializer.Deserialize<Event>(json);
                if (evt is not null)
                    OnMessage?.Invoke(evt);
            }
            catch (JsonException)
            {
                // Mirrors Swift: "received undecodable data channel message" — drop it.
            }
        };
        channel.onclose += () =>
        {
            _isPeerConnected = false;
        };
    }

    private void HandlePeerBecameReachable()
    {
        lock (_stateLock)
        {
            if (_isPeerConnected) return;
            _isPeerConnected = true;
        }
        CancelConnectTimeout();
        // The P2P path is live — drop the signaling server (mirrors closeSignalingSocket()).
        _ = _signaling.CloseSocketAsync();
        OnConnectionStateChanged?.Invoke("connected");
    }

    private void HandleConnectionFailed(string reason)
    {
        CancelConnectTimeout();
        lock (_stateLock)
        {
            TeardownConnection(keepSignaling: true);
            _targetId = null;
        }
        _isPeerConnected = false;
        OnConnectionStateChanged?.Invoke("failed");
        OnDisconnected?.Invoke(reason);
    }

    private void HandleSignaling(SignalingMessage message)
    {
        switch (message.Type)
        {
            case "offer":
                if (message.Sdp is null || message.From is null) return;
                _pendingOfferSdps[message.From] = message.Sdp;
                OnIncomingConnection?.Invoke(message.From, message.Sdp);
                break;

            case "reject":
                HandleConnectionFailed("Connection attempt rejected");
                break;

            case "not_found":
                HandleConnectionFailed("User not found on signaling server");
                break;

            case "answer":
            {
                if (message.Sdp is null || _peerConnection is null) return;
                var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = message.Sdp,
                });
                if (result == SetDescriptionResultEnum.OK)
                    DrainPendingCandidates();
                break;
            }

            case "candidate":
            {
                if (message.Sdp is null || message.SdpMLineIndex is null) return;
                var candidate = new RTCIceCandidateInit
                {
                    candidate = message.Sdp,
                    sdpMid = message.SdpMid,
                    sdpMLineIndex = (ushort)message.SdpMLineIndex,
                };
                if (_peerConnection?.RemoteDescription != null)
                    _peerConnection.addIceCandidate(candidate);
                else
                    _pendingRemoteCandidates.Add(candidate);
                break;
            }
        }
    }

    private void DrainPendingCandidates()
    {
        if (_peerConnection is null) return;
        foreach (var candidate in _pendingRemoteCandidates)
            _peerConnection.addIceCandidate(candidate);
        _pendingRemoteCandidates.Clear();
    }

    private void TeardownConnection(bool keepSignaling)
    {
        _dataChannel?.close();
        _dataChannel = null;
        if (_peerConnection is not null)
        {
            try { _peerConnection.Close("teardown"); } catch { /* ignore */ }
            _peerConnection = null;
        }
        _pendingRemoteCandidates.Clear();
        _pendingEvents.Clear();
    }

    private void CancelConnectTimeout()
    {
        _connectTimeoutCts?.Cancel();
        _connectTimeoutCts?.Dispose();
        _connectTimeoutCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        _signaling.OnMessage -= HandleSignaling;
        CancelConnectTimeout();
        lock (_stateLock)
        {
            TeardownConnection(keepSignaling: false);
        }
        await _signaling.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>Test hook: whether the signaling socket is still connected.</summary>
    internal bool SignalingConnected => _signaling.IsConnected;
}
