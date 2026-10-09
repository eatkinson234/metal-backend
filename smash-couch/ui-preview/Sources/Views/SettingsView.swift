import SwiftUI

struct SettingsView: View {
    @ObservedObject var model: LobbyStore
    let changeCapacity: (Int) -> Void
    @State(initialValue: false) private var diagnostics: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 26) {
            SectionTitle(title: "Settings", subtitle: "Applies to the next game.")
            VStack(alignment: .leading, spacing: 22) {
                HStack(alignment: .top) {
                    settingLabel("person.2", "Player slots", "Choose the number of players for this session.")
                    Spacer()
                    HStack(spacing: 5) {
                        ForEach(1...8, id: \.self) { count in
                            CouchActionButton(id: "capacity.\(count)") { changeCapacity(count) } label: {
                                Text("\(count)").font(.system(size: 14, weight: .semibold)).frame(width: 37, height: 39)
                                    .background(model.capacity == count ? CouchTheme.accent : CouchTheme.elevated, in: RoundedRectangle(cornerRadius: 9))
                                    .foregroundStyle(model.capacity == count ? CouchTheme.background : CouchTheme.text)
                            }.buttonStyle(.plain).disabled(model.running || model.starting).accessibilityLabel("\(count) player slots")
                                .accessibilityAddTraits(model.capacity == count ? .isSelected : [])
                        }
                    }
                }
                Divider().overlay(CouchTheme.line)
                CouchSwitch(id: "fullscreen", value: $model.fullscreen) { settingLabel("arrow.up.left.and.arrow.down.right", "Full screen", "Launch straight into the game, without a window border.") }
                    .toggleStyle(.switch).disabled(model.running || model.starting)
                    .accessibilityLabel("Full screen").accessibilityHint("Launch straight into the game, without a window border")
                Divider().overlay(CouchTheme.line)
                CouchSwitch(id: "sound", value: Binding(get: { !model.muted }, set: { model.muted = !$0 })) {
                    settingLabel("speaker.wave.2", "Game sound", "Keep the music and game audio on.")
                }.toggleStyle(.switch).disabled(model.running || model.starting)
                    .accessibilityLabel("Game sound").accessibilityHint("Keep the music and game audio on")
            }.couchPanel(padding: 28)
            HStack(alignment: .top, spacing: 16) {
                Image(systemName: "gamecontroller").font(.system(size: 25, weight: .light)).foregroundStyle(CouchTheme.accent)
                VStack(alignment: .leading, spacing: 6) {
                    Text("Controller presets").font(.system(size: 16, weight: .semibold))
                    Text("Choose the controller type and buttons for each player.").font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                }
                Spacer()
                CouchActionButton(id: "settings.controllers") { model.show(.controllers) } label: { Text("Manage controllers") }.buttonStyle(CouchButtonStyle(compact: true))
            }.couchPanel(padding: 24)
            DisclosureGroup(isExpanded: $diagnostics) {
                VStack(alignment: .leading, spacing: 16) {
                    Text("Optional tools for troubleshooting. Leave these off for everyday play.").font(.system(size: 13)).foregroundStyle(CouchTheme.secondary)
                    Toggle("Performance HUD", isOn: $model.hud).disabled(model.running || model.starting)
                    Toggle("GPU capture", isOn: $model.capture).disabled(model.running || model.starting)
                }.padding(.top, 16)
            } label: { Text("Advanced").font(.system(size: 13, weight: .semibold)) }
                .tint(CouchTheme.secondary).couchPanel(padding: 20)
            Text(model.starting ? "Wait for the game to finish starting before changing settings." : (model.running ? "Close the game normally to change session settings." : "Changes apply the next time you launch the game."))
                .font(.system(size: 12)).foregroundStyle(CouchTheme.secondary)
        }.frame(maxWidth: 1100, alignment: .leading)
    }
    private func settingLabel(_ symbol: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 16) {
            Image(systemName: symbol).font(.system(size: 19)).foregroundStyle(CouchTheme.secondary).frame(width: 25, height: 25)
            VStack(alignment: .leading, spacing: 7) {
                Text(title).font(.system(size: 16, weight: .semibold))
                Text(detail).font(.system(size: 12)).foregroundStyle(CouchTheme.secondary)
            }
        }
    }
}
