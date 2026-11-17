using System.Text.Json;
using chatxp.core;
using chatxp.core.Storage;
using Xunit;

namespace chatxp.Core.Tests;

/// <summary>CRDT semantics: convergence, idempotence, tombstone ordering and the
/// backward-compatible wire shape shared with the Swift app.</summary>
public class CrdtTests
{
    private const string Me = "MY-ID";
    private const string Peer = "PEER-ID";

    private static MessageOp Add(string id, string author, long ts, string payload) =>
        new(id, OpKind.Send, author, ts, payload, null, Peer);

    private static MessageOp Del(string id, string author, long ts, string target) =>
        new(id, OpKind.Delete, author, ts, target, null, Peer);

    [Fact]
    public void Merge_IsIndependentOfDeliveryOrder()
    {
        var a = new[] { Add("a", Peer, 10, "one"), Add("b", Me, 20, "two") };
        var b = new[] { Add("b", Me, 20, "two"), Add("a", Peer, 10, "one") };

        var first = ChatCrdt.Merge(a, b);
        var second = ChatCrdt.Merge(b, a);

        Assert.Equal(first.Select(o => o.Id), second.Select(o => o.Id));
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void Merge_DuplicateIdKeepsDeterministicWinner()
    {
        var left = ChatCrdt.Merge(
            new[] { Add("x", Peer, 10, "small") },
            new[] { Add("x", Me, 20, "large") });
        var right = ChatCrdt.Merge(
            new[] { Add("x", Me, 20, "large") },
            new[] { Add("x", Peer, 10, "small") });

        Assert.Equal(left.Single().Payload, right.Single().Payload);
        Assert.Equal("small", left.Single().Payload);
    }

    [Fact]
    public void Tombstone_RemovesAdd_RegardlessOfArrivalOrder()
    {
        var add = Add("m1", Peer, 10, "cipher");
        var del = Del("d1", Me, 11, "m1");

        Assert.Empty(ChatCrdt.VisibleSends(ChatCrdt.Merge(new[] { add }, new[] { del })));
        Assert.Empty(ChatCrdt.VisibleSends(ChatCrdt.Merge(new[] { del }, new[] { add })));
    }

    [Fact]
    public void VisibleSends_AreOrderedByTsThenAuthorThenId()
    {
        var ops = new[]
        {
            Add("z", Peer, 20, "later"),
            Add("a", Me, 10, "earlier"),
            Add("b", Peer, 10, "tie-broken"),
        };

        Assert.Equal(new[] { "a", "b", "z" },
            ChatCrdt.VisibleSends(ops).Select(o => o.Id));
    }

    [Fact]
    public void LegacyEvent_DefaultsAuthorAndTimestampToConnection()
    {
        var evt = JsonSerializer.Deserialize<Event>(
            "{\"id\":\"E1\",\"type\":\"send\",\"payload\":\"cipher\"}")!;

        var op = ChatCrdt.FromEvent(evt, Peer, 1234);

        Assert.Equal(Peer, op.Author);
        Assert.Equal(1234, op.TsMs);
        Assert.Null(evt.Author);
        Assert.Null(evt.Ts);
    }

    [Fact]
    public void NewEvent_OptsOut_OfMetadataFieldsForOldDecoders()
    {
        var json = JsonSerializer.Serialize(Event.SendEvent("cipher"));

        Assert.DoesNotContain("\"author\"", json);
        Assert.DoesNotContain("\"ts\"", json);

        var replay = Event.Replay("E1", "send", "cipher", null, Peer, 99);
        var withMeta = JsonSerializer.Serialize(replay);
        Assert.Contains("\"author\":\"PEER-ID\"", withMeta);
        Assert.Contains("\"ts\":99", withMeta);
        Assert.DoesNotContain("\"replyingTo\"", withMeta);
        Assert.Equal("E1", JsonSerializer.Deserialize<Event>(withMeta)!.Id);
    }

    private static ChatDatabase FreshDb(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"chatxp-crdt-{Guid.NewGuid():N}.db");
        return new ChatDatabase(path);
    }

    [Fact]
    public void Database_DedupesReplayedEventIds()
    {
        using var db = FreshDb(out var path);
        var evtId = Guid.NewGuid().ToString();
        var row = new Message(Guid.NewGuid().ToString(), "cipher", Peer, Me, 10, evtId, null, Peer, 10);
        db.AddMessage(row);
        db.AddMessage(row with { Id = Guid.NewGuid().ToString() });

        Assert.Single(db.GetConversation(Me, Peer));
    }

    [Fact]
    public void Database_OpsCarryMetadataAndTombstones()
    {
        using var db = FreshDb(out var path);
        db.AddMessage(new Message("m1", "cipher", Peer, Me, 10, "E1", null, Peer, 10));
        db.AddTombstone(new Tombstone("E2", "E1", Me, Peer, Me, 20));

        var ops = db.GetOpsForPeer(Me, Peer);
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o.Kind == OpKind.Send && o.Author == Peer && o.TsMs == 10);
        Assert.Contains(ops, o => o.Kind == OpKind.Delete && o.Payload == "E1" && o.TsMs == 20);
        Assert.Empty(db.GetConversation(Me, Peer));
    }

    [Fact]
    public void Database_ClearKeepsLateArrivingOldOpsHidden()
    {
        using var db = FreshDb(out var path);
        db.ClearConversation(Me, Peer);
        var oldTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        db.AddMessage(new Message("m1", "cipher", Peer, Me, oldTs, "E1", null, Peer, oldTs));

        Assert.Empty(db.GetConversation(Me, Peer));
    }
}
