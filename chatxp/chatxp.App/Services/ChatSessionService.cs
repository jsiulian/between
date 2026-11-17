using System.Collections.ObjectModel;
using chatxp.core;
using chatxp.core.Storage;

namespace chatxp.App.Services;

/// <summary>
/// Composes the whole core stack into one app-level service: identity key, WebRTC
/// manager, signaling, local database. Exposes observable UI state (connected peer,
/// decrypted conversation, contact/address book). This is to chatxp.App what
/// chatApp.swift + the environment objects are to the iOS app.
///
/// Identity persistence (v1): the private key is stored as an encrypted-at-rest PKCS#8
/// blob next to the database in the OS app-data directory with user-level file
/// permissions (per business-requirements.md NFR-3 — hardware-backed storage is out
/// of scope; on macOS the file lives under ~/Library/Application Support).
/// </summary>
public sealed class ChatSessionService : IDisposable
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "chatxp");

    private const string KeyFileName = "identity.key";
    private const string DbFileName = "chatxp.db";
    private const string SettingsFileName = "settings.txt";

    public E2eeCrypto Crypto { get; }
    public ChatDatabase Database { get; }
    public WebRtcManager WebRtc { get; }
    public DiagnosticLog Log { get; } = new();

    /// <summary>The signaling server currently in use (persisted across restarts).</summary>
    public string SignalingServer { get; private set; }

    /// <summary>Base64 identity (public key) of the connected peer, if any.</summary>
    public string? ConnectedPeerId { get; private set; }

    private readonly string _keyPath = Path.Combine(AppDataDir, KeyFileName);
    private readonly string _settingsPath = Path.Combine(AppDataDir, SettingsFileName);

    public ChatSessionService(string? signalingServer = null)
    {
        Directory.CreateDirectory(AppDataDir);

        Crypto = LoadOrCreateIdentity();
        Database = new ChatDatabase(Path.Combine(AppDataDir, DbFileName));

        SignalingServer = signalingServer
            ?? System.Environment.GetEnvironmentVariable("CHATXP_SIGNAL_SERVER")
            ?? LoadSavedSignalingServer()
            ?? SignalingClient.DefaultServer;

        WebRtc = new WebRtcManager(Crypto.Base64PublicKey, new SignalingClient(), Log);
        WebRtc.SetSignalingServer(SignalingServer);
        WebRtc.OnMessage += HandleIncomingEvent;
        WebRtc.OnPeerOpened += ReplayOpsForPeer;

        Log.Info($"chatxp starting — id {WebRtcLocalPeerId()}…");
        Log.Info($"Signaling server: {SignalingServer}");
        try
        {
            _ = WebRtc.RegisterAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Initial signaling registration failed", ex);
        }
    }

    /// <summary>
    /// Switches the signaling server at runtime (Settings/Connect "Apply"). Validates the
    /// URL, persists the choice, closes the old socket, and registers on the new server.
    /// Returns null on success or an error message on failure — the caller surfaces it.
    /// </summary>
    public async Task<string?> ApplySignalingServerAsync(string url)
    {
        var trimmed = url.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "Enter a server URL (ws:// or wss://).";

        try
        {
            // Validate scheme before touching state (throws on bad URL/scheme).
            WebRtc.SetSignalingServer(trimmed);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        SignalingServer = trimmed;
        SaveSignalingServer(trimmed);

        try
        {
            await WebRtc.RegisterAsync();
            Log.Info($"Registered on signaling server {trimmed}");
            return null; // applied
        }
        catch (Exception ex)
        {
            Log.Error($"Could not register on signaling server {trimmed}", ex);
            return $"Could not connect: {ex.Message}";
        }
    }

    private string? LoadSavedSignalingServer()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var saved = File.ReadAllText(_settingsPath).Trim();
                if (!string.IsNullOrEmpty(saved)) return saved;
            }
        }
        catch { /* unreadable settings fall back to default */ }
        return null;
    }

    private void SaveSignalingServer(string url)
    {
        try { File.WriteAllText(_settingsPath, url); }
        catch { /* best-effort persistence */ }
    }

    /// <summary>Sends a text message to the connected peer (encrypt → send Event).</summary>
    public (Event wireEvent, string localMessageId)? SendText(string plaintext, string? replyingTo = null)
    {
        if (ConnectedPeerId is null) return null;
        var ciphertext = Crypto.Encrypt(plaintext, ConnectedPeerId);
        if (ciphertext is null) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var evt = Event.SendEvent(ciphertext, replyingTo, Crypto.Base64PublicKey, now);
        WebRtc.Send(evt);

        // Store our own outgoing message (encrypted content, like SwiftData in the app).
        var message = new chatxp.core.Storage.Message(
            Guid.NewGuid().ToString(), ciphertext, Crypto.Base64PublicKey,
            ConnectedPeerId, now, evt.Id, replyingTo, Crypto.Base64PublicKey, now);
        Database.AddMessage(message);
        return (evt, message.Id);
    }

    /// <summary>Requests deletion of a message on both sides. The tombstone is kept
    /// locally so a later replay cannot bring the message back.</summary>
    public void DeleteMessage(string eventId, string localMessageId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var evt = Event.DeleteEvent(eventId, Crypto.Base64PublicKey, now);
        var row = Database.GetMessageByEventId(eventId);
        var peer = row is null
            ? ConnectedPeerId ?? string.Empty
            : row.From == Crypto.Base64PublicKey ? row.To : row.From;

        if (ConnectedPeerId is not null) WebRtc.Send(evt);
        Database.AddTombstone(new Tombstone(evt.Id, eventId,
            Crypto.Base64PublicKey, peer, Crypto.Base64PublicKey, now));
        Database.HideMessage(localMessageId);
    }

    /// <summary>Local-only delete: hides the message but keeps the op for replay safety.</summary>
    public void DeleteForYou(string localMessageId) => Database.HideMessage(localMessageId);

    /// <summary>Local-only clear: hidden rows stay hidden when old ops arrive on replay.</summary>
    public void ClearConversation(string peerId) =>
        Database.ClearConversation(Crypto.Base64PublicKey, peerId);

    /// <summary>Initiates a connection to a peer id (Connect flow).</summary>
    public Task ConnectToPeerAsync(string peerId) =>
        WebRtc.ConnectToAsync(peerId);

    /// <summary>
    /// Decrypts stored ciphertext for display. Returns null for tampered/foreign rows.
    /// </summary>
    public string? DecryptFor(string encryptedContent, string counterpartId) =>
        Crypto.Decrypt(encryptedContent, counterpartId);

    /// <summary>Sends every local op for the peer when the channel opens. Replay uses the
    /// original event ids, so both peers converge even if messages were sent while apart.</summary>
    private void ReplayOpsForPeer(string peer)
    {
        if (string.IsNullOrEmpty(peer)) peer = ConnectedPeerId ?? string.Empty;
        if (string.IsNullOrEmpty(peer)) return;

        var ops = Database.GetOpsForPeer(Crypto.Base64PublicKey, peer);
        foreach (var op in ops)
            WebRtc.Send(ChatCrdt.ToEvent(op));
        Log.Info($"CRDT replay: {ops.Count} op(s) sent to {peer[..8]}…");
    }

    private void HandleIncomingEvent(Event evt)
    {
        if (ConnectedPeerId is null && evt.Type != EventTypes.Typing)
        {
            // Events can arrive before the app marks the peer; accepts are peer-bound.
        }

        var peer = WebRtcLocalPeerId();
        switch (evt.Type)
        {
            case EventTypes.Send:
            case EventTypes.Delete:
            {
                if (Database.HasEvent(evt.Id)) return; // idempotent merge
                var op = ChatCrdt.FromEvent(evt, peer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (op.Kind == OpKind.Send)
                {
                    var plaintext = Crypto.Decrypt(evt.Payload, peer);
                    if (plaintext is null) return; // tampered — drop (FR-4.2)
                    var message = new chatxp.core.Storage.Message(
                        Guid.NewGuid().ToString(), evt.Payload, peer,
                        Crypto.Base64PublicKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        evt.Id, evt.ReplyingTo, op.Author, op.TsMs);
                    Database.AddMessage(message);
                    MessageReceived?.Invoke((message, plaintext));
                }
                else
                {
                    Database.AddTombstone(new Tombstone(evt.Id, evt.Payload,
                        peer, Crypto.Base64PublicKey, op.Author, op.TsMs));
                    MessageDeleted?.Invoke(evt.Payload);
                }
                break;
            }
            case EventTypes.Typing:
                PeerTyping?.Invoke();
                break;
        }
    }

    private string WebRtcLocalPeerId()
    {
        // The remote peer is whoever the manager is connected to.
        return ConnectedPeerId ?? string.Empty;
    }

    /// <summary>Raised when a decrypted message arrives: (local row, plaintext).</summary>
    public event Action<(Message row, string plaintext)>? MessageReceived;

    /// <summary>Raised when the peer deleted a message (payload = remote event id).</summary>
    public event Action<string>? MessageDeleted;

    /// <summary>Raised when the peer's typing indicator fires.</summary>
    public event Action? PeerTyping;

    /// <summary>Marks the connected peer (called when accept/connect completes).</summary>
    public void SetConnectedPeer(string peerId) => ConnectedPeerId = peerId;

    private E2eeCrypto LoadOrCreateIdentity()
    {
        // Per NFR-3: OS-protected store equivalent for v1: app-data file
        // (user-level permissions; document limitation in settings UI).
        var keyPath = Path.Combine(AppDataDir, KeyFileName);
        if (File.Exists(keyPath))
        {
            try
            {
                var loaded = E2eeCrypto.FromIdentityFile(keyPath);
                if (loaded is not null) return loaded;
            }
            catch { /* fall through: regenerate */ }
        }

        var crypto = E2eeCrypto.Create();
        crypto.SaveIdentityFile(keyPath);
        return crypto;
    }

    public void Dispose()
    {
        WebRtc.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        Database.Dispose();
    }
}

/// <summary>Collection of decrypted chat lines for one conversation.</summary>
public sealed record ChatLine(string Id, string? EventId, string Text, bool Mine, long TimestampMs, string? ReplyingTo);
