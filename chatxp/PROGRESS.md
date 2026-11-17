# chatxp — Progress Notes

Last updated: 2026-11-17

## Status: **Port COMPLETE** ✅

All 8 plan steps done. Full test suite 27/27 green. Avalonia app builds and boots on
macOS arm64. See `README.md` for run instructions and `business-requirements.md` for
the requirements + voice-calls Todo.

## CRDT offline sync (added after the port)

Both clients now treat a conversation as an idempotent op log. `Event` gained optional
`author` and `ts` fields (omitted when nil, so legacy decoders are unaffected). Each
client replays its ops when the WebRTC data channel opens; deletes are tombstones and
local hides/clears survive replay. See `../CRDT.md` for the design and tradeoffs.

- Core: `chatxp.Core/Crdt/ChatCrdt.cs`, `chatxp.Core.Tests/CrdtTests.cs` (11 tests).
- Swift: `chat/Crdt.swift` plus event/model/sync changes in `Data.swift`, `ContentView.swift`,
  `ChatView.swift`, `HistoryView.swift`, `WebRTCManager.swift`, `chatApp.swift`.
- Verified: `dotnet test` and `xcodebuild -scheme chat -sdk iphonesimulator build` pass.

## What exists

| Piece | File | Proof |
|---|---|---|
| E2EE crypto (CryptoKit byte-compatible) | `chatxp.Core/Crypto/E2eeCrypto.cs` | CryptoVectorTests (4) — vectors from the real Swift flow |
| Signaling client (Node.js server interop) | `chatxp.Core/Signaling/SignalingClient.cs` | SignalingClientTests (5) — live server |
| WebRTC manager (SIPSorcery 10.0.17) | `chatxp.Core/WebRTC/WebRtcManager.cs` | EndToEndTests (1) — two peers, encrypted msg |
| Event wire model (Swift-compatible JSON) | `chatxp.Core/Models/Event.cs` | shape test + E2E |
| SQLite persistence | `chatxp.Core/Storage/ChatDatabase.cs` | ChatDatabaseTests (8) |
| Avalonia UI (Connect/Chat tabs) | `chatxp.App/**` | builds; boot smoke test passed |
| Requirements + voice-call Todo | `business-requirements.md` | — |

## Historical notes — corrections that overturned earlier assumptions (keep!)

1. **`ECDiffieHellman.Create()` defaults to P-521 on this Mac.** Always pass
   `ECCurve.NamedCurves.nistP256`. The original "-50 AppleCFError" was caused by
   importing mangled P-521 coords as P-256, NOT a macOS API limitation.
2. **Swift public-key wire format = CryptoKit `rawRepresentation` = 64 bytes `X‖Y`,
   no SEC1 0x04 prefix** (CryptoKit *rejects* the 65-byte prefixed form). The iOS app
   uses this string as the user identity everywhere (`rawRepresentation.base64EncodedString()`).
3. **`DeriveKeyFromHash` is NOT HKDF.** Hand-rolled RFC 5869 HKDF-SHA256 in `E2eeCrypto.HkdfSha256`
   (empty salt → HMAC key = 32 zero bytes). `AesGcm` ctor takes tag size in BYTES (16).
4. **`WebRTC.Client` (kekekeii) NuGet does not exist** — the original plan's WebRTC step
   was rewritten around **SIPSorcery 10.0.17** (pure C#, net10.0, osx-arm64, data channels,
   actively maintained; fork `SpawnDev.RTC` exists if browser interop issues surface).
5. Microsoft.Data.Sqlite bumped 9.0.0→11.0.0 to clear the NU1903 SQLitePCLRaw vulnerability.
6. Avalonia 12 API notes: `Dispatcher.UIThread.Invoke(Action)`, `TryGetFeature(Type)`
   non-generic, clipboard via `DataTransfer`/`DataTransferItem.SetText` (no `SetTextAsync`).

## Regenerating the CryptoKit vectors

`chatxp.Core.Tests/TestVectors/gen_vectors.swift` (same folder as the .b64 files:
vec_alice_pub, vec_bob_pub, vec_key, vec_ct). Run `swiftc gen_vectors.swift && ./gen`,
copy outputs over the four .b64 files, update CryptoVectorTests if scalars change.
Fixed scalars: Alice = 0x00..0x1F; Bob = DE AD BE EF || 0x00..0x1B.

## Remaining / future work (not blockers)

- Voice calls (see business-requirements.md §5.1) — SDP m=audio, CallView, RTP, endCall.
- Per-platform OS keychain integration (currently PKCS#8 file, user-only perms, app-data dir).
- .NET↔iOS live interop test across devices (wire-format compat is proven via vectors;
  a live cross-device test still needs manual setup on two machines).
- History/Contacts/Blocked UI tabs (persistence layer + queries exist; iOS-parity UI is Todo).
- Image sending (B64__IMAGE: prefix convention documented in business-requirements.md).
