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
    string? ReplyingTo,
    string? Author = null,
    long TsMs = 0,
    bool Hidden = false)
{
    // Swift's SwiftData model uses Date precision; we store Unix ms for fidelity.
}

/// <summary>Remote unsend, stored as a tombstone so a later replay of the add cannot
/// resurrect it — including when the delete arrives before the add.</summary>
public sealed record Tombstone(
    string EventId,
    string TargetEventId,
    string From,
    string To,
    string? Author,
    long TsMs);

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
                replying_to TEXT,
                author TEXT,
                ts_ms INTEGER NOT NULL DEFAULT 0,
                hidden INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS tombstones (
                event_id TEXT PRIMARY KEY,
                target_event_id TEXT NOT NULL,
                from_id TEXT NOT NULL,
                to_id TEXT NOT NULL,
                author TEXT,
                ts_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tombstones_target
                ON tombstones(target_event_id);
            CREATE TABLE IF NOT EXISTS conversation_clears (
                peer_id TEXT PRIMARY KEY,
                at_ms INTEGER NOT NULL
            );

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
        MigrateCrdtColumns();
    }

    /// <summary>Additive migration for databases created before the CRDT columns existed.</summary>
    private void MigrateCrdtColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = _connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(messages);";
            using var reader = info.ExecuteReader();
            while (reader.Read()) existing.Add(reader.GetString(1));
        }
        foreach (var (name, ddl) in new[]
        {
            ("author", "ALTER TABLE messages ADD COLUMN author TEXT;"),
            ("ts_ms", "ALTER TABLE messages ADD COLUMN ts_ms INTEGER NOT NULL DEFAULT 0;"),
            ("hidden", "ALTER TABLE messages ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0;"),
        })
        {
            if (existing.Contains(name)) continue;
            using var alter = _connection.CreateCommand();
            alter.CommandText = ddl;
            alter.ExecuteNonQuery();
        }

        // Legacy rows have no metadata: derive author/ts from the fields we do have.
        using (var backfill = _connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE messages SET author = from_id WHERE author IS NULL;
                UPDATE messages SET ts_ms = timestamp_ms WHERE ts_ms = 0;
                DELETE FROM messages WHERE event_id IS NOT NULL AND rowid NOT IN (
                    SELECT MIN(rowid) FROM messages WHERE event_id IS NOT NULL GROUP BY event_id);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_messages_event ON messages(event_id);
                """;
            backfill.ExecuteNonQuery();
        }
    }

    // ------------------------------------------------------------ Messages

    /// <summary>Inserts a message (content is the encrypted Base64 payload, per the Swift app
    /// storing the ciphertext in SwiftData untouched).</summary>
    public void AddMessage(Message message)
    {
        var ts = message.TsMs != 0 ? message.TsMs : message.TimestampUnixMs;
        var author = message.Author ?? message.From;
        var hidden = message.Hidden || IsSuppressed(message.EventId, ts, message.From, message.To);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages
                (id, content, from_id, to_id, timestamp_ms, event_id, replying_to, author, ts_ms, hidden)
            VALUES ($id, $content, $from, $to, $ts, $event, $reply, $author, $opTs, $hidden);
            """;
        cmd.Parameters.AddWithValue("$id", message.Id);
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$from", message.From);
        cmd.Parameters.AddWithValue("$to", message.To);
        cmd.Parameters.AddWithValue("$ts", message.TimestampUnixMs);
        cmd.Parameters.AddWithValue("$event", (object?)message.EventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reply", (object?)message.ReplyingTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$author", author);
        cmd.Parameters.AddWithValue("$opTs", ts);
        cmd.Parameters.AddWithValue("$hidden", hidden ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>True when a tombstone or local clear marker already covers this op.</summary>
    private bool IsSuppressed(string? eventId, long ts, string from, string to)
    {
        if (eventId is not null)
        {
            using var tomb = _connection.CreateCommand();
            tomb.CommandText = "SELECT COUNT(*) FROM tombstones WHERE target_event_id = $e;";
            tomb.Parameters.AddWithValue("$e", eventId);
            if (Convert.ToInt64(tomb.ExecuteScalar()) > 0) return true;
        }
        using var clear = _connection.CreateCommand();
        clear.CommandText = """
            SELECT COUNT(*) FROM conversation_clears
            WHERE (peer_id = $from OR peer_id = $to) AND at_ms >= $ts;
            """;
        clear.Parameters.AddWithValue("$from", from);
        clear.Parameters.AddWithValue("$to", to);
        clear.Parameters.AddWithValue("$ts", ts);
        return Convert.ToInt64(clear.ExecuteScalar()) > 0;
    }

    /// <summary>Finds a stored op by its wire event id, for delete/reply resolution.</summary>
    public Message? GetMessageByEventId(string eventId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, from_id, to_id, timestamp_ms, event_id, replying_to, author, ts_ms, hidden
            FROM messages WHERE event_id = $e;
            """;
        cmd.Parameters.AddWithValue("$e", eventId);
        return ReadMessages(cmd).FirstOrDefault();
    }

    /// <summary>Messages between me and peer, in both roles, oldest-first — the ChatView query.</summary>
    public List<Message> GetConversation(string myId, string peerId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, content, from_id, to_id, timestamp_ms, event_id, replying_to, author, ts_ms, hidden
            FROM messages
            WHERE hidden = 0
              AND ((from_id = $peer AND to_id = $me) OR (from_id = $me AND to_id = $peer))
              AND (event_id IS NULL OR event_id NOT IN (SELECT target_event_id FROM tombstones))
            ORDER BY ts_ms ASC, author ASC, event_id ASC;
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
            SELECT m.id, m.content, m.from_id, m.to_id, m.timestamp_ms, m.event_id,
                   m.replying_to, m.author, m.ts_ms, m.hidden
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
             AND m.hidden = 0
             AND (m.event_id IS NULL OR m.event_id NOT IN (SELECT target_event_id FROM tombstones))
            ORDER BY m.timestamp_ms DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$me", myId);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadMessages(cmd);
    }

    /// <summary>Local wipe of a conversation. Also records a clear marker so replayed
    /// ops for the peer stay hidden instead of reappearing.</summary>
    public void DeleteConversation(string peerId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM messages WHERE from_id = $peer OR to_id = $peer;
            DELETE FROM tombstones WHERE from_id = $peer OR to_id = $peer;
            INSERT INTO conversation_clears (peer_id, at_ms) VALUES ($peer, $at)
            ON CONFLICT(peer_id) DO UPDATE SET at_ms = MAX(at_ms, excluded.at_ms);
            """;
        cmd.Parameters.AddWithValue("$peer", peerId);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt64(8),
                reader.GetInt64(9) != 0));
        }
        return list;
    }

    // ------------------------------------------------------------ CRDT ops

    /// <summary>Idempotence check used before inserting a decoded wire event.</summary>
    public bool HasEvent(string eventId)
    {
        using var msg = _connection.CreateCommand();
        msg.CommandText = "SELECT COUNT(*) FROM messages WHERE event_id = $e;";
        msg.Parameters.AddWithValue("$e", eventId);
        if (Convert.ToInt64(msg.ExecuteScalar()) > 0) return true;
        using var tomb = _connection.CreateCommand();
        tomb.CommandText = "SELECT COUNT(*) FROM tombstones WHERE event_id = $e;";
        tomb.Parameters.AddWithValue("$e", eventId);
        return Convert.ToInt64(tomb.ExecuteScalar()) > 0;
    }

    /// <summary>Stores a remote unsend and hides the target if we already have it.</summary>
    public void AddTombstone(Tombstone tombstone)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO tombstones
                (event_id, target_event_id, from_id, to_id, author, ts_ms)
            VALUES ($id, $target, $from, $to, $author, $ts);
            """;
        cmd.Parameters.AddWithValue("$id", tombstone.EventId);
        cmd.Parameters.AddWithValue("$target", tombstone.TargetEventId);
        cmd.Parameters.AddWithValue("$from", tombstone.From);
        cmd.Parameters.AddWithValue("$to", tombstone.To);
        cmd.Parameters.AddWithValue("$author", (object?)tombstone.Author ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", tombstone.TsMs);
        cmd.ExecuteNonQuery();

        using var hide = _connection.CreateCommand();
        hide.CommandText = "UPDATE messages SET hidden = 1 WHERE event_id = $target;";
        hide.Parameters.AddWithValue("$target", tombstone.TargetEventId);
        hide.ExecuteNonQuery();
    }

    /// <summary>Every op in one conversation, for anti-entropy replay on channel open.</summary>
    public List<MessageOp> GetOpsForPeer(string me, string peer)
    {
        var ops = new List<MessageOp>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, from_id, to_id, author, ts_ms, content, replying_to
                FROM messages
                WHERE event_id IS NOT NULL
                  AND ((from_id = $me AND to_id = $peer) OR (from_id = $peer AND to_id = $me));
                """;
            cmd.Parameters.AddWithValue("$me", me);
            cmd.Parameters.AddWithValue("$peer", peer);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var from = reader.GetString(1);
                ops.Add(new MessageOp(
                    reader.GetString(0), OpKind.Send,
                    reader.IsDBNull(3) ? from : reader.GetString(3),
                    reader.GetInt64(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    from == me ? reader.GetString(2) : from));
            }
        }
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT event_id, target_event_id, from_id, to_id, author, ts_ms
                FROM tombstones
                WHERE (from_id = $me AND to_id = $peer) OR (from_id = $peer AND to_id = $me);
                """;
            cmd.Parameters.AddWithValue("$me", me);
            cmd.Parameters.AddWithValue("$peer", peer);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var from = reader.GetString(2);
                ops.Add(new MessageOp(
                    reader.GetString(0), OpKind.Delete,
                    reader.IsDBNull(4) ? from : reader.GetString(4),
                    reader.GetInt64(5), reader.GetString(1), null,
                    from == me ? reader.GetString(3) : from));
            }
        }
        return ops;
    }

    /// <summary>Local-only hide: keeps the op on disk so replay cannot resurrect it.</summary>
    public void HideMessage(string localId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE messages SET hidden = 1 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", localId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Clear a conversation locally; older ops arriving later stay hidden.</summary>
    public void ClearConversation(string me, string peer)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE messages SET hidden = 1
            WHERE (from_id = $me AND to_id = $peer) OR (from_id = $peer AND to_id = $me);
            INSERT INTO conversation_clears (peer_id, at_ms) VALUES ($peer, $at)
            ON CONFLICT(peer_id) DO UPDATE SET at_ms = MAX(at_ms, excluded.at_ms);
            """;
        cmd.Parameters.AddWithValue("$me", me);
        cmd.Parameters.AddWithValue("$peer", peer);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
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
