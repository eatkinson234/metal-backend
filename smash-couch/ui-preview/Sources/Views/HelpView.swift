import SwiftUI

struct HelpView: View {
    @ObservedObject var model: LobbyStore
    var body: some View {
        VStack(alignment: .leading, spacing: 24) {
            SectionTitle(title: "Controller help", subtitle: "Xbox, PlayStation, Switch, and other gamepads.")
            HStack(alignment: .top, spacing: 24) {
                VStack(alignment: .leading, spacing: 20) {
                    Text("Join a game").font(.system(size: 20, weight: .semibold))
                    Text("Connect by Bluetooth or USB. Select a player slot, then press the join button on that controller.")
                        .font(.system(size: 15)).foregroundStyle(CouchTheme.secondary)
                    joinRow("Xbox", button: "Menu")
                    joinRow("PlayStation", button: "Options")
                    joinRow("Switch", button: "+")
                    Text("Release the controls after joining, then select Play Smash. On the Play screen, the join button can also start the game.")
                        .font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                    Divider()
                    Text("Reconnect").font(.system(size: 16, weight: .semibold))
                    Text("A disconnected player's slot stays reserved. Select it and press the controller's join button to reconnect.")
                        .font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                }.couchPanel(padding: 26).frame(maxWidth: .infinity).accessibilityElement(children: .contain)
                VStack(alignment: .leading, spacing: 12) {
                    Text("Player \(model.book.selected + 1) controls").font(.system(size: 20, weight: .semibold))
                    Text("\(model.selectedFamily.title) · \(model.selectedLayout.title(for: model.selectedFamily))")
                        .font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                    ControlBindingsView(layout: model.selectedLayout, family: model.selectedFamily)
                    CouchActionButton(id: "help.layout") { model.show(.controllers) } label: { Text("Change controls") }
                        .buttonStyle(CouchButtonStyle(compact: true))
                }.couchPanel(padding: 26).frame(width: 340).accessibilityElement(children: .contain)
            }
            VStack(alignment: .leading, spacing: 12) {
                Text("Keyboard").font(.system(size: 18, weight: .semibold))
                Text("Tab selects a control. Space or Return activates it. Escape goes back.")
                    .font(.system(size: 14)).foregroundStyle(CouchTheme.secondary)
                HStack(spacing: 25) { shortcut("⌘1", "Play"); shortcut("⌘2", "Controllers"); shortcut("⌘,", "Settings") }
            }.couchPanel(padding: 22)
            Text("Keep Smash Couch open during a game. Controller vibration is available for identification in the lobby when supported.")
                .font(.system(size: 12)).foregroundStyle(CouchTheme.secondary)
        }
    }
    private func joinRow(_ name: String, button: String) -> some View {
        HStack { Text(name).font(.system(size: 15, weight: .medium)); Spacer(); ButtonBadge(text: button) }
            .accessibilityElement(children: .ignore).accessibilityLabel("\(name): press \(button) to join")
    }
    private func shortcut(_ key: String, _ label: String) -> some View {
        HStack(spacing: 8) { KeyCap(text: key); Text(label).font(.system(size: 13)).foregroundStyle(CouchTheme.secondary) }
    }
}
