import Foundation

enum LobbyPage: String, CaseIterable {
    case play, controllers, settings, help
    var title: String { rawValue.capitalized }
    var symbol: String {
        switch self { case .play: return "play.fill"; case .controllers: return "gamecontroller"; case .settings: return "slider.horizontal.3"; case .help: return "questionmark.circle" }
    }
}

enum PreviewScene: String, CaseIterable {
    case live, empty, ready, four, mixed, disconnected, eight, longLabels, starting, running, error
    static var requested: Self {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--scene"), args.indices.contains(i + 1) else { return .live }
        return Self(rawValue: args[i + 1]) ?? .live
    }
}

struct LobbyReadiness: Equatable {
    let joined: Int
    let ready: Int
    let reserved: Int
    let running: Bool
    var starting = false
    var canPlay: Bool { !running && !starting && ready > 0 && joined == ready }
    var title: String {
        if starting { return "Starting Smash" }
        if running { return "Your game is running" }
        if joined == 0 { return "Connect a controller" }
        if joined != ready { return "Release your controls" }
        return "Ready to play"
    }
    var detail: String {
        if starting { return "Getting your game ready. Your player slots are saved." }
        if running { return "Keep Smash Couch open. Close the game normally when you're done." }
        if joined == 0 { return "Connect a controller. Press Menu, Options, or + to join." }
        if joined != ready { return "Let go of the buttons and center the sticks to get ready." }
        return "Select Play Smash. The Menu shortcut works on the Play screen."
    }
}

extension LobbyStore {
    var readiness: LobbyReadiness {
        let slots = book.slots.prefix(capacity)
        return LobbyReadiness(joined: slots.filter { $0.connection != nil }.count,
                              ready: slots.filter { $0.connection != nil && $0.ready }.count,
                              reserved: slots.filter { $0.connection == nil && $0.reserved }.count,
                              running: running, starting: starting)
    }
    var connectedCount: Int { names.count }
    var unassignedCount: Int { max(0, connectedCount - readiness.joined) }
    func detectedFamily(at player: Int) -> ControllerFamily {
        let name = book.slots[player].connection.flatMap { names[$0] }
        return ControllerFamily.detected(from: name)
    }
    func controllerFamily(at player: Int) -> ControllerFamily {
        let saved = profiles[player].controllerFamily
        if saved != .automatic { return saved }
        return detectedFamily(at: player)
    }
    var selectedFamily: ControllerFamily { controllerFamily(at: book.selected) }
    var selectedLayout: ControllerLayout { ControllerLayout(rawValue: profiles[book.selected].layout) ?? .positions }
    func changeCapacity(_ count: Int) {
        guard !running, !starting, (1...8).contains(count) else { return }
        capacity = count; book.capacity = count; book.selected = min(book.selected, count - 1)
        for i in count..<8 { book.clear(i) }
        revision += 1
    }
    func requestPlay() {
        guard readiness.canPlay else {
            page = .controllers
            message = readiness.detail
            revision += 1
            return
        }
        launch()
    }
    func show(_ destination: LobbyPage) { page = destination }
    func applyPreviewScene(_ scene: PreviewScene) {
        previewScene = scene
        book = SlotBook(); names.removeAll(); running = false; starting = false; message = ""
        switch scene {
        case .live: return
        case .empty, .error: capacity = 1
        case .ready: capacity = 1
        case .four, .mixed, .disconnected, .running, .starting: capacity = 4
        case .eight, .longLabels: capacity = 8
        }
        book.capacity = capacity
        let count: Int
        switch scene { case .ready: count = 1; case .four, .mixed, .running, .starting: count = 4; case .disconnected: count = 3; case .eight, .longLabels: count = 8; default: count = 0 }
        for i in 0..<count {
            let id = UUID(); _ = book.join(id, at: i); book.update(id, sample: .neutral)
            names[id] = ["Xbox Wireless Controller", "DualSense Wireless Controller", "Nintendo Switch Pro Controller", "Game controller"][i % 4]
        }
        if scene == .disconnected { book.slots[3].reserved = true }
        if scene == .running { running = true }
        if scene == .starting { starting = true }
        if scene == .longLabels { for i in 0..<8 { profiles[i].label = String("Player \(i + 1)'s incredibly long controller name".prefix(40)) } }
        if scene == .error { message = "The controller connection was lost. Reconnect your controller and join your player slot again." }
        book.selected = 0; revision += 1
    }
}
