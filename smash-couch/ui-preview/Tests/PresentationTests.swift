import Foundation

@main struct PresentationTests {
    @MainActor static func main() throws {
        var assertions = 0
        func check(_ condition: @autoclosure () -> Bool, _ description: String) {
            precondition(condition(), description); assertions += 1
            print("PASS \(description)")
        }
        let model = LobbyStore()
        let installed = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Smash Couch")
        check(model.isPreview && model.root != installed, "Preview uses a separate root")
        check(model.actions.intents.isEmpty, "Fixture preview starts with no recorded runtime intent")
        check(!FileManager.default.fileExists(atPath: model.root.appendingPathComponent("launcher.lock").path), "Presentation preview acquires no launcher lock")
        model.applyPreviewScene(.empty)
        check(model.readiness.joined == 0 && !model.readiness.canPlay, "Empty lobby cannot play")
        model.requestPlay()
        check(model.page == .controllers && model.message.contains("Menu") && model.message.contains("Options") && model.message.contains("+"), "Play guides an empty lobby to controller setup")
        model.applyPreviewScene(.ready)
        check(model.readiness.joined == 1 && model.readiness.ready == 1 && model.readiness.canPlay, "Joined neutral controllers make the lobby ready")
        model.requestPlay()
        check(model.actions.intents.last?.action == "play" && !model.running, "Ready preview Play cannot start a game")
        check(model.message.contains("Game launch is disabled"), "Preview explains its blocked launch")
        check(!FileManager.default.fileExists(atPath: model.dataRoot.appendingPathComponent("Config.json").path), "Preview Play creates no game configuration")
        check(!FileManager.default.fileExists(atPath: model.root.appendingPathComponent("Logs").path), "Preview Play creates no game log")
        model.applyPreviewScene(.disconnected)
        check(model.readiness.reserved == 1 && model.book.slots[0].connection != nil && model.book.slots[3].connection == nil, "Disconnected slot stays reserved without shifting a player")
        let reconnect = UUID(); check(model.book.join(reconnect, at: 3), "Reserved slot permits explicit reconnect")
        check(!model.book.slots[3].ready, "Reconnected controller must release controls")
        model.book.update(reconnect, sample: .neutral)
        check(model.book.slots[3].ready, "Neutral release makes reconnect ready")
        model.applyPreviewScene(.eight)
        check(model.capacity == 8 && model.readiness.joined == 8 && model.readiness.ready == 8, "Eight player cards have eight distinct joined slots")
        let savedProfile = model.profiles[7].label
        model.changeCapacity(2)
        check(model.book.slots.dropFirst(2).allSatisfy { !$0.reserved && $0.connection == nil }, "Reducing capacity frees removed slots")
        check(model.profiles[7].label == savedProfile, "Reducing capacity preserves stored player labels")
        model.applyPreviewScene(.running)
        model.changeCapacity(1)
        check(model.capacity == 4 && !model.readiness.canPlay, "Running state blocks player-count edits and relaunch")
        model.applyPreviewScene(.error)
        check(model.message.contains("connection was lost"), "Error scene gives an actionable reconnect message")
        check(ControllerLayout(rawValue: "positions_x_attack")?.labels(for: .attack, family: .xbox) == "B / X", "Help describes the preserved B and X attack layout")
        model.applyPreviewScene(.starting)
        check(!model.readiness.canPlay && model.readiness.title == "Starting Smash", "Starting state disables another launch")
        model.applyPreviewScene(.ready); model.page = .play
        let priorIntents = model.actions.intents.count
        model.setModalOrder(["layout.positions"]); model.navigate(.menu)
        check(model.actions.intents.count == priorIntents, "Menu cannot launch while a modal is open")
        model.setModalOrder([]); model.page = .settings; model.navigate(.menu)
        check(model.actions.intents.count == priorIntents, "Menu cannot launch while changing settings")
        model.page = .play; model.navigate(.menu)
        check(model.actions.intents.count == priorIntents + 1, "Menu on a ready Home records one play intent")
        model.applyPreviewScene(.four); model.page = .controllers; model.select(3); model.navigate(.right)
        check(model.book.selected == 0, "Controller right navigation wraps player slots")
        model.navigate(.left); check(model.book.selected == 3, "Controller left navigation wraps player slots")
        model.navigate(.back); check(model.page == .play && model.focusID == "play", "Controller back restores Home play focus")
        model.show(.controllers)
        check(model.book.selected == 3 && model.focusID == "player.3", "Returning to Controllers focuses the previously selected player")
        model.applyPreviewScene(.longLabels)
        check(model.profiles.allSatisfy { $0.label.count <= 40 }, "Long labels respect the existing profile limit")
        print("\(assertions) presentation and isolation assertions passed")
    }
}
