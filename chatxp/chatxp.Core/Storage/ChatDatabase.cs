using Microsoft.Data.Sqlite;

namespace chatxp.core.Storage;

/// <summary>
/// Message row — mirrors the SwiftData Message model in chat/Data.swift:
/// id (UUID string, unique), content (stored ENCRYPTED, Base64 AES-GCM blob),
/// from / to (Base64 public-key identities), timestamp, event (UUID of the wire Event),
/// replyingTo (UUID of the message being replied to, nullable).
/// </summary>
public sealed record Message(
    string Id,
    string Content,
    string From,
    string To,
    long TimestampUnixMs,
    string? EventId,
    string? ReplyingTo)
{
    // Swift's SwiftData model uses Date precision; we store Unix ms for fidelity.
}

/// <summary>Contact row — mirrors SwiftData Contact (unique webRTCId, humanName, image blob).</summary>
public sealed record Contact(
    string Id,
    string WebRtcId,
    string HumanName,
    byte[]? Image,
    long TimestampUnixMs);

/// <summary>Blocked user row — mirrors SwiftData BlockedUser.</summary>
public sealed record BlockedUser(
    string Id,
    string WebRtcId);

/// <summary>
/// SQLite-backed persistence, mirroring the SwiftData usage in the Swift screens:
///   - ChatView: messages where (from==peer && to==me) || (from==me && to==peer) sorted by time.
///   - HistoryView: unique conversations (the last message per remote peer both directions).
///   - ContactsView / ContactEditView: add/rename/delete contacts by webRTCId.
///   - blocked list checks (WebRTCManager checks incoming offers against it).
/// Uses Microsoft.Data.Sqlite directly (no ORM) with parameterized SQL.
/// </summary>
public sealed class ChatDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public ChatDatabase(string databasePath)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString());
        _connection.Open();
        Initialize();
    }

    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                from_id TEXT NOT NULL,
                to_id TEXT NOT NULL,
                timestamp_ms INTEGER NOT NULL,
                event_id TEXT,
                replying_to TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_messages_conversation
                ON messages(from_id, to_id, timestamp_ms);
            CREATE INDEX IF NOT EXISTS idx_messages_timestamp ON messages(timestamp_ms);

            CREATE TABLE IF NOT EXISTS contacts (
                id TEXT PRIMARY KEY,
                webrtc_id TEXT NOT NULL UNIQUE,
                human_name TEXT NOT NULL,
                image BLOB,
                timestamp_ms INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS blocked_users (
                id TEXT PRIMARY KEY,
                webrtc_id TEXT NOT NULL UNIQUE
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ Messages

    /// <summary>Inserts a message (content is the encrypted Base64 payload, per the Swift app
    /// storing the ciphertext in SwiftData untouched).</summary>
    public void AddMessage(Message message)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (id, content, from_id, to_id, timestamp_ms, event_id, replying_to)
            VALUES ($id, $content, $from, $to, $ts, $event, $reply);
            """;
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$from", message.From);
        cmd.Parameters.AddWithValue("$to", message.To);
        cmd.Parameters.AddWithValue("$ts", message.TimestampUnixMs);
        cmd.Parameters.AddWithValue("$event", (object?)message.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reply", (object?)message.ReplyingTo ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Messages between me and peer, in both roles, oldest-first — the ChatView query.</summary>
    public List<Message> GetConversation(string myId, string peerId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, from_id, to_id, timestamp_ms, event_id, replying_to
            FROM messages
            WHERE (from_id = $peer AND to_id = $me) OR (from_id = $me AND to_id = $peer)
            ORDER BY timestamp_ms ASC;
            """;
        cmd.Parameters.AddWithValue("$me", myId);
        cmd.Parameters.AddWithValue("$peer", peerId);
        return ReadMessages(cmd);
    }

    /// <summary>Last messages of all conversations for HistoryView — newest first.</summary>
    public List<Message> GetRecentConversations(string myId, int limit = 50)
    {
        // Last message per remote peer (either direction), newest first.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.content, m.from_id, m.to_id, m.timestamp_ms, m.event_id, m.replying_to
            FROM messages m
            JOIN (
                SELECT remote, MAX(timestamp_ms) AS max_ts
                FROM (
                    SELECT CASE WHEN from_id = $me THEN to_id ELSE from_id END AS remote,
                           timestamp_ms
                    FROM messages
                    WHERE from_id = $me OR to_id = $me
                )
                GROUP BY remote
            ) latest
              ON ((m.from_id = $me AND m.to_id = latest.remote)
                  OR (m.to_id = $me AND m.from_id = latest.remote))
             AND m.timestamp_ms = latest.max_ts
            ORDER BY m.timestamp_ms DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$me", myId);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadMessages(cmd);
    }

    /// <summary>Deletes every message to/from a peer — HistoryView.deleteMessages(from:).</summary>
    public void DeleteConversation(string peerId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE from_id = $peer OR to_id = $peer;";
        cmd.Parameters.AddWithValue("$peer", peerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a single message by id (used when handling delete Events).</summary>
    public void DeleteMessage(string messageId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", messageId);
        cmd.ExecuteNonQuery();
    }

    private static List<Message> ReadMessages(SqliteCommand cmd)
    {
        var list = new List<Message>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Message(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return list;
    }

    // ------------------------------------------------------------ Contacts

    public void UpsertContact(string webRtcId, string humanName, byte[]? image = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO contacts (id, webrtc_id, human_name, image, timestamp_ms)
            VALUES ($id, $webrtc, $name, $image, $ts)
            ON CONFLICT(webrtc_id) DO UPDATE SET
                human_name = excluded.human_name,
                image = COALESCE(excluded.image, contacts.image);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$webrtc", webRtcId);
        cmd.Parameters.AddWithValue("$name", humanName);
        cmd.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public Contact? GetContact(string webRtcId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, webrtc_id, human_name, image, timestamp_ms FROM contacts WHERE webrtc_id = $w;";
        cmd.Parameters.AddWithValue("$w", webRtcId);
        return ReadContact(cmd);
    }

    public List<Contact> GetAllContacts()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, webrtc_id, human_name, image, timestamp_ms FROM contacts ORDER BY human_name ASC;";
        var list = new List<Contact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Contact(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : (byte[])reader[3],
                reader.GetInt64(4)));
        }
        return list;
    }

    /// <summary>Deletes a contact by webRTCId — ContactsView.deleteContact(from:).</summary>
    public void DeleteContact(string webRtcId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM contacts WHERE webrtc_id = $w;";
        cmd.Parameters.AddWithValue("$w", webRtcId);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------ Blocked users

    public void BlockUser(string webRtcId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO blocked_users (id, webrtc_id) VALUES ($id, $w)
            ON CONFLICT(webrtc_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("$w", webRtcId);
        cmd.ExecuteNonQuery();
    }

    public void UnblockUser(string webRtcId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM blocked_users WHERE webrtc_id = $w;";
        cmd.Parameters.AddWithValue("$w", webRtcId);
        cmd.ExecuteNonQuery();
    }

    public bool IsBlocked(string webRtcId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM blocked_users WHERE webrtc_id = $w;";
        cmd.Parameters.AddWithValue("$w", webRtcId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    public List<BlockedUser> GetAllBlockedUsers()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, webrtc_id FROM blocked_users;";
        var list = new List<BlockedUser>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new BlockedUser(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    private static Contact? ReadContact(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new Contact(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : (byte[])reader[3],
            reader.GetInt64(4));
    }

    public void Dispose() => _connection.Dispose();
}
