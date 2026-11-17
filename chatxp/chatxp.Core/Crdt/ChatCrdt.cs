namespace chatxp.core;

public enum OpKind { Send, Delete }

/// <summary>One immutable CRDT operation. Id is the wire Event id and is the op identity.
/// Peer is the other party in the 1:1 conversation, used for replay scoping.</summary>
public sealed record MessageOp(
    string Id,
    OpKind Kind,
    string Author,
    long TsMs,
    string Payload,
    string? ReplyingTo,
    string Peer,
    long Seq = 0)
{
    public string WireType => Kind == OpKind.Send ? EventTypeNames.Send : EventTypeNames.Delete;
}

/// <summary>Pure CRDT rules shared by storage and transport. No I/O, no framework types.</summary>
public static class ChatCrdt
{
    /// <summary>Set union keyed by op id. A duplicate id keeps the deterministically
    /// smallest content, so convergence does not depend on delivery order.</summary>
    public static IReadOnlyList<MessageOp> Merge(
        IEnumerable<MessageOp> current, IEnumerable<MessageOp> incoming)
    {
        var byId = new Dictionary<string, MessageOp>(StringComparer.Ordinal);
        foreach (var op in current.Concat(incoming))
        {
            if (!byId.TryGetValue(op.Id, out var existing) || Compare(op, existing) < 0)
                byId[op.Id] = op;
        }
        return byId.Values.OrderBy(OrderKey).ToList();
    }

    /// <summary>Adds minus tombstoned ids, in deterministic display order.</summary>
    public static IReadOnlyList<MessageOp> VisibleSends(IEnumerable<MessageOp> ops)
    {
        var all = ops.ToList();
        var deleted = all.Where(o => o.Kind == OpKind.Delete)
                         .Select(o => o.Payload).ToHashSet(StringComparer.Ordinal);
        return all.Where(o => o.Kind == OpKind.Send && !deleted.Contains(o.Id))
                  .OrderBy(OrderKey).ToList();
    }

    /// <summary>Decodes a wire event into an op. Legacy events without author/ts get
    /// defaults from the connection so old clients still interoperate.</summary>
    public static MessageOp FromEvent(Event evt, string peerId, long fallbackTsMs) => new(
        evt.Id,
        evt.Type == EventTypes.Delete ? OpKind.Delete : OpKind.Send,
        string.IsNullOrEmpty(evt.Author) ? peerId : evt.Author!,
        evt.Ts ?? fallbackTsMs,
        evt.Payload,
        evt.ReplyingTo,
        peerId,
        evt.Seq ?? 0);

    public static Event ToEvent(MessageOp op) => Event.Replay(
        op.Id, op.WireType, op.Payload, op.ReplyingTo, op.Author, op.TsMs, op.Seq);

    private static (long, string, string) OrderKey(MessageOp op) =>
        (op.TsMs, op.Author, op.Id);

    private static int Compare(MessageOp a, MessageOp b)
    {
        var c = a.TsMs.CompareTo(b.TsMs);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Author, b.Author);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Kind.ToString(), b.Kind.ToString());
        if (c != 0) return c;
        c = string.CompareOrdinal(a.ReplyingTo ?? "", b.ReplyingTo ?? "");
        if (c != 0) return c;
        c = a.Seq.CompareTo(b.Seq);
        return c != 0 ? c : string.CompareOrdinal(a.Payload, b.Payload);
    }
}
