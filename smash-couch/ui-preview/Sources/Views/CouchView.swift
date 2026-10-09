import SwiftUI

struct CouchView: View {
    @ObservedObject var model: LobbyStore
    @State(initialValue: nil) private var editingPlayer: Int?
    @State(initialValue: nil) private var pendingCapacity: Int?
    @State(initialValue: false) private var showCapacityWarning: Bool
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GeometryReader { geometry in
            VStack(spacing: 0) {
                header.padding(.horizontal, 28)
                Rectangle().fill(CouchTheme.line).frame(height: 1)
                ScrollView {
                    VStack(alignment: .leading, spacing: 24) {
                        switch model.page {
                        case .play: home(width: geometry.size.width, height: geometry.size.height)
                        case .controllers: ControllersView(model: model, dense: geometry.size.height < 680, conciseInstructions: geometry.size.height < 850, editPlayer: { editingPlayer = $0 }, changeCapacity: changeCapacity)
                        case .settings: SettingsView(model: model, changeCapacity: changeCapacity)
                        case .help: HelpView(model: model)
                        }
                        if !model.message.isEmpty {
                            HStack(alignment: .top, spacing: 10) {
                                Image(systemName: "info.circle").foregroundStyle(CouchTheme.accent)
                                Text(model.message).font(.system(size: 13)).foregroundStyle(CouchTheme.secondary).textSelection(.enabled)
                            }.padding(.horizontal, 4).accessibilityElement(children: .ignore)
                                .accessibilityLabel(model.message).accessibilityAddTraits(.isStaticText)
                        }
                    }
                    .frame(maxWidth: 1440).padding(geometry.size.height < 680 ? 20 : 28).frame(maxWidth: .infinity)
                }.clipped()
                footer.padding(.horizontal, 36).background(CouchTheme.background)
            }
            .background(CouchTheme.background).foregroundStyle(CouchTheme.text)
        }
        .preferredColorScheme(.light)
        .environmentObject(model)
        .environment(\.couchReduceMotionOverride, model.reduceMotionOverride)
        .sheet(isPresented: Binding(get: { editingPlayer != nil }, set: { if !$0 { editingPlayer = nil } })) {
            if let player = editingPlayer { PlayerDetailView(model: model, player: player) }
        }
        .alert("Remove players from this lobby?", isPresented: $showCapacityWarning) {
            Button("Cancel", role: .cancel) { pendingCapacity = nil }
            Button("Change player count", role: .destructive) {
                if let count = pendingCapacity { model.changeCapacity(count) }; pendingCapacity = nil
            }
        } message: { Text("Players beyond the new count will leave the lobby. Their saved labels and button layouts will be kept.") }
    }

    private var header: some View {
        HStack(spacing: 34) {
            CouchActionButton(id: "brand") { model.show(.play) } label: {
                HStack(spacing: 10) {
                    Image(systemName: "gamecontroller.fill").font(.system(size: 26)).foregroundStyle(CouchTheme.accent)
                    Text("Smash Couch").font(.system(size: 18, weight: .bold))
                }
            }.buttonStyle(.plain).accessibilityLabel("Smash Couch home")
            HStack(spacing: 30) {
                ForEach([LobbyPage.play, .controllers, .settings], id: \.self) { page in
                    CouchActionButton(id: "nav.\(page.rawValue)") { model.show(page) } label: {
                        VStack(spacing: 0) {
                            Spacer()
                            Text(page.title).font(.system(size: 14, weight: model.page == page ? .bold : .medium))
                                .foregroundStyle(model.page == page ? CouchTheme.text : CouchTheme.secondary)
                            Spacer()
                            Capsule().fill(model.page == page ? CouchTheme.accent : Color.clear).frame(height: 3)
                        }.frame(width: 90, height: 62)
                    }.buttonStyle(.plain).accessibilityAddTraits(model.page == page ? .isSelected : [])
                }
            }
            Spacer(minLength: 12)
            HStack(spacing: 8) {
                Image(systemName: "gamecontroller").font(.system(size: 14))
                Text("\(model.connectedCount) connected").font(.system(size: 12, weight: .medium)).monospacedDigit()
            }.foregroundStyle(CouchTheme.secondary)
            CouchActionButton(id: "nav.help") { model.show(.help) } label: { Image(systemName: "questionmark.circle").font(.system(size: 21)) }
                .buttonStyle(.plain).foregroundStyle(model.page == .help ? CouchTheme.accent : CouchTheme.secondary)
                .accessibilityLabel("How to play and controller help").help("How to play (⌘?)")
        }.frame(height: 62)
    }

    private func home(width: CGFloat, height: CGFloat) -> some View {
        let dense = height < 680
        return VStack(spacing: dense ? 16 : 22) {
            HeroView(model: model, compact: width < 1120, short: dense, height: dense ? 212 : 280)
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    Text("Players").font(.system(size: 22, weight: .semibold))
                    Text("\(model.readiness.joined) joined").font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                    Spacer()
                    CouchActionButton(id: "manage") { model.show(.controllers) } label: { Label("Controller settings", systemImage: "slider.horizontal.3") }
                        .buttonStyle(.plain).font(.system(size: 13, weight: .semibold)).foregroundStyle(CouchTheme.accent)
                }
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 12), count: min(model.capacity, model.capacity > 4 ? 4 : model.capacity)), spacing: 12) {
                    ForEach(0..<model.capacity, id: \.self) { i in
                        PlayerTile(model: model, player: i, compact: true, dense: dense || model.capacity > 4 || model.capacity == 1) { model.select(i); editingPlayer = i }
                    }
                }
            }.couchPanel(padding: dense ? 16 : 22)
        }
    }

    private var footer: some View {
        HStack(spacing: 20) {
            HStack(spacing: 7) { KeyCap(text: "← →"); Text("Select player") }
            HStack(spacing: 7) { KeyCap(text: "Menu / Options / +"); Text("Join") }
            Spacer()
            Text("DESIGN PREVIEW").tracking(1.3).font(.system(size: 9, weight: .bold)).foregroundStyle(CouchTheme.accent)
            Text("Game launch disabled").font(.system(size: 10))
        }.font(.system(size: 11)).foregroundStyle(CouchTheme.secondary).frame(height: 42)
            .overlay(alignment: .top) { Rectangle().fill(CouchTheme.line).frame(height: 1) }
    }

    private func changeCapacity(_ count: Int) {
        guard count < model.capacity && model.book.slots[count..<model.capacity].contains(where: { $0.reserved || $0.connection != nil }) else {
            model.changeCapacity(count); return
        }
        pendingCapacity = count; showCapacityWarning = true
    }
}
