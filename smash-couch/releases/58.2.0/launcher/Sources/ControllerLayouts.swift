import Foundation

// Per-player face-button layouts, ported from Astra's controller presets. Every layout keeps all four Switch face
// buttons, so players can remap actions in Smash's own control settings; "Extra attack" (B and X both attack) and
// "Brawlhalla-style" (two specials) each left one Switch button unreachable and were removed on 2026-10-11 at the
// user's request. Saved choices of a removed layout load as the default (SlotBook.layout).
// Physical face positions are GameController bits: south=1 (Xbox A), east=2 (B), west=3 (X), north=4 (Y).
// The native receiver's Config is positional, so output bit 1 is Switch B (Special), bit 2 is
// Switch A (Attack/confirm) and bits 3/4 are Switch Y/X (Jump). Sticks, shoulders, triggers,
// D-pad, View and Menu always pass through unchanged.

enum SmashAction: Equatable {
    case attack, special, jump

    var title: String {
        switch self {
        case .attack: return "Attack"
        case .special: return "Special"
        case .jump: return "Jump"
        }
    }
}

enum ControllerLayout: String, Codable, CaseIterable, Identifiable {
    case standard = "positions"
    case southJump = "xbox_a_jump"
    case swapped = "letters"

    static let `default` = ControllerLayout.standard
    static let faceBits = [1, 2, 3, 4]  // south, east, west, north

    var id: String { rawValue }

    /// Output bit for each physical face bit, ordered south, east, west, north.
    var outputs: [Int] {
        switch self {
        case .standard: return [1, 2, 3, 4]
        case .southJump: return [3, 1, 2, 4]
        case .swapped: return [2, 1, 3, 4]
        }
    }

    /// The Smash action a physical face position performs (index: south, east, west, north).
    func action(face index: Int) -> SmashAction {
        switch outputs[index] {
        case 1: return .special
        case 2: return .attack
        default: return .jump
        }
    }

    /// Physical face positions (south, east, west, north indices) that perform an action.
    func faces(for action: SmashAction) -> [Int] {
        (0..<4).filter { self.action(face: $0) == action }
    }

    func title(southLabel: String) -> String {
        switch self {
        case .standard: return "Smash standard"
        case .southJump: return "\(southLabel) to jump"
        case .swapped: return "Swap attack / special"
        }
    }

    var summary: String {
        switch self {
        case .standard: return "Smash Ultimate's default face buttons"
        case .southJump: return "Jump on the bottom button, platformer style"
        case .swapped: return "Attack and special trade places"
        }
    }

    /// Converts Nintendo label order (bit 1 = A on the right, 2 = B bottom, 3 = X top, 4 = Y left)
    /// to positional order (1 = bottom, 2 = right, 3 = left, 4 = top). Other bits pass through.
    static func positionalFaces(fromLabelOrdered bits: UInt32) -> UInt32 {
        let a = bits & (1 << 1), b = bits & (1 << 2), x = bits & (1 << 3), y = bits & (1 << 4)
        return bits & ~UInt32(0b11110) | (a << 1) | (b >> 1) | (x << 1) | (y >> 1)
    }

    func next(_ step: Int) -> ControllerLayout {
        let all = Self.allCases
        let index = all.firstIndex(of: self) ?? 0
        return all[(index + step + all.count) % all.count]
    }
}

extension PadSample {
    /// Remaps only face bits 1-4; every other control passes through.
    func mapped(for layout: ControllerLayout) -> PadSample {
        var result = self
        result.buttons &= ~UInt32(0b11110)
        for (index, source) in ControllerLayout.faceBits.enumerated() where down(source) {
            result.buttons |= 1 << UInt32(layout.outputs[index])
        }
        return result
    }
}
