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
            // Local rows hold plaintext; re-encrypt with the static shared key so the
            // peer can decrypt the replayed op.
            guard let payload = encryptP2PMessage(msg.content, peerPublicKeyBase64: peer) else { continue }
            let event = Event(id: msg.event ?? msg.id,
                              type: .send,
                              payload: payload,
                              replyingTo: msg.replyingTo?.uuidString,
                              author: msg.author ?? msg.from,
                              ts: msg.ts ?? ChatCrdt.nowMs())
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
                              ts: tomb.ts ?? ChatCrdt.nowMs())
            webRTC.send(event)
        }
    }

    /// Decodes, deduplicates and stores an incoming add. A delete may arrive first —
    /// the stored tombstone then hides this message on insert.
    private func handleSend(_ event: Event) {
        let peer = webRTC.connectedTo
        let me = webRTC.localClientId
        let op = event.asOp(peer: peer, fallbackTs: ChatCrdt.nowMs())
        let eventID = UUID(uuidString: op.id) ?? event.id

        let duplicate = FetchDescriptor<Message>(predicate: #Predicate { $0.event == eventID })
        if let found = try? modelContext.fetch(duplicate), !found.isEmpty { return }

        guard let plaintext = decryptP2PMessage(event.payload, peerPublicKeyBase64: peer) else { return }
        let msg = Message(content: plaintext,
                          from: peer, to: me,
                          event: eventID,
                          replyingTo: event.replyingTo.flatMap { UUID(uuidString: $0) },
                          author: op.author,
                          ts: op.ts,
                          hidden: isSuppressed(op: op, peer: peer))
        modelContext.insert(msg)
        try? modelContext.save()
    }

    /// Stores a remote unsend as a tombstone and hides the target row. Keeping the
    /// op means a later replay cannot bring the message back.
    private func handleDelete(_ event: Event) {
        let peer = webRTC.connectedTo
        let me = webRTC.localClientId
        let op = event.asOp(peer: peer, fallbackTs: ChatCrdt.nowMs())

        let duplicate = FetchDescriptor<Tombstone>(predicate: #Predicate { $0.id == event.id })
        if let found = try? modelContext.fetch(duplicate), !found.isEmpty { return }

        let tomb = Tombstone(id: event.id,
                             targetEvent: event.payload,
                             from: peer, to: me,
                             author: op.author,
                             ts: op.ts)
        modelContext.insert(tomb)

        if let target = UUID(uuidString: event.payload) {
            let rows = try? modelContext.fetch(
                FetchDescriptor<Message>(predicate: #Predicate { $0.event == target }))
            rows?.forEach { $0.hidden = true }
        }
        try? modelContext.save()
    }

    /// Local suppression from a tombstone or a clear-history marker.
    private func isSuppressed(op: MessageOp, peer: String) -> Bool {
        let target = op.id
        let tombPredicate = #Predicate<Tombstone> { $0.targetEvent == target }
        if let found = try? modelContext.fetch(FetchDescriptor<Tombstone>(predicate: tombPredicate)),
           !found.isEmpty { return true }

        let ts = op.ts
        let clearPredicate = #Predicate<ConversationClear> { $0.peer == peer && $0.atMs >= ts }
        if let found = try? modelContext.fetch(FetchDescriptor<ConversationClear>(predicate: clearPredicate)),
           !found.isEmpty { return true }
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
                                wipeAlert()
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
                    case .send: handleSend(event)
                    case .delete: handleDelete(event)
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

struct wipeAlert: View {
    
    @Environment(\.modelContext) var modelContext
    
    var body: some View {
        Button("Messages") {
            Task {
                do {
                    try modelContext.delete(model: Message.self)
                    try modelContext.save()
                } catch {
                    print(error)
                }
            }
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
