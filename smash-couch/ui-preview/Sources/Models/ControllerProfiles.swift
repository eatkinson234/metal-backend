import Foundation

enum FaceButton: Int, CaseIterable {
    // Apple GameController's normalized face positions, independent of printed letters.
    case south = 1, east = 2, west = 3, north = 4
}

enum ControllerFamily: String, Codable, CaseIterable, Identifiable {
    case automatic, xbox, playstation, switchPro, generic
    var id: String { rawValue }
    var title: String {
        switch self {
        case .automatic: return "Auto"
        case .xbox: return "Xbox"
        case .playstation: return "PlayStation"
        case .switchPro: return "Switch"
        case .generic: return "Other"
        }
    }
    var joinButton: String {
        switch self {
        case .xbox: return "Menu"
        case .playstation: return "Options"
        case .switchPro: return "+"
        case .automatic, .generic: return "Start / Menu"
        }
    }
    func label(_ button: FaceButton) -> String {
        switch self {
        case .xbox:
            return [.south: "A", .east: "B", .west: "X", .north: "Y"][button]!
        case .playstation:
            return [.south: "×", .east: "○", .west: "□", .north: "△"][button]!
        case .switchPro:
            return [.south: "B", .east: "A", .west: "Y", .north: "X"][button]!
        case .automatic, .generic:
            return [.south: "Bottom", .east: "Right", .west: "Left", .north: "Top"][button]!
        }
    }
    func spokenLabel(_ button: FaceButton) -> String {
        if self == .playstation {
            return [.south: "Cross", .east: "Circle", .west: "Square", .north: "Triangle"][button]!
        }
        return label(button)
    }
    static func detected(from name: String?) -> Self {
        guard let name = name?.lowercased() else { return .generic }
        if name.contains("xbox") { return .xbox }
        if name.contains("dualsense") || name.contains("dualshock") || name.contains("playstation") { return .playstation }
        if name.contains("nintendo") || name.contains("switch") || name.contains("joy-con") { return .switchPro }
        return .generic
    }
}

enum SmashAction: String, CaseIterable, Identifiable {
    case jump, attack, special
    var id: String { rawValue }
    var title: String {
        switch self { case .jump: return "Jump"; case .attack: return "Quick attack"; case .special: return "Special" }
    }
}

enum ControllerLayout: String, CaseIterable, Identifiable {
    case positions = "positions"
    case xboxAJump = "xbox_a_jump"
    case brawlhalla = "brawlhalla_style"
    case positionsXAttack = "positions_x_attack"
    case letters = "letters"
    var id: String { rawValue }
    func title(for family: ControllerFamily) -> String {
        switch self {
        case .positions: return "Smash standard"
        case .xboxAJump: return "\(family.label(.south)) to jump"
        case .brawlhalla: return "Brawlhalla-style"
        case .positionsXAttack: return "Extra attack button"
        case .letters: return "Swap attack / special"
        }
    }
    var title: String { title(for: .xbox) }
    func output(for button: FaceButton) -> FaceButton {
        switch self {
        case .positions, .letters: return button
        case .positionsXAttack: return button == .west ? .east : button
        case .xboxAJump:
            switch button { case .south: return .north; case .east: return .south; case .west: return .east; case .north: return .west }
        case .brawlhalla:
            switch button { case .south: return .north; case .east, .north: return .south; case .west: return .east }
        }
    }
    func buttons(for action: SmashAction) -> [FaceButton] {
        FaceButton.allCases.filter { source in
            let output = output(for: source)
            switch action {
            case .jump: return output == .north || output == .west
            case .attack: return output == (self == .letters ? .south : .east)
            case .special: return output == (self == .letters ? .east : .south)
            }
        }
    }
    func labels(for action: SmashAction, family: ControllerFamily) -> String {
        buttons(for: action).map { family.label($0) }.joined(separator: " / ")
    }
    func detail(for family: ControllerFamily) -> String {
        "\(labels(for: .jump, family: family)) jump · \(labels(for: .attack, family: family)) quick attack · \(labels(for: .special, family: family)) special"
    }
    var detail: String { detail(for: .xbox) }
}
