import SwiftUI
import SwiftData


struct ContentView: View {
 
    @StateObject var webRTC = WebRTCManager()
    @Environment(\.modelContext) var modelContext
    @Environment(\.scenePhase) var scenePhase
    
    @Query var messages: [Message]
    
    @Query var contacts: [Contact]
    
    @State var showWipeAlert = false
    
    @State var isTyping = false
    
    @State var tab = ""
    
    func incomingName(for peerId: String?) -> String {
        return contacts.first(where: { $0.webRTCId == peerId })?.humanName
            ?? peerId
            ?? "Someone"
    }

    /// Sends every stored op for the peer. Ids and metadata are preserved so the
    /// receiver deduplicates and orders identically, regardless of delivery order.
    private func replayOps(for peer: String) {
        let me = webRTC.localClientId
        guard !peer.isEmpty else { return }

        let messagePredicate = #Predicate<Message> { msg in
            (msg.from == peer && msg.to == me) || (msg.from == me && msg.to == peer)
        }
        let stored = (try? modelContext.fetch(FetchDescriptor<Message>(predicate: messagePredicate))) ?? []
        for msg in stored {
            // Replay the original wire payload when we have it. Re-encrypting would
            // change the nonce and give the same op a different payload on each replay.
            guard let payload = msg.payload ?? encryptP2PMessage(msg.content, peerPublicKeyBase64: peer) else { continue }
            let event = Event(id: msg.event ?? msg.id,
                              type: .send,
                              payload: payload,
                              replyingTo: msg.replyingTo?.uuidString,
                              author: msg.author ?? msg.from,
                              ts: msg.ts ?? ChatCrdt.nowMs(),
                               seq: msg.seq)
            webRTC.send(event)
        }

        let tombPredicate = #Predicate<Tombstone> { t in
            (t.from == peer && t.to == me) || (t.from == me && t.to == peer)
        }
        let tombstones = (try? modelContext.fetch(FetchDescriptor<Tombstone>(predicate: tombPredicate))) ?? []
        for tomb in tombstones {
            let event = Event(id: tomb.id,
                              type: .delete,
                              payload: tomb.targetEvent,
                              author: tomb.author ?? tomb.from,
                              ts: tomb.ts ?? ChatCrdt.nowMs(),
                               seq: tomb.seq)
            webRTC.send(event)
        }
    }

    /// Stored add or tombstone for an op id, rebuilt so the live receive path can
    /// merge with the same function the tests exercise.
    private func existingOp(for id: String) -> MessageOp? {
        let me = webRTC.localClientId
        guard let uuid = UUID(uuidString: id) else { return nil }

        let addPredicate = #Predicate<Message> { $0.event == uuid }
        if let rows = try? modelContext.fetch(FetchDescriptor<Message>(predicate: addPredicate)),
           let m = rows.first {
            let from = m.from
            return MessageOp(id: id,
                             kind: .send,
                             author: m.author ?? from,
                             ts: m.ts ?? Int64(m.timestamp.timeIntervalSince1970 * 1000),
                             payload: m.payload ?? m.content,
                             replyingTo: m.replyingTo?.uuidString,
                             peer: from == me ? m.to : from,
                             seq: m.seq)
        }

        let tombPredicate = #Predicate<Tombstone> { $0.id == uuid }
        if let rows = try? modelContext.fetch(FetchDescriptor<Tombstone>(predicate: tombPredicate)),
           let t = rows.first {
            let from = t.from
            return MessageOp(id: id,
                             kind: .delete,
                             author: t.author ?? from,
                             ts: t.ts ?? ChatCrdt.nowMs(),
                             payload: t.targetEvent,
                             replyingTo: nil,
                             peer: from == me ? t.to : from,
                             seq: t.seq)
        }
        return nil
    }

    /// Removes the stored row for a losing op. If it was a tombstone, restore the
    /// target unless another tombstone still covers it.
    private func removeStoredOp(for id: String) {
        guard let uuid = UUID(uuidString: id) else { return }

        let tombPredicate = #Predicate<Tombstone> { $0.id == uuid }
        if let tombs = try? modelContext.fetch(FetchDescriptor<Tombstone>(predicate: tombPredicate)),
           let tomb = tombs.first {
            let target = tomb.targetEvent
            let others = (try? modelContext.fetch(FetchDescriptor<Tombstone>(
                predicate: #Predicate { $0.targetEvent == target && $0.id != uuid }))) ?? []
            if others.isEmpty, let targetID = UUID(uuidString: target) {
                let targetPredicate = #Predicate<Message> { $0.event == targetID }
                let rows = (try? modelContext.fetch(
                    FetchDescriptor<Message>(predicate: targetPredicate))) ?? []
                rows.forEach { $0.hidden = false }
            }
            modelContext.delete(tomb)
        }

        let addPredicate = #Predicate<Message> { $0.event == uuid }
        let rows = (try? modelContext.fetch(FetchDescriptor<Message>(predicate: addPredicate))) ?? []
        rows.forEach { modelContext.delete($0) }
    }

    /// Merges an incoming op with the copy already stored under the same id. The
    /// winner is decided once by ChatCrdt, so delivery order cannot change the result.
    private func handleOp(_ event: Event) {
        let peer = webRTC.connectedTo
        let me = webRTC.localClientId
        let incoming = event.asOp(peer: peer, fallbackTs: ChatCrdt.nowMs())
        let existing = existingOp(for: incoming.id)
        let winner = ChatCrdt.merge(existing.map { [$0] } ?? [], [incoming]).first!
        if let existing, winner == existing { return }

        var plaintext: String?
        if winner.kind == .send {
            plaintext = decryptP2PMessage(winner.payload, peerPublicKeyBase64: peer)
            guard plaintext != nil else { return }
        }

        if existing != nil { removeStoredOp(for: winner.id) }

        let mine = winner.author == me
        if winner.kind == .send {
            let eventID = UUID(uuidString: winner.id) ?? event.id
            let msg = Message(content: plaintext!,
                              from: mine ? me : peer, to: mine ? peer : me,
                              event: eventID,
                              replyingTo: winner.replyingTo.flatMap { UUID(uuidString: $0) },
                              author: winner.author,
                              ts: winner.ts,
                              seq: winner.seq,
                              payload: winner.payload,
                              hidden: isSuppressed(op: winner, peer: peer))
            modelContext.insert(msg)
        } else {
            let tomb = Tombstone(id: UUID(uuidString: winner.id) ?? event.id,
                                 targetEvent: winner.payload,
                                 from: mine ? me : peer, to: mine ? peer : me,
                                 author: winner.author,
                                 ts: winner.ts,
                                 seq: winner.seq)
            modelContext.insert(tomb)

            if let target = UUID(uuidString: winner.payload) {
                let targetPredicate = #Predicate<Message> { $0.event == target }
                let rows = (try? modelContext.fetch(
                    FetchDescriptor<Message>(predicate: targetPredicate))) ?? []
                rows.forEach { $0.hidden = true }
            }
        }
        try? modelContext.save()
    }

    /// Local suppression from a tombstone or a clear-history marker. Per-author
    /// sequence values avoid comparing two device clocks.
    private func isSuppressed(op: MessageOp, peer: String) -> Bool {
        let target = op.id
        let tombPredicate = #Predicate<Tombstone> { $0.targetEvent == target }
        if let found = try? modelContext.fetch(FetchDescriptor<Tombstone>(predicate: tombPredicate)),
           !found.isEmpty { return true }

        let predicate = #Predicate<ConversationClear> { $0.peer == peer }
        let clears = (try? modelContext.fetch(FetchDescriptor<ConversationClear>(predicate: predicate))) ?? []
        for clear in clears {
            if op.seq > 0 {
                if op.author == clear.selfId && op.seq <= clear.selfSeq { return true }
                if op.author != clear.selfId && op.seq <= clear.peerSeq { return true }
            } else if clear.atMs >= op.ts {
                return true
            }
        }
        return false
    }

    var body: some View {
        TabView(selection:$tab) {
            NavigationStack {
                if !webRTC.isPeerConnected {
                    ConnectView(webRTC: webRTC, tab: $tab)
                        .padding()
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .onTapGesture(count: 3) {
                            showWipeAlert = true
                        }
                        .navigationTitle("Connect")
                } else {
                    if webRTC.enableAudio {
                        CallView(webRTC: webRTC, searchTarget: webRTC.connectedTo)
                            .padding()
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ChatView(webRTC: webRTC, searchTarget: webRTC.connectedTo, searchTarget2: webRTC.localClientId, isTyping:$isTyping)
                            .padding()
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .onTapGesture(count: 3) {
                                showWipeAlert = true
                            }
                            .alert("What do you want to wipe?", isPresented: $showWipeAlert) {
                                wipeAlert(me: webRTC.localClientId)
                            }
                    }
                }
            }.tabItem {
                Image(systemName: "bubble")
            }
            .tag("Connect")
            
            ContactsView()
                .tabItem {
                    Image(systemName: "person.crop.circle")
                }
                .tag("Contacts")
            
            HistoryView(webRTC: webRTC)
                .tabItem {
                    Image(systemName: "clock")
                }
                .tag("History")
        }
        .alert("Incoming Connection", isPresented: $webRTC.showConnectionAlert) {
            Button("Accept") {
                tab = "Connect"
                webRTC.connectionDecisionCallback?(true)
                webRTC.connectedTo = webRTC.incomingConnectionPeerId ?? ""
                webRTC.connectionDecisionCallback = nil
                webRTC.incomingConnectionPeerId = nil
            }
            Button("Reject", role: .cancel) {
                webRTC.connectionDecisionCallback?(false)
                webRTC.connectionDecisionCallback = nil
                webRTC.incomingConnectionPeerId = nil
            }
            
        } message: {
            Text("\(incomingName(for: webRTC.incomingConnectionPeerId)) is requesting a \(webRTC.incomingConnectionType).")
        }
        .onAppear {
            webRTC.modelContext = modelContext
            
            webRTC.onPeerOpened = { peer in
                DispatchQueue.main.async { replayOps(for: peer) }
            }
            webRTC.onMessage = { event in
                DispatchQueue.main.async {
                    switch event.type {
                    case .send, .delete: handleOp(event)
                    case .typing: isTyping = event.payload == "true"
                    case .endCall:
                        webRTC.disconnect()
                        webRTC.connectedTo = ""
                    default: break
                    }
                }
            }
        }
        .onChange(of: scenePhase) { _, newPhase in
            if newPhase == .active {
                if !webRTC.isPeerConnected {
                    webRTC.register()
                }
            }
        }
    }
    
}

private func wipeAllMessages(modelContext: ModelContext, me: String,
                             messages: [Message], tombstones: [Tombstone]) {
    let peers = Set(messages.flatMap { [$0.from, $0.to] }
                    + tombstones.flatMap { [$0.from, $0.to] })
        .filter { !$0.isEmpty && $0 != me }
    let at = ChatCrdt.nowMs()

    for peer in peers {
        let seqs = messages + tombstones.map { tomb in
            Message(content: "", from: tomb.from, to: tomb.to, event: tomb.id,
                    replyingTo: nil, author: tomb.author, ts: tomb.ts, seq: tomb.seq)
        }
        let peerSeq = seqs.filter { $0.author == peer }.map(\.seq).max() ?? 0
        let selfSeq = seqs.filter { $0.author == me }.map(\.seq).max() ?? 0

        let predicate = #Predicate<ConversationClear> { $0.peer == peer }
        let existing = (try? modelContext.fetch(FetchDescriptor<ConversationClear>(predicate: predicate))) ?? []
        if let clear = existing.first {
            clear.atMs = max(clear.atMs, at)
            clear.peerSeq = max(clear.peerSeq, peerSeq)
            clear.selfSeq = max(clear.selfSeq, selfSeq)
            clear.selfId = me
        } else {
            modelContext.insert(ConversationClear(peer: peer, atMs: at,
                                                  peerSeq: peerSeq, selfSeq: selfSeq, selfId: me))
        }
    }

    do {
        try modelContext.delete(model: Message.self)
        try modelContext.delete(model: Tombstone.self)
        try modelContext.save()
    } catch {
        print(error)
    }
}

struct wipeAlert: View {
    @Environment(\.modelContext) var modelContext
    @Query var messages: [Message]
    @Query var tombstones: [Tombstone]
    var me: String

    var body: some View {
        Button("Messages") {
            wipeAllMessages(modelContext: modelContext, me: me,
                            messages: messages, tombstones: tombstones)
        }
        Button("Contacts") {
            Task {
                do {
                    try modelContext.delete(model: Contact.self)
                    try modelContext.save()
                } catch {
                    print(error)
                }
            }
        }
        Button("Cancel", role: .cancel) {

        }
    }
}
