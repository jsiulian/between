import Foundation
import SwiftData

let DEFAULT_SIGNAL_SERVER = "wss://150.230.37.157:3030/"

enum Config {
    static let appGroupIdentifier = "com.bryce.chat"
    static var sharedDefaults: UserDefaults? {
        return UserDefaults(suiteName: appGroupIdentifier)
    }
}

struct User {
    
    var publicKey: String
    let privateKey: String
    
}


enum EventTypes: String, Codable {
    case send = "send"
    case delete = "delete"
    case offer = "offer"
    case answer = "answer"
    case candidate = "candidate"
    case incoming = "incoming"
    case typing = "typing"
    case endCall = "endCall"
}

struct Event: Codable, Identifiable {
    var id = UUID()
    var type: EventTypes
    var payload: String
    var replyingTo: String?
    // CRDT metadata. Swift's synthesized Codable omits nil optionals, so an older
    // client sees exactly the JSON it already understands.
    var author: String?
    var ts: Int64?
    /// Per-author Lamport counter used by clear markers. Nil for legacy senders.
    var seq: Int64?
}

@Model
final class Message {
    @Attribute(.unique) var id: UUID
    var content: String
    var from: String
    var to: String
    var timestamp: Date
    var event: UUID?
    var replyingTo: UUID?
    // CRDT op metadata
    var author: String?
    var ts: Int64?
    var seq: Int64 = 0
    /// Wire payload as received, so replay is byte-identical and CRDT comparison
    /// uses the same ciphertext both clients store.
    var payload: String?
    var hidden: Bool = false

    init(id: UUID = UUID(), content: String, from: String, to: String, event: UUID,
         replyingTo: UUID?, author: String? = nil, ts: Int64? = nil,
         seq: Int64 = 0, payload: String? = nil, hidden: Bool = false) {
        self.id = id
        self.content = content
        self.from = from
        self.to = to
        self.event = event
        self.replyingTo = replyingTo
        self.author = author
        self.ts = ts
        self.seq = seq
        self.payload = payload
        self.hidden = hidden
        if let ts {
            self.timestamp = Date(timeIntervalSince1970: Double(ts) / 1000.0)
        } else {
            self.timestamp = Date()
        }
    }
}

/// Remote unsend. Kept as a tombstone so a later replay cannot resurrect the add,
/// including when the delete arrives before the message it targets.
@Model
final class Tombstone {
    @Attribute(.unique) var id: UUID
    var targetEvent: String
    var from: String
    var to: String
    var author: String?
    var ts: Int64?
    var seq: Int64 = 0

    init(id: UUID = UUID(), targetEvent: String, from: String, to: String,
         author: String? = nil, ts: Int64? = nil, seq: Int64 = 0) {
        self.id = id
        self.targetEvent = targetEvent
        self.from = from
        self.to = to
        self.author = author
        self.ts = ts
        self.seq = seq
    }
}

/// Local clear marker. Newer clients compare per-author `peerSeq`/`selfSeq`, so
/// device clock skew cannot change what a clear hides. `atMs` remains for legacy ops.
@Model
final class ConversationClear {
    @Attribute(.unique) var peer: String
    var atMs: Int64
    var peerSeq: Int64 = 0
    var selfSeq: Int64 = 0
    var selfId: String = ""

    init(peer: String, atMs: Int64, peerSeq: Int64 = 0, selfSeq: Int64 = 0, selfId: String = "") {
        self.peer = peer
        self.atMs = atMs
        self.peerSeq = peerSeq
        self.selfSeq = selfSeq
        self.selfId = selfId
    }
}

@Model
final class Contact {
    @Attribute(.unique) var id: UUID
      var webRTCId: String
    var humanName: String
    @Attribute(.externalStorage) var image: Data?
    var timestamp: Date
      
    init(id: UUID = UUID(), webRTCid: String, humanName:String, image: Data? = nil) {
          self.id = id
          self.webRTCId = webRTCid
        self.humanName = humanName
        self.image = image
        self.timestamp = Date()
      }
}

@Model
final class BlockedUser {
    var id: UUID
    var webRTCid: String
    
    init(webRTCid: String) {
        self.id = UUID()
        self.webRTCid = webRTCid
    }
    
}
