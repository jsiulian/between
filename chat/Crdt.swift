import Foundation

/// CRDT operation kinds. A `send` adds an encrypted payload; a `delete` is a
/// tombstone naming the op it removes. Mirrors chatxp.Core/Crdt/ChatCrdt.cs.
enum OpKind: String {
    case send
    case delete
}

/// One immutable op. `id` is the wire Event UUID string and is the op identity.
struct MessageOp: Equatable {
    let id: String
    let kind: OpKind
    let author: String
    let ts: Int64
    let payload: String
    let replyingTo: String?
    let peer: String
    let seq: Int64
}

enum ChatCrdt {
    static func nowMs() -> Int64 {
        Int64(Date().timeIntervalSince1970 * 1000)
    }

    private static let seqLock = NSLock()

    /// Per-author Lamport counter. Persisted so a restart cannot reuse a value used
    /// by an old clear marker.
    static func nextSeq(for author: String) -> Int64 {
        seqLock.lock()
        defer { seqLock.unlock() }
        let defaults = Config.sharedDefaults ?? .standard
        let key = "crdt.seq.\(author)"
        let next = defaults.integer(forKey: key) + 1
        defaults.set(next, forKey: key)
        return Int64(next)
    }

    static func maxSeq(for author: String) -> Int64 {
        let defaults = Config.sharedDefaults ?? .standard
        return Int64(defaults.integer(forKey: "crdt.seq.\(author)"))
    }

    /// Set union keyed by op id. A duplicate id keeps the deterministically smallest
    /// content, so convergence does not depend on delivery order.
    static func merge(_ current: [MessageOp], _ incoming: [MessageOp]) -> [MessageOp] {
        var byId: [String: MessageOp] = [:]
        for op in current + incoming {
            if let existing = byId[op.id] {
                if prefer(op, existing) { byId[op.id] = op }
            } else {
                byId[op.id] = op
            }
        }
        return byId.values.sorted { less($0, $1) }
    }

    /// Adds minus tombstoned ids, in deterministic display order.
    static func visibleSends(_ ops: [MessageOp]) -> [MessageOp] {
        let deleted = Set(ops.filter { $0.kind == .delete }.map { $0.payload })
        return ops.filter { $0.kind == .send && !deleted.contains($0.id) }
                  .sorted { less($0, $1) }
    }

    private static func less(_ a: MessageOp, _ b: MessageOp) -> Bool {
        if a.ts != b.ts { return a.ts < b.ts }
        if a.author != b.author { return a.author < b.author }
        return a.id < b.id
    }

    private static func prefer(_ a: MessageOp, _ b: MessageOp) -> Bool {
        if a.ts != b.ts { return a.ts < b.ts }
        if a.author != b.author { return a.author < b.author }
        if a.kind.rawValue != b.kind.rawValue { return a.kind.rawValue < b.kind.rawValue }
        if a.replyingTo != b.replyingTo { return (a.replyingTo ?? "") < (b.replyingTo ?? "") }
        if a.seq != b.seq { return a.seq < b.seq }
        return a.payload < b.payload
    }
}

extension Event {
    /// Decodes a wire event into an op. Legacy events without author/ts get defaults
    /// from the connection so old clients still interoperate.
    func asOp(peer: String, fallbackTs: Int64) -> MessageOp {
        MessageOp(id: id.uuidString,
                  kind: type == .delete ? .delete : .send,
                  author: author ?? peer,
                  ts: ts ?? fallbackTs,
                  payload: payload,
                  replyingTo: replyingTo,
                  peer: peer,
                  seq: seq ?? 0)
    }
}

extension MessageOp {
    /// Rebuilds the original wire event for anti-entropy replay: same id and metadata.
    var event: Event {
        Event(id: UUID(uuidString: id) ?? UUID(),
              type: kind == .send ? .send : .delete,
              payload: payload,
              replyingTo: replyingTo,
              author: author,
              ts: ts,
              seq: seq)
    }
}
