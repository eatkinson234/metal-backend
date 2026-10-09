import SwiftUI

struct PlayerDetailView: View {
    @ObservedObject var model: LobbyStore
    let player: Int
    @State(initialValue: "") private var label: String
    @State(initialValue: "positions") private var layout: String
    @State(initialValue: ControllerFamily.automatic) private var family: ControllerFamily
    @State(initialValue: false) private var moreLayouts: Bool
    @State(initialValue: false) private var confirmClear: Bool
    @Environment(\.dismiss) private var dismiss

    init(model: LobbyStore, player: Int) {
        self.model = model; self.player = player
        _label = State(initialValue: model.profiles[player].label)
        _layout = State(initialValue: model.profiles[player].layout)
        _family = State(initialValue: model.profiles[player].controllerFamily)
        _moreLayouts = State(initialValue: ["letters", "positions_x_attack"].contains(model.profiles[player].layout))
    }
    private var resolvedFamily: ControllerFamily { family == .automatic ? model.detectedFamily(at: player) : family }
    private var preset: ControllerLayout { ControllerLayout(rawValue: layout) ?? .positions }
    private var locked: Bool { model.running || model.starting }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 18) {
                Text("Player \(player + 1)").font(.system(size: 24, weight: .bold))
                TextField("Controller name", text: $label).textFieldStyle(.roundedBorder).frame(maxWidth: 250).disabled(locked)
                    .accessibilityLabel("Controller name")
                    .onChange(of: label) { _, value in if value.count > 40 { label = String(value.prefix(40)) } }
                Spacer()
                Button("Cancel") { dismiss() }.buttonStyle(.plain).foregroundStyle(CouchTheme.secondary).keyboardShortcut(.cancelAction)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Controller type").font(.system(size: 13, weight: .semibold)).foregroundStyle(CouchTheme.secondary)
                        HStack(spacing: 8) {
                            ForEach(ControllerFamily.allCases) { choice in
                                CouchActionButton(id: "family.\(choice.rawValue)") { family = choice } label: {
                                    Text(choice.title).font(.system(size: 13, weight: .semibold)).frame(maxWidth: .infinity).padding(.vertical, 11)
                                        .background(family == choice ? CouchTheme.accent.opacity(0.09) : CouchTheme.elevated.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
                                        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(family == choice ? CouchTheme.accent : CouchTheme.line))
                                }.buttonStyle(.plain).disabled(locked).accessibilityAddTraits(family == choice ? .isSelected : [])
                            }
                        }

                    }
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Preset").font(.system(size: 13, weight: .semibold)).foregroundStyle(CouchTheme.secondary)
                        HStack(spacing: 10) {
                            ForEach([ControllerLayout.positions, .xboxAJump, .brawlhalla]) { choice in presetButton(choice) }
                        }
                        DisclosureGroup("More layouts", isExpanded: $moreLayouts) {
                            HStack(spacing: 10) { presetButton(.positionsXAttack); presetButton(.letters) }.padding(.top, 10)
                        }.font(.system(size: 12)).tint(CouchTheme.secondary)
                    }
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            Text(preset.title(for: resolvedFamily)).font(.system(size: 15, weight: .bold))
                            Spacer()
                            Text(resolvedFamily.title).font(.system(size: 12)).foregroundStyle(CouchTheme.secondary)
                        }.padding(.bottom, 5)
                        ControlBindingsView(layout: preset, family: resolvedFamily)
                    }.couchPanel(padding: 18)

                }.padding(5)
            }
            HStack {
                CouchActionButton(id: "identify.\(player)") { model.pulse(player) } label: { Label("Identify", systemImage: "waveform") }
                    .buttonStyle(CouchButtonStyle(compact: true)).disabled(model.book.slots[player].connection == nil)
                if model.book.slots[player].reserved {
                    Button("Remove player", role: .destructive) { confirmClear = true }.buttonStyle(.plain).disabled(locked)
                }
                Spacer()
                Button("Done") { save() }.buttonStyle(CouchButtonStyle(prominent: true, compact: true)).keyboardShortcut(.defaultAction)
                    .disabled(label.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }.padding(24).frame(width: 650, height: 560)
            .background(CouchTheme.background).foregroundStyle(CouchTheme.text).preferredColorScheme(.light)
            .environmentObject(model)
            .onAppear {
                model.registerFocus("modal.cancel") { dismiss() }; model.registerFocus("modal.done") { save() }
                updateFocusOrder()
            }
            .onChange(of: moreLayouts) { _, _ in updateFocusOrder() }
            .onDisappear { model.unregisterFocus("modal.cancel"); model.unregisterFocus("modal.done"); model.setModalOrder([]) }
            .alert("Remove Player \(player + 1)?", isPresented: $confirmClear) {
                Button("Cancel", role: .cancel) {}
                Button("Remove player", role: .destructive) { model.clear(player); dismiss() }
            } message: { Text("The slot becomes available. The saved name and preset are kept.") }
    }
    private func presetButton(_ choice: ControllerLayout) -> some View {
        CouchActionButton(id: "layout.\(choice.rawValue)") { layout = choice.rawValue } label: {
            HStack(spacing: 7) {
                if layout == choice.rawValue { Image(systemName: "checkmark").font(.system(size: 11, weight: .bold)) }
                Text(choice.title(for: resolvedFamily)).font(.system(size: 13, weight: .semibold)).lineLimit(1)
            }.frame(maxWidth: .infinity).padding(.vertical, 13)
                .foregroundStyle(layout == choice.rawValue ? Color.white : CouchTheme.text)
                .background(layout == choice.rawValue ? CouchTheme.accent : CouchTheme.surface, in: RoundedRectangle(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(layout == choice.rawValue ? Color.clear : CouchTheme.line))
        }.buttonStyle(.plain).disabled(locked).accessibilityAddTraits(layout == choice.rawValue ? .isSelected : [])
            .help(choice == .brawlhalla ? "Brawlhalla heavy/signature buttons become Special in Smash." : "")
    }
    private func updateFocusOrder() {
        let choices: [ControllerLayout] = [.positions, .xboxAJump, .brawlhalla] + (moreLayouts ? [.positionsXAttack, .letters] : [])
        model.setModalOrder(ControllerFamily.allCases.map { "family.\($0.rawValue)" } + choices.map { "layout.\($0.rawValue)" } + ["identify.\(player)", "modal.done", "modal.cancel"])
    }
    private func save() {
        let name = label.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.count <= 40 else { return }
        if !locked {
            model.profiles[player] = PlayerProfile(label: name, layout: layout, controllerFamily: family)
            model.actions.record(LobbyIntent(action: "profile", player: player, value: "\(family.rawValue):\(layout)"))
            model.revision += 1
        }
        dismiss()
    }
}
