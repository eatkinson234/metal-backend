import SwiftUI

private struct CouchReduceMotionKey: EnvironmentKey { static let defaultValue = false }
extension EnvironmentValues {
    var couchReduceMotionOverride: Bool {
        get { self[CouchReduceMotionKey.self] }
        set { self[CouchReduceMotionKey.self] = newValue }
    }
}

enum CouchTheme {
    static let arenaImage: NSImage? = Bundle.main.url(forResource: "arena", withExtension: "png").flatMap { NSImage(contentsOf: $0) }
    static let background = Color(red: 0.955, green: 0.961, blue: 0.967)
    static let surface = Color.white
    static let elevated = Color(red: 0.921, green: 0.933, blue: 0.944)
    static let text = Color(red: 0.12, green: 0.15, blue: 0.19)
    static let secondary = Color(red: 0.36, green: 0.40, blue: 0.46)
    static let accent = Color(red: 0.73, green: 0.25, blue: 0.13)
    static let mint = Color(red: 0.12, green: 0.43, blue: 0.29)
    static let line = Color.black.opacity(0.09)
    static let players: [Color] = [accent, Color(red: 0.22, green: 0.40, blue: 0.67), mint, Color(red: 0.52, green: 0.39, blue: 0.09), Color(red: 0.62, green: 0.29, blue: 0.42), Color(red: 0.43, green: 0.35, blue: 0.67), Color(red: 0.12, green: 0.44, blue: 0.49), Color(red: 0.47, green: 0.37, blue: 0.29)]
}

struct CouchActionButton<Label: View>: View {
    let id: String?
    let action: () -> Void
    let label: Label
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.couchReduceMotionOverride) private var overrideMotion
    @EnvironmentObject private var store: LobbyStore
    @FocusState private var focused: Bool
    init(id: String? = nil, action: @escaping () -> Void, @ViewBuilder label: () -> Label) {
        self.id = id; self.action = action; self.label = label()
    }
    var body: some View {
        Button(action: action) { label }.focusable().focused($focused).focusEffectDisabled()
            .onKeyPress(.space) { guard enabled else { return .ignored }; action(); return .handled }
            .onKeyPress(.return) { guard enabled else { return .ignored }; action(); return .handled }
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(focused ? CouchTheme.accent : .clear, lineWidth: 2).padding(-4).allowsHitTesting(false))
            .scaleEffect(focused && !reduceMotion && !overrideMotion ? 1.01 : 1)
            .onAppear { register(); if let id { focused = store.focusID == id } }
            .onDisappear { if let id { store.unregisterFocus(id) } }
            .onChange(of: enabled) { _, _ in register() }
            .onChange(of: store.focusID) { _, value in if let id { focused = value == id } }
            .onChange(of: focused) { _, value in if value, let id { store.focusID = id } }
            .onChange(of: store.activationSerial) { _, _ in if let id, store.activationID == id, enabled { action() } }
    }
    private func register() { if let id { store.registerFocus(id) { if enabled { action() } } } }
}

struct CouchButtonStyle: ButtonStyle {
    var prominent = false
    var compact = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.couchReduceMotionOverride) private var overrideMotion
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: compact ? 14 : 16, weight: .semibold))
            .padding(.horizontal, compact ? 16 : 24).padding(.vertical, compact ? 12 : 17)
            .foregroundStyle(prominent ? Color.white : CouchTheme.text)
            .background(prominent ? CouchTheme.accent : CouchTheme.elevated, in: RoundedRectangle(cornerRadius: 13))
            .overlay(RoundedRectangle(cornerRadius: 13).strokeBorder(prominent ? Color.clear : CouchTheme.line))
            .opacity(enabled ? (configuration.isPressed ? 0.76 : 1) : 0.4)
            .scaleEffect(configuration.isPressed && !reduceMotion && !overrideMotion ? 0.985 : 1)
            .animation(reduceMotion || overrideMotion ? nil : .easeOut(duration: 0.13), value: configuration.isPressed)
    }
}

struct CouchSwitch<Label: View>: View {
    let id: String
    @Binding var value: Bool
    let label: Label
    @EnvironmentObject private var store: LobbyStore
    @Environment(\.isEnabled) private var enabled
    @FocusState private var focused: Bool
    init(id: String, value: Binding<Bool>, @ViewBuilder label: () -> Label) { self.id = id; _value = value; self.label = label() }
    var body: some View {
        Toggle(isOn: $value) { label }.toggleStyle(.switch).focusable().focused($focused).focusEffectDisabled()
            .onKeyPress(.space) { guard enabled else { return .ignored }; value.toggle(); return .handled }
            .onKeyPress(.return) { guard enabled else { return .ignored }; value.toggle(); return .handled }
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(focused ? CouchTheme.accent : .clear, lineWidth: 2).padding(-7))
            .onAppear { register() }
            .onDisappear { store.unregisterFocus(id) }
            .onChange(of: enabled) { _, _ in register() }
            .onChange(of: store.focusID) { _, next in focused = next == id }
            .onChange(of: focused) { _, isFocused in if isFocused { store.focusID = id } }
            .onChange(of: store.activationSerial) { _, _ in if store.activationID == id, enabled { value.toggle() } }
    }
    private func register() { store.registerFocus(id) { if enabled { value.toggle() } } }
}

struct KeyCap: View {
    let text: String
    var body: some View {
        Text(text).font(.system(size: 11, weight: .semibold, design: .rounded))
            .padding(.horizontal, 7).padding(.vertical, 4)
            .foregroundStyle(CouchTheme.text)
            .background(CouchTheme.elevated, in: RoundedRectangle(cornerRadius: 5))
            .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(CouchTheme.line))
    }
}

struct Eyebrow: View {
    let text: String
    var body: some View { Text(text).font(.system(size: 11, weight: .bold)).tracking(2.1).foregroundStyle(CouchTheme.secondary) }
}

struct SectionTitle: View {
    let title: String
    let subtitle: String
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.system(size: 34, weight: .bold)).tracking(-1)
            Text(subtitle).font(.system(size: 15)).foregroundStyle(CouchTheme.secondary)
        }
    }
}

extension View {
    func couchPanel(padding: CGFloat = 24) -> some View {
        self.padding(padding).background(CouchTheme.surface, in: RoundedRectangle(cornerRadius: 18))
            .overlay(RoundedRectangle(cornerRadius: 18).strokeBorder(CouchTheme.line))
    }
}
