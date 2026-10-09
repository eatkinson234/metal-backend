import Foundation
import Combine

struct LobbyIntent: Codable {
    let action: String
    let player: Int?
    let value: String?
}

@MainActor protocol LobbyActionHandling: AnyObject {
    func record(_ intent: LobbyIntent)
}

@MainActor final class PreviewActions: LobbyActionHandling {
    private(set) var intents: [LobbyIntent] = []
    func record(_ intent: LobbyIntent) { intents.append(intent) }
}

enum LobbyNavigation { case next, previous, left, right, confirm, back, menu }

// Presentation state only. This type has no emulator, socket, controller discovery,
// polling timer, launcher lock, or production data path. The legacy runtime is
// preserved separately and deliberately excluded from the preview build.
@MainActor final class LobbyStore: ObservableObject {
    @Published var revision = 0
    @Published var message = ""
    @Published var page: LobbyPage = .play {
        didSet { if page != oldValue { focusID = page == .play ? "play" : (page == .controllers ? "player.\(book.selected)" : "nav.\(page.rawValue)") } }
    }
    @Published var focusID = "play"
    @Published var modalVisible = false
    @Published var activationSerial = 0
    @Published var reduceMotionOverride = false
    var activationID = ""
    @Published var running = false
    @Published var starting = false
    @Published var muted = false
    @Published var fullscreen = true
    @Published var capacity = 1
    @Published var profiles = (1...8).map { PlayerProfile(label: "Controller \($0)", layout: $0 == 1 ? "xbox_a_jump" : "positions") }
    @Published var hud = false
    @Published var capture = false
    @Published var previewScene = PreviewScene.requested
    var book = SlotBook()
    var names: [UUID: String] = [:]
    let isPreview = true
    let actions = PreviewActions()
    private var focusActions: [String: () -> Void] = [:]
    private var modalOrder: [String] = []
    let root: URL
    var dataRoot: URL { root.appendingPathComponent("Data") }

    init() {
        let base = Bundle.main.object(forInfoDictionaryKey: "SmashCouchPreviewRoot") as? String ?? NSTemporaryDirectory()
        root = URL(fileURLWithPath: base).appendingPathComponent("presentation-preview-\(ProcessInfo.processInfo.processIdentifier)")
        applyPreviewScene(previewScene == .live ? .empty : previewScene)
    }
    func select(_ i: Int) { guard (0..<capacity).contains(i) else { return }; book.selected = i; revision += 1 }
    func clear(_ i: Int) {
        guard !running && !starting else { return }
        book.clear(i); actions.record(LobbyIntent(action: "clear", player: i, value: nil)); revision += 1
    }
    func pulse(_ i: Int) {
        actions.record(LobbyIntent(action: "identify", player: i, value: nil))
        message = "Preview: identify Player \(i + 1). No controller vibration is sent."
    }
    func launch() {
        actions.record(LobbyIntent(action: "play", player: nil, value: nil))
        message = "Design preview: Play Smash selected. Game launch is disabled in this preview."
    }
    func saveProfiles() throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try JSONEncoder().encode(profiles).write(to: root.appendingPathComponent("controller-profiles.json"), options: .atomic)
        try JSONEncoder().encode(actions.intents).write(to: root.appendingPathComponent("recorded-intents.json"), options: .atomic)
    }
    func registerFocus(_ id: String, action: @escaping () -> Void) { focusActions[id] = action }
    func unregisterFocus(_ id: String) { focusActions.removeValue(forKey: id) }
    func setModalOrder(_ ids: [String]) { modalOrder = ids; modalVisible = !ids.isEmpty; focusID = ids.first ?? "player.\(book.selected)" }
    var focusOrder: [String] {
        if modalVisible { return modalOrder }
        let nav = ["nav.play", "nav.controllers", "nav.settings", "nav.help"]
        switch page {
        case .play: return nav + ["play", "manage"] + (0..<capacity).map { "player.\($0)" }
        case .controllers: return nav + ["controllers.play"] + (0..<capacity).map { "player.\($0)" }
        case .settings: return nav + (1...8).map { "capacity.\($0)" } + ["fullscreen", "sound", "settings.controllers"]
        case .help: return nav + ["help.layout"]
        }
    }
    func navigate(_ input: LobbyNavigation) {
        switch input {
        case .menu:
            guard !modalVisible, page == .play, readiness.canPlay else { return }
            requestPlay()
        case .back:
            if modalVisible { focusActions["modal.cancel"]?() }
            else { show(.play); focusID = "play" }
        case .confirm:
            if focusID == "modal.cancel" || focusID == "modal.done" { focusActions[focusID]?() }
            else { activationID = focusID; activationSerial += 1 }
        case .left, .right:
            guard !modalVisible else { return }
            if page == .controllers {
                select((book.selected + (input == .left ? capacity - 1 : 1)) % capacity); focusID = "player.\(book.selected)"
            } else { moveFocus(input == .left ? -1 : 1) }
        case .next: moveFocus(1)
        case .previous: moveFocus(-1)
        }
    }
    private func moveFocus(_ step: Int) {
        let order = focusOrder; guard !order.isEmpty else { return }
        let current = order.firstIndex(of: focusID) ?? 0
        focusID = order[(current + step + order.count) % order.count]
    }
}
