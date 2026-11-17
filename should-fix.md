# Whole-repo audit

Report only. No code was changed.

Load assumption: one person per device, 1:1 chats, a few thousand messages, and a small public signaling server. At that size, full replay on each connect is a known cost recorded in `CRDT.md`, not a defect on its own.

## Must fix

1. .NET encrypts every message with the same all-zero AES-GCM nonce (`E2eeCrypto.cs:153-159`).
   What. Each call to the encrypt path passes 12 zero bytes as the nonce. AES-GCM breaks when a nonce repeats under one key. Two messages with the same plaintext produce the same ciphertext prefix, and an attacker who knows one plaintext can recover relationships in others and forge valid tags.
   Why. This defeats the confidentiality and integrity the rest of the app is built around. It affects every .NET-to-Swift message.
   Fix. Generate a fresh random 12-byte nonce per message, prepend it to the ciphertext, and version the wire payload so old records still decrypt. Update the Swift decrypt path to read the prefix. Keep the existing crypto vector tests and add one that proves the same plaintext encrypts differently twice.
   Check. Encrypt "hello" twice with the same key, assert different ciphertexts, then decrypt both in Swift and assert equal plaintext.

2. .NET chat history is empty after reconnect or restart.
   What. `ChatSessionService` clears `Lines`, then `HandleIncomingEvent` drops every replayed op because `HasEvent` already knows its id. `Database.GetConversation` is never used to rebuild the view.
   Why. The user sees an empty conversation until someone sends a new message, even though the database has the whole history. This is the main promise of offline sync.
   Fix. When the data channel opens, load the conversation from `Database.GetConversation` and populate `Lines` from it, then apply only new events. Keep the dedupe check for incremental events.
   Check. Exchange messages between two .NET clients, restart one, reconnect, and confirm all earlier messages appear.

3. Swift global wipe and "New ID" do not block replay of wiped messages.
   What. `wipeAlert` in `ContentView.swift:219-233` deletes `Message` rows only. It writes no `ConversationClear` marker. `ConnectView.swift:127-146` deletes `Message` rows only and leaves existing `Tombstone` and `ConversationClear` rows in place while rotating the identity.
   Why. On the next connect the peer replays its op log. Without a marker at the current time, wiped messages come back. With old markers and tombstones left behind, messages from the new identity can be hidden.
   Fix. In both paths, insert a `ConversationClear` row for every known peer at the current time. In "New ID", also delete all `Tombstone` and `ConversationClear` rows after the key rotates. Save before reconnecting.
   Check. Wipe on device A, reconnect A and B, and confirm old messages stay gone. Rotate the ID on A, reconnect, and confirm new messages from B are visible.

4. Clear-history suppression compares two different clocks.
   What. `ConversationClear.atMs` comes from the local clock. `isSuppressed` in `ContentView.swift:112-124` compares it with the sender's `Event.ts`, which comes from the peer's clock.
   Why. If one device is minutes ahead, a clear can fail to hide that peer's recent messages, or it can hide messages sent after the clear. The result depends on device settings, not on what happened in the chat.
   Fix. Use a per-peer monotonic sequence, or a hybrid logical clock with the peer id in the tie-break, and compare values from the same clock domain. Apply the same rule in the .NET database markers.
   Check. Put device B five minutes ahead, clear history on A, reconnect, then send from B. Confirm old messages stay hidden and the new one appears.

## Should fix

5. .NET ignores the hidden flag when displaying replayed messages.
   What. `MainViewModel.cs:67-71` adds a `ChatLine` for every `MessageReceived` event without checking `Message.Hidden`.
   Why. Messages cleared locally can reappear in the visible list when they are loaded or replayed. The database hides them but the view does not.
   Fix. Skip hidden rows in `MessageReceived` and in the history load used by finding 2.
   Check. Clear history locally, reconnect, and confirm no cleared row is shown.

6. Production dedupe and the tested merge functions are two different implementations.
   What. `ChatCrdt.Merge` and the Swift `merge` function are not used by the live receive path. Live code uses first-arrival-wins. The two merge functions also break ties differently, and the CRDT tests exercise the unused ones.
   Why. Convergence is only guaranteed for code that runs. A duplicate id with a conflicting payload can leave the two clients showing different content.
   Fix. Route replay and incoming events through one merge function per client, make both platforms use the same `(ts, author, id)` winner rule, and point the tests at the production path. Delete the duplicate dedupe logic.
   Check. Deliver the same id with two payloads in both orders on each platform and assert identical final state.

7. A corrupt identity file silently creates a new identity.
   What. The key file is written in place, and a failed load falls through to key generation. A crash during write leaves a partial file that becomes a different user id on the next launch.
   Why. The user loses their identity and contacts without any warning, and the old id stays registered until the server times it out.
   Fix. Write to a temporary file, fsync, then rename. On load failure, keep a backup and report the error instead of rotating the key.
   Check. Truncate the key file, launch, and confirm the app either recovers the old key or reports an error without registering a new id.

8. Recent conversation order uses receipt time instead of message time.
   What. `GetRecentConversations` sorts by `timestamp_ms`, when the row arrived, rather than `ts_ms`, when the message was sent.
   Why. A late-arriving old message pushes a conversation to the top of the list, so the list does not reflect the real latest message.
   Fix. Order by the maximum `ts_ms` per peer, with the existing author and id tie-break.
   Check. Send an old-timestamped message after a new one and confirm the conversation keeps the newer preview and order.

9. Replayed own messages can be shown as peer messages.
   What. `author` is optional. When it is missing, the receive path defaults to the connected peer's id. Replay of the local user's own older ops can therefore be attributed to the other person.
   Why. The transcript shows the wrong sender, and reply and quote metadata can point at the wrong author.
   Fix. Write `author` on every new event, default missing author to the stored local id for local rows, and only use the peer id when the event was actually received from the peer.
   Check. Replay a mixed set of own and peer ops from the peer side and verify sender labels on both clients.

10. The signaling server leaves stale registrations.
    What. `server/index.js:78-91` removes one registration per closed socket. If an id registered more than once, the other rows survive.
    Why. Offers can be routed to a dead connection, so the caller waits until timeout while a live connection is available.
    Fix. Remove all registrations owned by that socket on close, and remove rows by id when a new registration replaces the old one.
    Check. Register the same id twice, close the socket, and inspect the registration map for leftovers.

11. Unknown event types become ordinary messages.
    What. `Event.Type` maps any unrecognized string to `Send`.
    Why. A malformed or future event silently creates a visible message instead of being rejected. That makes protocol mistakes look like user content.
    Fix. Parse the type strictly at the decode boundary. Log and ignore unknown types.
    Check. Inject an event with type `future-type` and confirm no message row appears.

12. .NET accepts blocked incoming offers.
    What. `MainViewModel.cs:59-65` shows the accept prompt for every offer. `ChatDatabase.IsBlocked` exists but is not checked there. Swift rejects blocked offers before prompting.
    Why. A blocked peer can still reach the user with a call prompt, and the two clients behave differently.
    Fix. Check `IsBlocked(fromId)` when the offer arrives, send `RejectIncomingAsync` and skip the prompt when blocked.
    Check. Offer from a blocked peer, confirm no prompt appears and a reject is sent.

## Nice to have

13. Empty conditional in `ChatSessionService.HandleIncomingEvent`.
    What. The handler contains an `if` block with no body after the dedupe check.
    Why. It hides the real control flow from anyone reading the receive path, which is the path finding 2 and finding 6 need to change.
    Fix. Invert the condition into an early return and delete the empty block.
    Check. Receive a duplicate event and confirm it is ignored.

14. Stray `reg.json` in the repo root.
    What. A gzip NuGet metadata file sits at `/Users/vlad/Work/between/reg.json`.
    Why. It has no role in the app and pollutes searches and diffs.
    Fix. Delete it and add the pattern to `.gitignore` if the tooling recreates it.
    Check. `git status --short` stays clean after a build.

15. `CRDT.md` is still untracked.
    What. The design rationale exists on disk but is not in git.
    Why. The accepted full-replay tradeoff and the wire-format rules disappear for anyone who clones the repo.
    Fix. Commit it with the CRDT changes.
    Check. `git ls-files CRDT.md` prints the path.

16. No live cross-client replay test exists.
    What. Verification covered builds, unit tests, and integration tests with local peers. No run exercised offline queueing and reconnect between the Swift app and the .NET app on two devices.
    Why. The findings above about empty history, marker clocks, and sender attribution all sit on that path.
    Fix. Add a repeatable two-client scenario that goes offline, sends both ways, reconnects, and asserts identical visible transcripts.
    Check. Run it after the must-fix items land.

Verdict: Do not ship this state. Four issues break either message confidentiality or the promise that old messages survive reconnect and disappear when wiped. The rest can follow in the next pass.

Lean: Fix the nonce, rebuild the .NET view from the database on connect, write clear markers in both Swift reset paths, then collapse receive paths onto the tested merge. Those four changes cover the security breach and the three ways history behaves incorrectly. The remaining items are smaller and can be batched.

Not checked: real two-device cross-client behavior, iOS on physical hardware, signaling server behavior under many simultaneous clients, App Store and backup restore flows, performance with more than a few thousand messages, and tampered ciphertext handling beyond the existing unit vectors.
