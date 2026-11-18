using System.Collections.ObjectModel;
using chatxp.core;
using chatxp.core.Storage;

namespace chatxp.AvaloniaApp.Services;

public sealed class ChatSessionService : IDisposable
{
    /// <summary>App data dir (DB, identity, settings). Overridable via
    /// CHATXP_DATA_DIR — used by tests to avoid touching the real user data.
    /// Resolved PER CONSTRUCTION (not static-once) so env changes between
    /// constructions in the same process take effect.</summary>
    private static string ResolveAppDataDir() =>
        Environment.GetEnvironmentVariable("CHATXP_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "chatxp");

    private const string KeyFileName = "identity.key";
    private const string DbFileName = "chatxp.db";
    private const string SettingsFileName = "settings.txt";

    public E2eeCrypto Crypto { get; }
    public ChatDatabase Database { get; }
    public WebRtcManager WebRtc { get; }
    public DiagnosticLog Log { get; } = new();

    /// <summary>The pending incoming connection, if any (contact request).</summary>
    public string? PendingIncomingContactId { get; private set; }

    /// <summary>The server the pending request arrived on (stored with the contact
    /// on accept, so future reconnections route via the same server).</summary>
    private string? _pendingRequestServer;

    private readonly string _appDataDir = ResolveAppDataDir();
    private readonly string _keyPath;
    private readonly string _settingsPath;
    private readonly string _dbPath;

    public ChatSessionService(string? signalingServer = null)
    {
        _keyPath = Path.Combine(_appDataDir, KeyFileName);
        _settingsPath = Path.Combine(_appDataDir, SettingsFileName);
        _dbPath = Path.Combine(_appDataDir, DbFileName);
        Directory.CreateDirectory(_appDataDir);

        Crypto = LoadOrCreateIdentity();
        Database = new ChatDatabase(_dbPath);

        SignalingServer = signalingServer
            ?? System.Environment.GetEnvironmentVariable("CHATXP_SIGNAL_SERVER")
            ?? LoadSavedSignalingServer()
            ?? SignalingClient.DefaultServer;

        WebRtc = new WebRtcManager(Crypto.Base64PublicKey, new SignalingClient(), Log);
        WebRtc.SetSignalingServer(SignalingServer);
        WebRtc.OnMessage += HandleIncomingEvent;
        WebRtc.OnIncomingConnection += HandleIncomingConnection;

        Log.Info($"chatxp starting — id {Crypto.Base64PublicKey[..10]}…");
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

    /// <summary>The signaling server currently in use (persisted across restarts).</summary>
    public string SignalingServer { get; private set; }

    // ------------------------------------------------------------ contacts

    /// <summary>Adds a contact. Nickname is mandatory; per-contact signaling server
    /// optional (falls back to the default server when null).</summary>
    public void AddContact(string nickname, string peerId, string? signalingServer = null)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            throw new ArgumentException("Nickname is required.");
        Database.UpsertContact(peerId, nickname.Trim(), null,
            string.IsNullOrWhiteSpace(signalingServer) ? null : signalingServer.Trim());
        ContactAdded?.Invoke(peerId);
    }

    /// <summary>Renames/edit of a contact (server unchanged when null passed).</summary>
    public void EditContact(string peerId, string newNickname, string? signalingServer = null)
    {
        if (string.IsNullOrWhiteSpace(newNickname))
            throw new ArgumentException("Nickname is required.");
        Database.UpsertContact(peerId, newNickname.Trim(), null, signalingServer?.Trim());
        ContactUpdated?.Invoke(peerId);
    }

    public void DeleteContact(string peerId)
    {
        Database.DeleteContact(peerId);
        ContactRemoved?.Invoke(peerId);
    }

    public string? NicknameFor(string peerId) => Database.GetContact(peerId)?.HumanName;

    /// <summary>Per-contact signaling server, or null when the contact has none
    /// (meaning: use the default).</summary>
    public string? ServerFor(string peerId) => Database.GetContact(peerId)?.SignalingServer;

    public List<Contact> GetAllContacts() => Database.GetAllContacts();

    // ------------------------------------------------------------ connection

    /// <summary>Auto-accept logic: when an offer comes in from an EXISTING contact,
    /// the other party's request is accepted automatically (no user prompt). First-time
    /// senders raise ContactRequestReceived and wait for the user's Accept/Ignore.</summary>
    private void HandleIncomingConnection(string fromId, string sdp)
    {
        if (Database.GetContact(fromId) is not null)
        {
            Log.Info($"Incoming connection from known contact {NicknameFor(fromId)} — auto-accepting");
            // Route via the contact's stored signaling server (per-contact server).
            if (ServerFor(fromId) is { } server && !string.IsNullOrEmpty(server))
                WebRtc.SetSignalingServer(server);
            _ = WebRtc.AcceptIncomingAsync(fromId);
            SetConnectedPeer(fromId);
        }
        else
        {
            Log.Info($"Contact request from unknown peer {fromId[..10]}… — pending");
            PendingIncomingContactId = fromId;
            // The request arrived on whatever server we're currently registered on —
            // remember it so Accept can store it as the contact's server.
            _pendingRequestServer = WebRtcServerUrl;
            ContactRequestReceived?.Invoke(fromId);
        }
    }

    /// <summary>The signaling URL the manager's client is currently configured with
    /// (the server an incoming request just arrived on).</summary>
    public string WebRtcServerUrl => WebRtc.SignalingServerUrl;

    /// <summary>Begins a connection to a contact by id. Registers on the contact's
    /// signaling server when one is stored (per-contact server), otherwise the
    /// current/default server.</summary>
    public async Task ConnectToContactAsync(string peerId)
    {
        if (ServerFor(peerId) is { } server && !string.IsNullOrEmpty(server))
            WebRtc.SetSignalingServer(server);
        SetConnectedPeer(peerId);
        await WebRtc.ConnectToAsync(peerId);
    }

    /// <summary>Accept: creates the contact with the request's origin server (so
    /// future reconnections route via the same signaling server) and answers.</summary>
    public void AcceptContactRequest(string peerId, string nickname)
    {
        PendingIncomingContactId = null;
        if (Database.GetContact(peerId) is null)
            Database.UpsertContact(peerId, nickname, null, _pendingRequestServer);
        _pendingRequestServer = null;
        _ = WebRtc.AcceptIncomingAsync(peerId);
        SetConnectedPeer(peerId);
    }

    public void IgnoreContactRequest(string peerId)
    {
        PendingIncomingContactId = null;
        _ = WebRtc.RejectIncomingAsync(peerId);
    }

    /// <summary>Reloads history for a peer and provides decrypted lines.</summary>
    public List<(Message row, string plaintext)> LoadHistory(string peerId)
    {
        var rows = Database.GetConversation(Crypto.Base64PublicKey, peerId);
        var result = new List<(Message, string)>(rows.Count);
        foreach (var row in rows)
        {
            var peer = row.From == Crypto.Base64PublicKey ? row.To : row.From;
            var plain = DecryptFor(row.Content, peer);
            if (plain is not null)
                result.Add((row, plain));
        }
        return result;
    }

    /// <summary>Marks the connected peer (called when accept/connect completes).</summary>
    public void SetConnectedPeer(string peerId) => ConnectedPeerId = peerId;

    /// <summary>Base64 identity (public key) of the connected peer, if any.</summary>
    public string? ConnectedPeerId { get; private set; }

    // ------------------------------------------------------------ messaging

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

    /// <summary>Decrypts stored ciphertext for display. Returns null for tampered/foreign rows.</summary>
    public string? DecryptFor(string encryptedContent, string counterpartId) =>
        Crypto.Decrypt(encryptedContent, counterpartId);

    private void HandleIncomingEvent(Event evt)
    {
        var peer = ConnectedPeerId ?? string.Empty;
        switch (evt.Type)
        {
            case EventTypes.Send:
            {
                if (ConnectedPeerId is null && PendingIncomingContactId is not null)
                    return; // message before accept — ignore (requests are signal-level)
                // Decrypt. Messages from unknown peers cannot be decrypted anyway
                // (no shared key is possible until both accept).
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

    /// <summary>Raised when a decrypted message arrives: (local row, plaintext).</summary>
    public event Action<(Message row, string plaintext)>? MessageReceived;

    /// <summary>Raised when the peer deleted a message (payload = remote event id).</summary>
    public event Action<string>? MessageDeleted;

    /// <summary>Raised when the peer's typing indicator fires.</summary>
    public event Action? PeerTyping;

    /// <summary>First-time contact request received; needs Accept/Ignore.</summary>
    public event Action<string>? ContactRequestReceived;

    public event Action<string>? ContactAdded;

    public event Action<string>? ContactUpdated;

    public event Action<string>? ContactRemoved;

    // ------------------------------------------------------------ settings

    /// <summary>
    /// Switches the signaling server at runtime (Settings "Apply"). Validates the URL,
    /// persists the choice, closes the old socket, and registers on the new server.
    /// Returns null on success or an error message on failure — the caller surfaces it.
    /// </summary>
    public async Task<string?> ApplySignalingServerAsync(string url)
    {
        var trimmed = url.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "Enter a server URL (ws:// or wss://).";
        try
        {
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

    // ------------------------------------------------------------ identity

    private E2eeCrypto LoadOrCreateIdentity()
    {
        var keyPath = _keyPath;
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

/// <summary>Single decrypted chat line for display: plaintext + receive time.</summary>
public sealed record ChatLine(string Id, string? EventId, string Text, bool Mine,
    long TimestampUnixMs, string? ReplyingTo)
{
    /// <summary>HH:mm for the bubble timestamp (local time).</summary>
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(TimestampUnixMs).ToLocalTime()
        .ToString("HH:mm");
}

/// <summary>A contact as shown in the Contacts list (server shown as hint).</summary>
public sealed record ContactView(string WebRtcId, string Nickname, string IdPreview, string? Server);

/// <summary>A pending contact request shown in Notifications (requires a decision).</summary>
public sealed record PendingRequestView(string PeerId)
{
    public string IdPreview => PeerId.Length <= 14 ? PeerId : PeerId[..14] + "…";
}
