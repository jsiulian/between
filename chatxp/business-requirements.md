# chatxp — Business Requirements

Cross-platform (.NET 10 / Avalonia) port of **Between**, an end-to-end encrypted peer-to-peer
chat application originally built in SwiftUI + WebRTC for iOS.

This document describes the functional requirements the port implements, the scope
boundaries, and the future-feature backlog.

---

## 1. Product overview

Between is a messaging app in which messages travel **directly between devices** over a
WebRTC data channel. A lightweight signaling server (the existing Node.js app in
`../server`) brokers only the initial handshake — it forwards connection offers, answers
and ICE candidates; it never sees message content. Every message payload is
end-to-end encrypted with a symmetric key derived from an ECDH key agreement between
the two devices' identity keys; the signaling server cannot decrypt anything.

### Users and identity
- A user's identity is the **Base64 raw public key** of their P-256 key pair
  (CryptoKit `rawRepresentation`: 64 bytes `X‖Y`). To chat, users exchange this identity
  string out of band (copy/paste, QR, etc.), exactly as the iOS app does.

## 2. Functional requirements (implemented)

### FR-1 Identity and keys
- FR-1.1 The app maintains a local long-term P-256 identity key pair used for all
  message encryption (ECDH + HKDF-SHA256, sharedInfo `P2P-Secure-Channel-Context`,
  32-byte AES-256-GCM key), byte-compatible with the iOS app's `Encrypt.swift`.
- FR-1.2 The private key is stored in a per-platform OS-protected store;
  message payloads at rest (local DB) remain encrypted ciphertext.
- FR-1.3 The user can view and copy their Base64 public key ("their ID").

### FR-2 Signaling
- FR-2.1 The app connects to a signaling server and registers its identity. The
  **registration server** (Settings → "My registration server" → Register; persisted
  as the startup default, default `ws://150.230.37.157:3030/`) is where the user is
  *reachable*: new contacts' connection requests arrive there. Since 2026-11-17 the
  server used to *reach a specific peer* is **per contact** (FR-7.2a). Status: ✅ done.
- FR-2.2 The app interoperates with the existing Node.js signaling server
  (`server/index.js`): register, offer/answer/candidate forwarding by target id,
  `not_found` replies, `reject` propagation. **The server is not ported** — it is
  consumed as-is.
- FR-2.3 Once a direct peer connection is established, the app **drops the signaling
  connection**; subsequent communication is purely peer-to-peer.

### FR-3 Connection lifecycle
- FR-3.1 The user can initiate a chat by entering a peer's public-key ID
  ("Connect" flow): offer → answer → trickle ICE, over the signaling server.
- FR-3.2 Incoming connection offers raise an accept/reject decision in the UI;
  offers from blocked users are auto-rejected.
- FR-3.3 Connection attempts time out after 15 seconds and surface the failure
  ("Connection attempt timed out", "rejected", "user not found").

### FR-4 Messaging (text)
- FR-4.1 Send text messages: the plaintext is encrypted with the shared key derived
  from the peer's public key, wrapped in a `send` Event, and pushed over the data
  channel. Payload wire format: Base64 `nonce‖ciphertext‖tag` (CryptoKit
  `AES.GCM.SealedBox.combined` compatible).
- FR-4.2 Receive and decrypt incoming events; display plaintext only after
  successful AES-GCM authentication (tampered ciphertext is dropped).
- FR-4.3 Reply-to: a message may reference a previous message id.
- FR-4.4 Delete: a user can delete a message locally and notify the peer with a
  `delete` event (identified by event id), which removes it on both sides.
- FR-4.5 Typing indicator: `typing` events indicating the peer is composing
  (throttled while typing, mirrored from the Swift app).
- FR-4.6 Images: payload prefixed with `B64__IMAGE:` carries a Base64 image
  (encrypted like any payload).

### FR-5 Local persistence
- FR-5.1 Messages (encrypted content, from/to identities, timestamp, event id,
  reply reference) persist locally in SQLite.
- FR-5.2 Chat history lists the most recent message per peer ("History" view),
  with both-message-directions conversations and clear-history per peer.
- FR-5.3 Contacts: save peer ids with human names and optional avatar images;
  rename and delete; unique by peer id.
- FR-5.4 Blocked users: block by id; blocked peers' offers are auto-rejected;
  the block list is viewable and manageable.

### FR-6 Voice calls — **deferred to Todo** (see §5.1)
The original app supports WebRTC audio calls (`CallView`, `enableAudio`, `endCall`
events) and incoming offer classification ("Voice Call" vs "Text Chat"). This port
implements **text chat only** for v1 (see below).

### FR-7 Contacts & contact requests ⏳ **in progress (2026-11-17)**
- FR-7.1 The app has a **Contacts screen**: list, add, edit (rename), delete.
  Status: ✅ done (Contacts tab; add/edit forms; delete button).
- FR-7.2 Adding a contact requires two fields: **Peer ID** (public key, as before)
  and **Nickname** (mandatory). The list displays nicknames; the underlying public
  key (and thus the ability to reconnect/resume chat later) is retained internally.
  Status: ✅ done (form validates both fields; list shows nickname + key preview;
  full key kept in SQLite contacts table).
- FR-7.2a **Per-contact signaling server** (2026-11-17 addition): each contact
  optionally carries its own signaling-server URL (ws://…). Connecting to, or
  auto-accepting a request from, that contact routes via the contact's stored
  server; blank = the currently-active/registration server (FR-2.1). First-time
  requests record the server the offer arrived on, so the accepted contact
  reconnects via the same server thereafter. The Settings page hosts the user's
  registration server ("My registration server" → Register), distinct from
  per-contact servers (configured in Contacts → add/edit). Status: ✅ done (schema
  column `contacts.signaling_server` with migration for existing DBs; forms; routing
  in `ConnectToContactAsync`/`HandleIncomingConnection`/`AcceptContactRequest`;
  tests serialize shared app-data via an xUnit collection).
- FR-7.3 **Auto-accept for known contacts**: when an offer arrives from a peer that
  already exists in the local contact list, the recipient's app accepts automatically
  (no prompt), because both parties have an established relationship. First-time
  senders (unknown peer id) follow FR-7.4. Status: ✅ done
  (`ChatSessionService.HandleIncomingConnection` checks the contacts table).
- FR-7.4 First-time contact requests keep the existing **Accept / Ignore** behavior
  (FR-3.2). Status: ✅ done (prompt is now the Notifications item below).
- FR-7.5 Chats are reopened per contact: selecting a contact reconnects and loads
  the stored (decrypted-on-display) history. Status: ✅ done (chat connect loads
  history lines before connecting).
- FR-7.6 **Chat navigation** (2026-11-18): clicking a contact's **Chat** button
  opens the chat surface with that contact — showing **who you're talking to**
  (nickname + key preview + connection state) at the top of the chat, with a
  back control to Contacts. The chat is NOT a tab (tabs made the peer ambiguous).
  Incoming auto-accepted conversations open the same surface. Status: ✅ done
  (chat is an overlay over the tab area; LeaveChat returns to the tabs without
  dropping the connection).

### FR-8 Notifications screen ⏳ **in progress (2026-11-17)**
- FR-8.1 A dedicated **Notifications screen** shows contact requests. Status: ✅
  done (Notifications tab + header badge linking to it).
- FR-8.2 A contact request **cannot be dismissed** without an explicit decision:
  it stays until the user clicks **Accept** or **Ignore**. Status: ✅ done
  (requests collection has no remove/close affordance other than the two buttons).
- FR-8.3 Accepting a request creates a contact (with a default nickname the user can
  rename in Contacts) and connects. Status: ✅ done.
- FR-8.4 The screen is designed to host other notification types in the future.
  Status: ✅ scaffolded (single ItemsControl for requests; placeholder note).

### FR-9 Message timestamps ⏳ **in progress (2026-11-17)**
- FR-9.1 **Display**: every message bubble shows its local timestamp (HH:mm).
  Status: ✅ done (bubble footer; both sent and received lines).
- FR-9.2 **Wire format (current state, documented — not yet changed)**: the `Event`
  JSON carries NO sender timestamp; messages are stamped **locally** on each side at
  store time. The sender's DB keeps the send time; the receiver's DB keeps the
  arrival time, so the two devices can show slightly different times for the same
  exchange (clock skew + channel-setting delay).
  *Recommendation (not implemented): add an optional `sentAt` field to `Event` —
  Swift Codable ignores unknown fields, so it is forward-compatible with the iOS
  app — and use `sentAt` when present, local receive time otherwise.*

## 3. Non-functional requirements

- NFR-1 **Cross-platform**: macOS, Windows, Linux via Avalonia (net10.0);
  pure-managed WebRTC (SIPSorcery) with **no native binary dependencies**.
- NFR-2 **End-to-end trust model limitations (inherited)**: static identity keys
  (no forward secrecy), no key authentication/MITM protection, and public keys
  must be verified out of band. Documented, unchanged from the original.
- NFR-3 **Key protection**: the iOS app uses the Secure Enclave-backed Keychain.
  The .NET port stores the key in an OS-protected store per platform (macOS
  Keychain/keychain-file, Windows DPAPI, Linux file-perms). **Hardware-backed key
  storage (Secure Enclave equivalent) is out of scope for this port** — noted as a
  limitation in the UI settings.
- NFR-4 **Interoperability**: crypto and wire formats are byte-compatible with the
  iOS app — a .NET user and an iOS user can chat (verified by CryptoKit-generated
  test vectors and the identical Event JSON shape).
- NFR-5 **Dark/light theme support** (2026-11-18, main requirement): the whole app
  renders correctly in the OS dark AND light theme — no unreadable text, no
  hardcoded single-theme colors. Implemented via a theme-dictionary palette
  (`App.axaml`: Chrome/Card/Form/Pill brushes, per-direction bubble background +
  text brushes, danger and ID-link accents) consumed with `DynamicResource`;
  `RequestedThemeVariant="Default"` follows the OS and restyles live. Status: ✅
  done (all surfaces migrated: header, contacts list/cards, forms, notifications,
  chat overlay, settings, diagnostics, footer, bubbles).

## 4. Wire formats (for interop reference)

| Thing | Format |
|---|---|
| Public identity | Base64 of CryptoKit `rawRepresentation` (64 bytes: X‖Y, no SEC1 0x04 prefix) |
| Encrypted payload | Base64 of `nonce(12) ‖ ciphertext ‖ tag(16)` (AES-GCM combined) |
| Event (data channel) | `{"id":"UPPER-UUID","type":"send","payload":"…","replyingTo":"…"}` (nulls omitted, camelCase) |
| Signaling | `{"type":"offer","from":"…","to":"…","sdp":"…"}` + `sdpMid`/`sdpMLineIndex` for candidates |

## 5. Todo (future features)

### 5.1 Voice calls (prominent)
- [ ] Audio call initiation: user selects a contact, app initiates a P2P connection
  with `enableAudio = true`; the SDP contains `m=audio`, signaling "Voice Call" to
  the callee (mirroring the Swift offer classifier).
- [ ] Call UI: contact avatar/name, live call timer, answer/end buttons, mute
  toggle (`setAudioEnabled`).
- [ ] RTP audio: capture/playback via platform audio APIs over the WebRTC audio
  transport; keep E2EE semantics (SRTP) of the original.
- [ ] `endCall` event + call-ended UI states.
- [ ] Incoming "Voice Call" vs "Text Chat" offer classification.

### 5.2 Other backlog
- [ ] QR-code exchange of public-key identities.
- [ ] Settings UI: signaling server picker, key reset (with re-auth warning),
  theme, notifications.
- [ ] Message delivery/read receipts.
- [ ] File transfer beyond images.
- [ ] Multi-device / key sync.
- [ ] UI polish: message bubbles/styling parity with the iOS app (MessageRowView),
  drag-and-drop image attachment, contact avatar picker.
- [ ] Installer/packaging: macOS `.app` bundle, Windows MSIX, Linux AppImage.
