using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace chatxp.core;

/// <summary>
/// The signaling message shape, mirroring SignalingMessage in chat/WebRTCManager.swift:
///   { "type": "register"|"offer"|"answer"|"candidate", "from": ..., "to": ...,
///     "sdp": ..., "sdpMLineIndex": ..., "sdpMid": ... }
/// Null fields are omitted (Swift encodeIfPresent), keys are camelCase.
/// The server (server/index.js) forwards the message verbatim by "to" or replies
/// { "type": "not_found", "from": &lt;target&gt;, "to": &lt;sender&gt; }.
/// </summary>
public sealed class SignalingMessage
{
    /// <summary>"register", "offer", "answer", "candidate" (client->server),
    /// "reject" is client->client via the server; "not_found" is server->client.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; init; }

    [JsonPropertyName("sdp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sdp { get; init; }

    [JsonPropertyName("sdpMLineIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SdpMLineIndex { get; init; }

    [JsonPropertyName("sdpMid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SdpMid { get; init; }

    public static SignalingMessage Register(string from) =>
        new() { Type = "register", From = from };

    public static SignalingMessage Offer(string from, string to, string sdp) =>
        new() { Type = "offer", From = from, To = to, Sdp = sdp };

    public static SignalingMessage Answer(string from, string to, string sdp) =>
        new() { Type = "answer", From = from, To = to, Sdp = sdp };

    public static SignalingMessage Candidate(string from, string to, string sdp,
        string sdpMid, int sdpMLineIndex) =>
        new() { Type = "candidate", From = from, To = to, Sdp = sdp,
                SdpMid = sdpMid, SdpMLineIndex = sdpMLineIndex };

    public static SignalingMessage Reject(string from, string to) =>
        new() { Type = "reject", From = from, To = to };
}

/// <summary>
/// WebSocket client for the existing Node.js signaling server (server/index.js).
/// The server is a dumb relay: it registers clients by "from" and forwards all other
/// messages verbatim to the socket registered under "to", replying "not_found"
/// when the target is absent. This class reproduces WebRTCManager's socket behavior:
/// connect, register, send, receive loop, reconnect-on-demand, and close.
/// </summary>
public sealed class SignalingClient : IAsyncDisposable
{
    public const string DefaultServer = "wss://150.230.37.157:3030/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Mirror Swift's encodeIfPresent: omit nulls. DefaultWrite (no indentation).
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Uri _serverUri = new(DefaultServer);

    /// <summary>Raised for every message received from the server (offers, answers,
    /// candidates, reject, not_found). Raised on a background thread.</summary>
    public event Action<SignalingMessage>? OnMessage;

    /// <summary>Raised when the underlying socket closes or drops.</summary>
    public event Action? OnDisconnected;

    public Uri ServerUri => _serverUri;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>Configure the signaling server URL. Mirrors setSignalingServer:
    /// must be ws:// or wss://. Note: if the socket is already open this does NOT
    /// reconnect — callers switch by changing the URL and registering again
    /// (WebRtcManager closes the socket on URL change, mirroring the Swift app).</summary>
    public void SetSignalingServer(string url)
    {
        var uri = new Uri(url, UriKind.Absolute);
        if (uri.Scheme != "ws" && uri.Scheme != "wss")
            throw new ArgumentException("Signaling server URL must use ws:// or wss://.", nameof(url));
        _serverUri = uri;
    }

    /// <summary>
    /// Connects to the signaling server (if not already connected) and registers
    /// this client id so other peers can reach us. Mirrors WebRTCManager.register().
    /// </summary>
    public async Task RegisterAsync(string clientId, CancellationToken cancellationToken = default)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
        {
            await CloseSocketAsync().ConfigureAwait(false);
            _socket = new ClientWebSocket();
            await _socket.ConnectAsync(_serverUri, cancellationToken).ConfigureAwait(false);
            StartReceiveLoop();
        }

        await SendAsync(SignalingMessage.Register(clientId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Ensures a live registered connection (mirrors the register() guard in connect()).</summary>
    public Task EnsureConnectedAndRegisteredAsync(string clientId, CancellationToken cancellationToken = default) =>
        RegisterAsync(clientId, cancellationToken);

    /// <summary>Sends a signaling message (offer/answer/candidate/reject) via the server.
    /// The server forwards it to the client registered under message.To.</summary>
    public Task SendAsync(SignalingMessage message, CancellationToken cancellationToken = default) =>
        SendRawAsync(JsonSerializer.Serialize(message, JsonOptions), cancellationToken);

    private async Task SendRawAsync(string json, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("Signaling socket is not connected.");

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StartReceiveLoop()
    {
        _receiveCts = new CancellationTokenSource();
        var socket = _socket!;
        var token = _receiveCts.Token;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, token), token);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", ct)
                            .ConfigureAwait(false);
                        OnDisconnected?.Invoke();
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                var json = sb.ToString();
                SignalingMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<SignalingMessage>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    continue; // Ignore malformed payloads (server sends raw forwards only).
                }
                if (message is not null)
                    OnMessage?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (WebSocketException)
        {
            OnDisconnected?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Force-closes the signaling socket (mirrors closeSignalingSocket).</summary>
    public async Task CloseSocketAsync()
    {
        _receiveCts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); }
            catch { /* ignore */ }
            _receiveLoop = null;
        }
        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing",
                        CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* ignore */ }
            _socket.Dispose();
            _socket = null;
        }
        _receiveCts?.Dispose();
        _receiveCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseSocketAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
