using chatxp.core;
using chatxp.core.Storage;
using Xunit;

namespace chatxp.Core.Tests;

/// <summary>
/// Tests for the SQLite persistence layer mirroring the SwiftData model usage in the
/// Swift screens (ChatView query, HistoryView conversations + delete, contacts, blocking).
/// </summary>
public class ChatDatabaseTests : IDisposable
{
    private readonly ChatDatabase _db;
    private readonly string _path;

    private const string Me = "MY-ID";
    private const string Alice = "ALICE-PUB";
    private const string Bob = "BOB-PUB";

    public ChatDatabaseTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"chatxp-test-{Guid.NewGuid():N}.db");
        _db = new ChatDatabase(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { /* ignore */ }
    }

    private static Message Msg(string from, string to, string content, long ts, string? replyingTo = null) =>
        new(Guid.NewGuid().ToString(), content, from, to, ts, Guid.NewGuid().ToString(), replyingTo);

    [Fact]
    public void Conversation_Query_Matches_ChatView_Predicate()
    {
        // ChatView: (msg.from == peer && msg.to == me) || (msg.from == me && msg.to == peer)
        _db.AddMessage(Msg(Alice, Me, "enc-1", 1000));
        _db.AddMessage(Msg(Me, Alice, "enc-2", 2000));
        _db.AddMessage(Msg(Bob, Me, "enc-3", 3000));       // different peer
        _db.AddMessage(Msg(Me, Bob, "enc-4", 4000));       // different peer
        _db.AddMessage(Msg(Alice, Bob, "enc-5", 5000));    // neither side is me

        var conversation = _db.GetConversation(Me, Alice);

        Assert.Equal(2, conversation.Count);
        Assert.Equal(new[] { "enc-1", "enc-2" },
            conversation.Select(m => m.Content).ToArray()); // sorted by timestamp
    }

    [Fact]
    public void RecentConversations_Returns_Last_Message_Per_Peer()
    {
        _db.AddMessage(Msg(Alice, Me, "old-a", 1000));
        _db.AddMessage(Msg(Me, Alice, "new-a", 2000));
        _db.AddMessage(Msg(Me, Bob, "only-b", 1500));

        var recent = _db.GetRecentConversations(Me);

        // Two unique conversations: Alice (newest message enc "new-a") and Bob.
        Assert.Equal(2, recent.Count);
        Assert.Equal("new-a", recent[0].Content); // newest conversation first
        Assert.Equal("only-b", recent[1].Content);
    }

    [Fact]
    public void RecentConversations_Order_By_MessageTime_Not_ReceiptTime()
    {
        var lateOld = new Message(Guid.NewGuid().ToString(), "late-old", Alice, Me,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), "E1", null, Alice, 1000);
        _db.AddMessage(lateOld);
        _db.AddMessage(new Message(Guid.NewGuid().ToString(), "newer", Me, Bob,
            1000, "E2", null, Me, 5000));

        var recent = _db.GetRecentConversations(Me);

        Assert.Equal("newer", recent[0].Content);
        Assert.Equal("late-old", recent[1].Content);
    }

    [Fact]
    public void DeleteConversation_Removes_Both_Directions()
    {
        _db.AddMessage(Msg(Alice, Me, "a", 1000));
        _db.AddMessage(Msg(Me, Alice, "b", 2000));
        _db.AddMessage(Msg(Bob, Me, "keep", 3000));

        _db.DeleteConversation(Me, Alice); // HistoryView.deleteMessages(from: peer)

        Assert.Empty(_db.GetConversation(Me, Alice));
        Assert.Single(_db.GetConversation(Me, Bob));
    }

    [Fact]
    public void DeleteMessage_By_Id()
    {
        var m = Msg(Alice, Me, "tmp", 1000);
        _db.AddMessage(m);
        _db.DeleteMessage(m.Id);
        Assert.Empty(_db.GetConversation(Me, Alice));
    }

    [Fact]
    public void Contacts_Upsert_Rename_Delete()
    {
        _db.UpsertContact(Alice, "Alice");
        Assert.Equal("Alice", _db.GetContact(Alice)!.HumanName);

        // Rename by same webRTCId (ContactEditView rename flow).
        _db.UpsertContact(Alice, "Alice Smith");
        Assert.Equal("Alice Smith", _db.GetContact(Alice)!.HumanName);

        var all = _db.GetAllContacts();
        Assert.Single(all);

        _db.DeleteContact(Alice);
        Assert.Null(_db.GetContact(Alice));
        Assert.Empty(_db.GetAllContacts());
    }

    [Fact]
    public void Blocked_Users_Add_Check_Remove()
    {
        Assert.False(_db.IsBlocked(Bob));

        _db.BlockUser(Bob);       // WebRTCManager: block incoming offers
        _db.BlockUser(Bob);       // idempotent
        Assert.True(_db.IsBlocked(Bob));

        Assert.Single(_db.GetAllBlockedUsers());

        _db.UnblockUser(Bob);
        Assert.False(_db.IsBlocked(Bob));
        Assert.Empty(_db.GetAllBlockedUsers());
    }

    [Fact]
    public void Message_With_ReplyMetadata_RoundTrips()
    {
        var replyTarget = Guid.NewGuid().ToString();
        var m = new Message("msg-id-1", "enc", Me, Alice, 12345, "event-id-9", replyTarget);
        _db.AddMessage(m);

        var loaded = _db.GetConversation(Me, Alice).Single();
        Assert.Equal("msg-id-1", loaded.Id);
        Assert.Equal("event-id-9", loaded.EventId);
        Assert.Equal(replyTarget, loaded.ReplyingTo);
        Assert.Equal(12345, loaded.TimestampUnixMs);
    }

    [Fact]
    public void Database_Reopen_Keeps_Data()
    {
        _db.AddMessage(Msg(Alice, Me, "persisted", 1000));
        _db.Dispose();

        using var reopened = new ChatDatabase(_path);
        Assert.Single(reopened.GetConversation(Me, Alice));
    }
}
