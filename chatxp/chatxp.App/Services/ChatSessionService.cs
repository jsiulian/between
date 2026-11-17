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

        // Precedence: explicit argument > CHATXP_SIGNAL_SERVER env > saved setting > default.
        SignalingServer = signalingServer
            ?? System.Environment.GetEnvironmentVariable("CHATXP_SIGNAL_SERVER")
            ?? LoadSavedSignalingServer()
            ?? SignalingClient.DefaultServer;

        WebRtc = new WebRtcManager(Crypto.Base64PublicKey, new SignalingClient());
        WebRtc.SetSignalingServer(SignalingServer);
        WebRtc.OnMessage += HandleIncomingEvent;

        _ = WebRtc.RegisterAsync();
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
            return null; // applied
        }
        catch (Exception ex)
        {
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

        var evt = Event.SendEvent(ciphertext, replyingTo);
        WebRtc.Send(evt);

        // Store our own outgoing message (encrypted content, like SwiftData in the app).
        var message = new chatxp.core.Storage.Message(
            Guid.NewGuid().ToString(), ciphertext, Crypto.Base64PublicKey,
            ConnectedPeerId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            evt.Id, replyingTo);
        Database.AddMessage(message);
        return (evt, message.Id);
    }

    /// <summary>Requests deletion of a message on both sides (delete Event).</summary>
    public void DeleteMessage(string eventId, string localMessageId)
    {
        if (ConnectedPeerId is not null)
            WebRtc.Send(Event.DeleteEvent(eventId));
        Database.DeleteMessage(localMessageId);
    }

    /// <summary>Initiates a connection to a peer id (Connect flow).</summary>
    public Task ConnectToPeerAsync(string peerId) =>
        WebRtc.ConnectToAsync(peerId);

    /// <summary>
    /// Decrypts stored ciphertext for display. Returns null for tampered/foreign rows.
    /// </summary>
    public string? DecryptFor(string encryptedContent, string counterpartId) =>
        Crypto.Decrypt(encryptedContent, counterpartId);

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
            {
                var plaintext = Crypto.Decrypt(evt.Payload, peer);
                if (plaintext is null) return; // tampered — drop (FR-4.2)
                var message = new chatxp.core.Storage.Message(
                    Guid.NewGuid().ToString(), evt.Payload, peer,
                    Crypto.Base64PublicKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    evt.Id, evt.ReplyingTo);
                Database.AddMessage(message);
                MessageReceived?.Invoke((message, plaintext));
                break;
            }
            case EventTypes.Delete:
                // Payload carries the remote event id; find the local row by it.
                var row = Database.GetConversation(Crypto.Base64PublicKey, peer)
                    .FirstOrDefault(m => m.EventId == evt.Payload);
                if (row is not null) Database.DeleteMessage(row.Id);
                MessageDeleted?.Invoke(evt.Payload);
                break;
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
