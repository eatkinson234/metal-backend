import SwiftUI

struct PlayerTile: View {
    @ObservedObject var model: LobbyStore
    let player: Int
    var compact = false
    var dense = false
    let action: () -> Void
    private var slot: SlotState { model.book.slots[player] }
    private var joined: Bool { slot.connection != nil }
    private var selected: Bool { model.book.selected == player }
    private var tint: Color { CouchTheme.players[player] }
    private var status: String { joined ? (slot.ready ? "Ready" : "Release controls") : (slot.reserved ? "Reconnect" : "Join") }

    var body: some View {
        CouchActionButton(id: "player.\(player)", action: action) {
            if dense {
                HStack(spacing: 8) {
                    Text("P\(player + 1)").font(.system(size: 11, weight: .bold)).foregroundStyle(tint)
                    Image(systemName: joined ? "gamecontroller.fill" : (slot.reserved ? "wifi.slash" : "plus")).foregroundStyle(joined ? tint : CouchTheme.secondary)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.profiles[player].label).font(.system(size: model.capacity == 1 ? 14 : 11, weight: .semibold)).lineLimit(1)
                        Text("\(model.controllerFamily(at: player).title) · \(status)").font(.system(size: model.capacity == 1 ? 12 : 10, weight: .medium)).foregroundStyle(joined && slot.ready ? CouchTheme.mint : CouchTheme.secondary).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }.padding(12).frame(maxWidth: .infinity).frame(height: 48)
                    .background(CouchTheme.elevated.opacity(0.6), in: RoundedRectangle(cornerRadius: 10))
                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? CouchTheme.accent : CouchTheme.line))
            } else {
            VStack(spacing: 10) {
                HStack {
                    Text("P\(player + 1)").font(.system(size: 11, weight: .bold, design: .rounded)).foregroundStyle(joined ? tint : CouchTheme.secondary)
                    Spacer(minLength: 2)
                    if selected { Circle().fill(CouchTheme.accent).frame(width: 5, height: 5) }
                }
                Image(systemName: joined ? "gamecontroller.fill" : (slot.reserved ? "wifi.slash" : "plus"))
                    .font(.system(size: compact ? 23 : 28, weight: .light))
                    .foregroundStyle(joined ? tint : CouchTheme.secondary.opacity(0.65))
                    .frame(height: compact ? 25 : 32)
                if !compact {
                    VStack(spacing: 5) {
                        Text(model.profiles[player].label).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                        Text(joined ? model.controllerFamily(at: player).title : (slot.reserved ? "Slot reserved" : "Press \(model.controllerFamily(at: player).joinButton) to join"))
                            .font(.system(size: 11)).foregroundStyle(CouchTheme.secondary).lineLimit(1)
                    }
                }
                if compact { Text(model.profiles[player].label).font(.system(size: 10, weight: .semibold)).lineLimit(1) }
                HStack(spacing: 4) {
                    if joined && slot.ready { Circle().fill(CouchTheme.mint).frame(width: 4, height: 4) }
                    Text(status).font(.system(size: compact ? 10 : 11, weight: .medium)).lineLimit(1).minimumScaleFactor(0.8)
                }.foregroundStyle(joined && slot.ready ? CouchTheme.mint : CouchTheme.secondary)
            }
            .padding(compact ? 12 : 16)
            .frame(minWidth: compact ? 48 : 140, maxWidth: compact && model.capacity == 1 ? 100 : .infinity)
            .background(joined ? tint.opacity(0.075) : CouchTheme.elevated.opacity(0.3), in: RoundedRectangle(cornerRadius: compact ? 13 : 18))
            .overlay(RoundedRectangle(cornerRadius: compact ? 13 : 18).strokeBorder(selected ? CouchTheme.accent.opacity(0.9) : CouchTheme.line, lineWidth: selected ? 1.5 : 1))
            }
        }.buttonStyle(.plain)
            .accessibilityElement(children: .ignore)
            .accessibilityAddTraits(.isButton)
            .accessibilityLabel("Player \(player + 1), \(model.profiles[player].label), \(model.controllerFamily(at: player).title), \(status)\(selected ? ", selected slot" : "")")
            .accessibilityHint("Open this player's label and button layout")
            .help("Player \(player + 1): \(model.profiles[player].label) · \(status)")
    }
}

struct ControllersView: View {
    @ObservedObject var model: LobbyStore
    var dense = false
    var conciseInstructions = false
    let editPlayer: (Int) -> Void
    let changeCapacity: (Int) -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: dense ? 14 : 20) {
            HStack(alignment: .bottom) {
                if dense {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Controllers").font(.system(size: 28, weight: .bold)).tracking(-0.8)
                        Text("Select a player to change their controls.").font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                    }
                } else { SectionTitle(title: "Controllers", subtitle: "Select a player to choose a controller type and button preset.") }
                Spacer()
                Text("\(model.readiness.joined) of \(model.capacity) joined").font(.system(size: 13, weight: .medium)).foregroundStyle(CouchTheme.secondary)
            }
            HStack(spacing: 14) {
                Image(systemName: model.readiness.canPlay ? "checkmark.circle" : "gamecontroller").font(.system(size: 27, weight: .light)).foregroundStyle(CouchTheme.accent)
                VStack(alignment: .leading, spacing: 4) {
                    Text(model.readiness.title).font(.system(size: 17, weight: .semibold))
                    Text(model.readiness.detail).font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                }
                Spacer()
                CouchActionButton(id: "controllers.play") { model.requestPlay() } label: { Label(model.starting ? "Starting Smash…" : (model.running ? "Game is running" : "Play Smash"), systemImage: "play.fill") }
                    .buttonStyle(CouchButtonStyle(prominent: true, compact: true)).disabled(!model.readiness.canPlay)
            }.couchPanel(padding: dense ? 14 : 20)
            HStack {
                Eyebrow(text: "PLAYER SLOTS")
                Spacer()
                Text("Players").font(.system(size: 12)).foregroundStyle(CouchTheme.secondary)
                Picker("Number of player slots", selection: Binding(get: { model.capacity }, set: changeCapacity)) {
                    ForEach(1...8, id: \.self) { Text("\($0)").tag($0) }
                }.labelsHidden().frame(width: 70).disabled(model.running || model.starting)
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 16), count: 4), spacing: 16) {
                ForEach(0..<model.capacity, id: \.self) { player in
                    PlayerTile(model: model, player: player, dense: dense) { model.select(player); editPlayer(player) }
                }
            }
            Text("To join: Xbox Menu · PlayStation Options · Switch +")
                .font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
            if model.readiness.reserved > 0 {
                Label("Disconnected slots stay reserved. Select the slot and press your controller's join button.", systemImage: "arrow.triangle.2.circlepath")
                    .font(.system(size: 13)).foregroundStyle(CouchTheme.accent).couchPanel(padding: 18)
            }
            if model.unassignedCount > 0 {
                Text("\(model.unassignedCount) connected controller\(model.unassignedCount == 1 ? " is" : "s are") waiting to join.")
                    .font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
            }
        }
    }
    private func instruction(_ number: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 11) {
            Text(number).font(.system(size: 12, weight: .bold, design: .rounded)).foregroundStyle(CouchTheme.accent)
                .frame(width: 26, height: 26).background(CouchTheme.accent.opacity(0.09), in: Circle())
            VStack(alignment: .leading, spacing: 7) {
                Text(title).font(.system(size: 14, weight: .semibold))
                Text(detail).font(.system(size: 12)).foregroundStyle(CouchTheme.secondary).fixedSize(horizontal: false, vertical: true)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}
