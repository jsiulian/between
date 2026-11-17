# CRDT message sync

A 1:1 conversation is a CRDT op log. Messages exchanged while peers are disconnected converge when the data channel reopens. Signaling and E2EE are unchanged.

## Problem

Today a `send` event inserts a local row and a `delete` event removes one. Reconnecting never reconciles the two peers, so offline messages are lost and deletes can diverge.

## Constraints

- Wire format must stay decodable by existing iOS and .NET builds.
- Payloads stay encrypted; only metadata is added in cleartext.
- Both clients must converge without a server.
- Local-only actions (delete-for-you, clear history) must survive a later replay.

## Operation

Each wire event is an immutable op keyed by its UUID. A `send` adds an encrypted payload; a `delete` is a tombstone naming the op it removes. Merge is set union with duplicate ids ignored. Visible messages are adds minus tombstoned ids, ordered by `(ts, author, id)`.

## Wire format

Three optional fields are added to `Event`. Swift's synthesized `Codable` and .NET's `JsonSerializer` both omit nil values, so an old client sees exactly the JSON it already understood. A new client receiving an old event defaults `author` to the connected peer and `ts` to receipt time. `seq` is a per-author Lamport counter used only by clear markers.

```
{"id":"UUID","type":"send","payload":"BASE64 AES-GCM","replyingTo":"UUID?",
 "author":"BASE64 PUBLIC KEY?","ts":1731900000000,"seq":42}
```

No new event type is introduced. Replaying an existing event is a valid sync operation because merge is idempotent. Unknown type strings are dropped rather than treated as `send`.

## Sync protocol

When the data channel opens, each side sends every stored op for that peer as ordinary events, preserving the original event id, author and timestamp. Duplicates are dropped on arrival. A delete can arrive before its add: the tombstone is retained, and the add is hidden when it later appears. There is no request/response phase and no server-side state.

Swift re-encrypts stored plaintext for replay; .NET reuses the stored ciphertext. Both produce a valid payload for the same static shared key.

## Local-only state

Delete-for-you sets a hidden flag rather than removing the row, so replay cannot resurrect it. Clear-history hides current rows and records the highest `seq` seen for each author; a replayed op with an equal or lower sequence is hidden on insert. Old rows without `seq` still use the wall-clock marker, so existing databases keep working.

## Alternatives considered

A two-phase `sync` event carrying a summary followed by a delta exchange would avoid replaying the whole history on each connect. It loses wire compatibility: the existing Swift decoder drops unknown event types, and the .NET decoder currently maps unknown types to `send`. It also adds a second protocol and chunking for large histories. A CRDT library (Automerge or similar) would replace the wire format and the E2EE envelope rather than extend it.

## Tradeoffs accepted

- We replay the full conversation on every connect in exchange for a single, backward-compatible event model. Bandwidth grows with history; delta sync can be added behind the same merge operation later.
- `ts` is a wall clock used for display order; the `(ts, author, id)` tuple gives a deterministic order even when clocks skew. Clear markers use `seq` instead, so clock skew cannot change what a clear hides.
- Hidden rows remain on disk in exchange for replay-safe local deletes.

## Verification

Unit tests cover merge order, idempotence, database suppression, nonce uniqueness and identity-file recovery. Builds run for the .NET app and the iOS simulator. A two-session replay test uses local databases to prove both sides converge after offline sends.
