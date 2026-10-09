import Foundation

@main struct ControllerPresetTests {
    static func main() throws {
        var count = 0
        func check(_ value: @autoclosure () -> Bool, _ name: String) {
            precondition(value(), name); count += 1; print("PASS \(name)")
        }
        // Expected wire bits come from the existing positional configuration:
        // 1 -> Switch B / Special, 2 -> Switch A / Attack, 3 & 4 -> Jump.
        let axes: [Float] = [0.2, -0.4, 0.7, 0, 0.3, 0.8]
        let untouched: UInt32 = (1 << 6) | (1 << 8) | (1 << 16)
        for (input, output) in [(1,4), (2,1), (3,2), (4,3)] {
            let sample = PadSample(buttons: (1 << input) | untouched, axes: axes)
            let mapped = sample.mapped(for: "xbox_a_jump")
            check(mapped.buttons == (1 << output) | untouched, "Xbox A-jump physical face \(input) reaches expected action")
            check(mapped.axes == axes, "Xbox A-jump keeps stick and trigger axes \(input)")
        }
        for (input, output) in [(1,4), (2,1), (3,2), (4,1)] {
            let mapped = PadSample(buttons: (1 << input) | untouched, axes: axes).mapped(for: "brawlhalla_style")
            check(mapped.buttons == (1 << output) | untouched, "Brawlhalla physical face \(input) reaches expected action")
        }
        let bothSpecial = PadSample(buttons: (1 << 2) | (1 << 4)).mapped(for: "brawlhalla_style")
        check(bothSpecial.buttons == 1 << 1, "B and Y together send one Special action")
        check(PadSample(buttons: 1 << 2).mapped(for: "brawlhalla_style").buttons == bothSpecial.buttons, "Releasing Y does not release held B Special")
        check(PadSample(buttons: 1 << 4).mapped(for: "brawlhalla_style").buttons == bothSpecial.buttons, "Releasing B does not release held Y Special")
        check(PadSample.neutral.mapped(for: "brawlhalla_style").isNeutral, "Releasing both special buttons becomes neutral")
        check(PadSample(buttons: (1 << 1) | (1 << 3)).mapped(for: "brawlhalla_style").buttons == (1 << 4) | (1 << 2), "Jump and quick attack work together")

        let legacy = Data(#"{"label":"Blue controller","layout":"positions_x_attack"}"#.utf8)
        let profile = try JSONDecoder().decode(PlayerProfile.self, from: legacy)
        check(profile.layout == "positions_x_attack" && profile.controllerFamily == .automatic, "Old profiles preserve mapping and gain Auto type")
        let switchProfile = PlayerProfile(label: "Switch Pro", layout: "brawlhalla_style", controllerFamily: .switchPro)
        let roundTrip = try JSONDecoder().decode(PlayerProfile.self, from: JSONEncoder().encode(switchProfile))
        check(roundTrip.controllerFamily == .switchPro && roundTrip.layout == "brawlhalla_style", "Controller type and preset survive save/load")
        check(ControllerLayout.xboxAJump.labels(for: .jump, family: .xbox) == "A / Y", "Xbox A-jump shows both jump buttons")
        check(ControllerLayout.xboxAJump.labels(for: .attack, family: .xbox) == "X", "Xbox quick attack shows X")
        check(ControllerLayout.brawlhalla.labels(for: .special, family: .xbox) == "B / Y", "Brawlhalla-style shows both Special buttons")
        check(ControllerLayout.positions.labels(for: .attack, family: .switchPro) == "A", "Switch standard attack shows A")
        check(ControllerLayout.positions.labels(for: .special, family: .switchPro) == "B", "Switch standard Special shows B")
        check(ControllerLayout.positions.labels(for: .jump, family: .switchPro) == "Y / X", "Switch standard jump shows Y and X")
        check(ControllerLayout.brawlhalla.labels(for: .jump, family: .switchPro) == "B", "Switch Brawlhalla-style Jump is B")
        check(ControllerLayout.brawlhalla.labels(for: .attack, family: .switchPro) == "Y", "Switch Brawlhalla-style Quick attack is Y")
        check(ControllerLayout.brawlhalla.labels(for: .special, family: .switchPro) == "A / X", "Switch Brawlhalla-style Special is A or X")
        check(ControllerLayout.brawlhalla.labels(for: .jump, family: .playstation) == "×", "PlayStation jump uses Cross")
        check(ControllerFamily.switchPro.joinButton == "+", "Switch joining uses Plus")

        let base: [String: Any] = ["version":70,"game_dirs":["/fixture"],"input_config":[],"autoload_dirs":[],"vsync_mode":0,"enable_custom_vsync_interval":false,"graphics_backend":"Vulkan","docked_mode":true,"use_hypervisor":true,"audio_volume":0.5,"keep_this":"unchanged"]
        for layout in ["xbox_a_jump", "brawlhalla_style"] {
            let profiles = (1...8).map { PlayerProfile(label: "P\($0)", layout: layout) }
            let output = try CouchConfiguration.prepare(data: JSONSerialization.data(withJSONObject: base), profiles: profiles, capacity: 1, muted: false, fullScreen: true)
            let config = try JSONSerialization.jsonObject(with: output) as! [String: Any]
            let input = (config["input_config"] as! [[String: Any]])[0]
            let face = input["right_joycon"] as! [String: String]
            check(face["button_a"] == "B" && face["button_b"] == "A", "\(layout) keeps the canonical emulator action mapping")
            check(config["keep_this"] as? String == "unchanged" && config["graphics_backend"] as? String == "Vulkan", "\(layout) preserves unrelated configuration")
        }
        var slots = SlotBook(); let id = UUID(); _ = slots.join(id, at: 0)
        slots.update(id, sample: .neutral)
        slots.update(id, sample: PadSample(buttons: (1 << 1) | (1 << 3)).mapped(for: "brawlhalla_style"))
        let packet = slots.packet(token: Array(repeating: 0, count: 16), sequence: 1, captureTime: 1, enabled: true)
        let buttonWord = (0..<4).reduce(UInt32(0)) { $0 | UInt32(packet[44 + $1]) << UInt32(8 * $1) }
        check(packet.count == 296 && buttonWord == (1 << 4) | (1 << 2), "Mapped jump plus attack reaches the unchanged SCINPUT2 packet")
        print("\(count) controller-preset checks passed")
    }
}
