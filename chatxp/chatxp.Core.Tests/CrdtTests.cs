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

    [Fact]
    public void LegacyReplay_StaysHiddenUnderWallClockClearMarker()
    {
        using var db = FreshDb(out _);
        var oldTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        db.AddMessage(new Message("m1", "cipher", Peer, Me, oldTs, "E1", null, Peer, oldTs));

        // A marker written before seq existed holds only at_ms. A legacy op must
        // still be judged by the wall clock when it is replayed after an upgrade.
        db.ClearConversation(Me, Peer);

        var stored = db.GetOpsForPeer(Me, Peer).Single();
        Assert.Equal(0, stored.Seq);
        var evt = ChatCrdt.ToEvent(stored);
        var replayed = ChatCrdt.FromEvent(evt, Peer, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(0, replayed.Seq);

        db.AddMessage(FromOp(replayed, Me));
        Assert.Empty(db.GetConversation(Me, Peer));
    }

    [Fact]
    public void ClearMarker_UsesSequenceNotWallClock()
    {
        using var db = FreshDb(out _);
        db.AddMessage(new Message("m1", "cipher", Peer, Me, 1, "E1", null, Peer, 1000, Seq: 5));
        db.ClearConversation(Me, Peer);

        // A skewed clock must not let a replayed op with an old sequence through.
        db.AddMessage(new Message("m2", "cipher", Peer, Me, 2, "E2", null, Peer, 9_999_999_999, Seq: 5));
        db.AddMessage(new Message("m3", "cipher", Peer, Me, 3, "E3", null, Peer, 10_000_000_000, Seq: 6));

        var visible = db.GetConversation(Me, Peer);
        Assert.Single(visible);
        Assert.Equal("m3", visible[0].Id);
    }

    [Fact]
    public void UnknownWireType_IsNotTreatedAsSend()
    {
        var evt = JsonSerializer.Deserialize<Event>(
            "{\"id\":\"E1\",\"type\":\"future-type\",\"payload\":\"cipher\"}")!;

        Assert.False(evt.IsKnownType);
    }

    private static Message FromOp(MessageOp op, string localId)
    {
        var remote = op.Peer;
        var to = op.Author == localId ? remote : localId;
        return new Message($"row-{op.Id}", op.Payload, op.Author, to, op.TsMs,
            op.Id, op.ReplyingTo, op.Author, op.TsMs, Seq: op.Seq);
    }

    [Fact]
    public void Two_Sessions_Converge_After_Offline_Sends()
    {
        using var alice = FreshDb(out _);
        using var bob = FreshDb(out _);

        alice.AddMessage(new Message("a1", "from-alice", Me, Peer, 10, "EA", null, Me, 10, Seq: 1));
        bob.AddMessage(new Message("b1", "from-bob", Peer, Me, 20, "EB", null, Peer, 20, Seq: 1));

        foreach (var op in alice.GetOpsForPeer(Me, Peer))
            bob.AddMessage(FromOp(op, Peer));
        foreach (var op in bob.GetOpsForPeer(Peer, Me))
            alice.AddMessage(FromOp(op, Me));

        Assert.Equal(2, alice.GetConversation(Me, Peer).Count);
        Assert.Equal(2, bob.GetConversation(Peer, Me).Count);
        Assert.Equal(new[] { "EA", "EB" }, alice.GetConversation(Me, Peer).Select(m => m.EventId));
        Assert.Equal(new[] { "EA", "EB" }, bob.GetConversation(Peer, Me).Select(m => m.EventId));
    }
}
