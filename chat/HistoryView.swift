import SwiftUI
import SwiftData


struct HistoryView: View {
 
    @StateObject var webRTC: WebRTCManager
    @Environment(\.modelContext) var modelContext
    
    @Query var messages: [Message]
    
    @Query var contacts: [Contact]
    
    
    @State var searchQuery = ""
    
    private var filteredMessages: [Message] {
        let visible = messages.filter { !$0.hidden }
        if searchQuery.isEmpty {
            return visible
        }
        return visible.filter {
            $0.content.localizedCaseInsensitiveContains(searchQuery)
        }
    }
    
    var body: some View {
        NavigationStack {
            let uniqueConversations = filteredMessages
                .sorted { $0.timestamp > $1.timestamp }
                .reduce(into: [Message]()) { result, msg in
                    let peer = msg.from == webRTC.localClientId ? msg.to : msg.from
                    if !result.contains(where: { ($0.from == webRTC.localClientId ? $0.to : $0.from) == peer }) {
                        result.append(msg)
                    }
                }
            
            if uniqueConversations.count > 0 {
                
                List(uniqueConversations) { msg in
                    let peer = msg.from == webRTC.localClientId ? msg.to : msg.from
                    let contact = contacts.first(where: { $0.webRTCId == peer })
                    NavigationLink(value: msg) {
                        HistoryRowView(peer: peer, contact: contact)
                    }
                    .swipeActions(edge: .trailing, allowsFullSwipe: true) {
                        Button(role: .destructive) {
                            deleteMessages(from: peer)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                }
                .searchable(text: $searchQuery)
                .navigationDestination(for: Message.self) { msg in
                    let peer = msg.from == webRTC.localClientId ? msg.to : msg.from
                    ChatView(webRTC: webRTC, searchTarget: peer, searchTarget2: webRTC.localClientId, isViewingHistory: true, isTyping: .constant(false))
                }
                .navigationTitle("Chat History")
            } else {
                ContentUnavailableView {
                    Label("No chat history", systemImage: "clock.badge.xmark")
                }
                .navigationTitle("Chat History")
            }
        }
    }
    
    
    
    /// Local clear. Rows stay on disk (hidden) and per-author sequence markers keep
    /// older ops hidden when the peer replays them on a future connection.
    private func deleteMessages(from peer: String) {
        let predicate = #Predicate<Message> { msg in
            (msg.from == peer || msg.to == peer) && !msg.hidden
        }
        let rows = (try? modelContext.fetch(FetchDescriptor<Message>(predicate: predicate))) ?? []
        let me = webRTC.localClientId
        let at = ChatCrdt.nowMs()
        rows.forEach { $0.hidden = true }

        let peerSeq = rows.filter { $0.author == peer }.map(\.seq).max() ?? 0
        let selfSeq = rows.filter { $0.author == me }.map(\.seq).max() ?? 0

        let clearPredicate = #Predicate<ConversationClear> { $0.peer == peer }
        if let existing = try? modelContext.fetch(FetchDescriptor<ConversationClear>(predicate: clearPredicate)) {
            if let clear = existing.first {
                clear.atMs = max(clear.atMs, at)
                clear.peerSeq = max(clear.peerSeq, peerSeq)
                clear.selfSeq = max(clear.selfSeq, selfSeq)
                clear.selfId = me
            } else {
                modelContext.insert(ConversationClear(peer: peer, atMs: at,
                                                      peerSeq: peerSeq, selfSeq: selfSeq, selfId: me))
            }
        } else {
            modelContext.insert(ConversationClear(peer: peer, atMs: at,
                                                  peerSeq: peerSeq, selfSeq: selfSeq, selfId: me))
        }
        try? modelContext.save()
    }

    
}
