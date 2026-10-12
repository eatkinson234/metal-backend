import SwiftUI

// Console-style light lobby. Pure presentation: it renders a LobbyScene snapshot and
// calls LobbyActions. All controller ownership, launch and cleanup logic stays in
// NativeLobbyModel; controllers drive the lobby (D-pad chooses, Menu joins/starts).

enum ControllerFamily: Equatable {
    case xbox, playstation, nintendo, generic

    static func from(name: String) -> ControllerFamily {
        let n = name.lowercased()
        if n.contains("xbox") { return .xbox }
        if n.contains("dualsense") || n.contains("dualshock") || n.contains("playstation") || n.contains("ps4") || n.contains("ps5") { return .playstation }
        if n.contains("pro controller") || n.contains("joy-con") || n.contains("nintendo") || n.contains("switch") { return .nintendo }
        return .generic
    }

    var viewName: String {
        switch self {
        case .playstation: return "CREATE"
        case .nintendo: return "−"
        default: return "VIEW"
        }
    }

    var menuName: String {
        switch self {
        case .xbox: return "Menu"
        case .playstation: return "Options"
        case .nintendo: return "+"
        case .generic: return "Menu"
        }
    }

    /// Label of the bottom face button, used in layout names such as "A to jump".
    var southLabel: String {
        switch self {
        case .playstation: return "✕"
        case .nintendo: return "B"
        default: return "A"
        }
    }

    var displayName: String {
        switch self {
        case .xbox: return "Xbox"
        case .playstation: return "PlayStation"
        case .nintendo: return "Nintendo"
        case .generic: return "Controller"
        }
    }
}

enum SlotPhase: Equatable {
    case open
    case selecting([String])
    case releasing
    case ready
    case reserved
}

struct PlayerSlotScene: Identifiable {
    let id: Int
    let label: String
    let phase: SlotPhase
    let controllerName: String?
    let family: ControllerFamily?
    let activity: Bool
    var layout: ControllerLayout = .default
    /// Physical face positions held right now (south, east, west, north), lobby only.
    var pressedFaces: [Int] = []
}

struct WaitingControllerScene: Identifiable {
    let id: UUID
    let name: String
    let family: ControllerFamily
    let selectedSlot: Int
    let active: Bool
}

struct LobbyScene {
    var capacity: Int
    var slots: [PlayerSlotScene]
    var waiting: [WaitingControllerScene]
    var connectedCount: Int
    var message: String
    var running: Bool
    var canLaunch: Bool

    var guideFamily: ControllerFamily {
        slots.first(where: { $0.family != nil })?.family ?? waiting.first?.family ?? .xbox
    }

    /// The player whose layout the Controls card explains: the first joined player, else P1.
    var guideSlot: PlayerSlotScene? {
        slots.prefix(capacity).first(where: { $0.controllerName != nil }) ?? slots.first
    }
}

struct LobbyActions {
    var launch: () -> Void = {}
    var setCapacity: (Int) -> Void = { _ in }
    var identifySlot: (Int) -> Void = { _ in }
    var identifyController: (UUID) -> Void = { _ in }
    var setLabel: (String, Int) -> Void = { _, _ in }
    var removePlayer: (Int) -> Void = { _ in }
    var setLayout: (Int, ControllerLayout) -> Void = { _, _ in }
}

// MARK: - Design tokens

enum Couch {
    static let ink = Color(red: 0.10, green: 0.11, blue: 0.14)
    static let inkSoft = Color(red: 0.36, green: 0.38, blue: 0.44)
    static let inkFaint = Color(red: 0.58, green: 0.60, blue: 0.66)
    static let canvasTop = Color(red: 0.975, green: 0.978, blue: 0.988)
    static let canvasBottom = Color(red: 0.925, green: 0.934, blue: 0.955)
    static let card = Color.white
    static let hairline = Color.black.opacity(0.06)
    static let flameA = Color(red: 1.00, green: 0.30, blue: 0.24)
    static let flameB = Color(red: 1.00, green: 0.55, blue: 0.20)
    static let flameC = Color(red: 0.90, green: 0.16, blue: 0.42)
    static let ready = Color(red: 0.16, green: 0.69, blue: 0.40)
    static let caution = Color(red: 0.96, green: 0.62, blue: 0.10)

    // Classic fighter-slot colors: red, blue, yellow, green, orange, cyan, pink, purple.
    static let players: [Color] = [
        Color(red: 0.93, green: 0.22, blue: 0.24), Color(red: 0.16, green: 0.45, blue: 0.93),
        Color(red: 0.96, green: 0.72, blue: 0.08), Color(red: 0.18, green: 0.70, blue: 0.36),
        Color(red: 0.98, green: 0.52, blue: 0.14), Color(red: 0.10, green: 0.72, blue: 0.80),
        Color(red: 0.94, green: 0.38, blue: 0.66), Color(red: 0.55, green: 0.36, blue: 0.90)]

    static func player(_ slot: Int) -> Color { players[slot % players.count] }

    static func font(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight, design: .rounded)
    }
}

// MARK: - Button glyphs

enum FaceButton {
    case bottom, right, left, top

    /// Physical face order used by ControllerLayout: south, east, west, north.
    static let layoutOrder: [FaceButton] = [.bottom, .right, .left, .top]
}

struct FaceGlyph: View {
    let family: ControllerFamily
    let button: FaceButton
    var size: CGFloat = 26

    var body: some View {
        ZStack {
            Circle().fill(fill)
            Circle().strokeBorder(Color.black.opacity(0.08), lineWidth: 1)
            symbol
        }
        .frame(width: size, height: size)
        .shadow(color: .black.opacity(0.10), radius: 1.5, y: 1)
    }

    private var fill: Color {
        switch family {
        case .playstation: return Color(red: 0.16, green: 0.17, blue: 0.20)
        case .nintendo: return Color(red: 0.27, green: 0.28, blue: 0.31)
        default: return Color(red: 0.16, green: 0.17, blue: 0.20)
        }
    }

    @ViewBuilder private var symbol: some View {
        switch family {
        case .playstation:
            psShape
        case .nintendo:
            Text(nintendoLetter).font(Couch.font(size * 0.52, .bold)).foregroundStyle(.white)
        default:
            Text(xboxLetter.0).font(Couch.font(size * 0.52, .heavy)).foregroundStyle(xboxLetter.1)
        }
    }

    private var xboxLetter: (String, Color) {
        switch button {
        case .bottom: return ("A", Color(red: 0.45, green: 0.80, blue: 0.30))
        case .right: return ("B", Color(red: 0.95, green: 0.33, blue: 0.29))
        case .left: return ("X", Color(red: 0.33, green: 0.58, blue: 0.98))
        case .top: return ("Y", Color(red: 0.98, green: 0.80, blue: 0.22))
        }
    }

    private var nintendoLetter: String {
        switch button {
        case .bottom: return "B"
        case .right: return "A"
        case .left: return "Y"
        case .top: return "X"
        }
    }

    @ViewBuilder private var psShape: some View {
        let s = size * 0.42
        switch button {
        case .bottom:
            Image(systemName: "xmark").font(.system(size: s * 0.95, weight: .bold)).foregroundStyle(Color(red: 0.55, green: 0.70, blue: 1.0))
        case .right:
            Circle().stroke(Color(red: 1.0, green: 0.45, blue: 0.48), lineWidth: 2.2).frame(width: s, height: s)
        case .left:
            RoundedRectangle(cornerRadius: 1.5).stroke(Color(red: 0.98, green: 0.55, blue: 0.85), lineWidth: 2.2).frame(width: s * 0.92, height: s * 0.92)
        case .top:
            Image(systemName: "triangle").font(.system(size: s * 0.95, weight: .bold)).foregroundStyle(Color(red: 0.40, green: 0.88, blue: 0.70))
        }
    }
}

struct KeyCap: View {
    let text: String
    var dark = true

    var body: some View {
        Text(text)
            .font(Couch.font(12, .bold))
            .foregroundStyle(dark ? Color.white : Couch.ink)
            .padding(.horizontal, 8).frame(minWidth: 26, minHeight: 22)
            .background(Capsule().fill(dark ? Couch.ink : Color.white))
            .overlay(Capsule().strokeBorder(Couch.hairline))
    }
}

struct MenuGlyph: View {
    let family: ControllerFamily
    var body: some View {
        switch family {
        case .nintendo:
            ZStack {
                Circle().fill(Couch.ink).frame(width: 22, height: 22)
                Image(systemName: "plus").font(.system(size: 11, weight: .heavy)).foregroundStyle(.white)
            }
        case .playstation:
            KeyCap(text: "OPTIONS")
        default:
            ZStack {
                Circle().fill(Couch.ink).frame(width: 22, height: 22)
                Image(systemName: "line.3.horizontal").font(.system(size: 10, weight: .heavy)).foregroundStyle(.white)
            }
        }
    }
}

struct ShoulderGlyphs: View {
    let family: ControllerFamily
    var body: some View {
        let names: (String, String) = family == .playstation ? ("L1", "R1") : family == .nintendo ? ("L", "R") : ("LB", "RB")
        HStack(spacing: 4) { KeyCap(text: names.0); KeyCap(text: names.1) }
    }
}

struct TriggerGlyphs: View {
    let family: ControllerFamily
    var body: some View {
        let names: (String, String) = family == .playstation ? ("L2", "R2") : family == .nintendo ? ("ZL", "ZR") : ("LT", "RT")
        HStack(spacing: 4) { KeyCap(text: names.0); KeyCap(text: names.1) }
    }
}

// MARK: - Lobby

struct CouchLobbyView: View {
    let scene: LobbyScene
    let actions: LobbyActions
    @State private var pulse = false

    var body: some View {
        ZStack {
            CouchBackdrop()
            VStack(alignment: .leading, spacing: 18) {
                TopBar(scene: scene, actions: actions)
                HeroCard(scene: scene, actions: actions, pulse: pulse && !scene.running)
                HStack(alignment: .firstTextBaseline) {
                    Text("Players").font(Couch.font(24, .bold)).foregroundStyle(Couch.ink)
                    Text(playersSubtitle).font(Couch.font(14, .medium)).foregroundStyle(Couch.inkSoft)
                    Spacer()
                }
                PlayerGrid(scene: scene, actions: actions, pulse: pulse && !scene.running)
                Spacer(minLength: 0)
                HStack(alignment: .bottom, spacing: 18) {
                    StatusToast(message: scene.message, running: scene.running)
                    Spacer(minLength: 12)
                    ControlsCard(family: scene.guideFamily, slot: scene.guideSlot)
                }
                HintBar(family: scene.guideFamily, running: scene.running)
            }
            .padding(.horizontal, 36).padding(.top, 22).padding(.bottom, 16)
        }
        .frame(minWidth: 1120, minHeight: 760)
        .onAppear {
            withAnimation(.easeInOut(duration: 1.1).repeatForever(autoreverses: true)) { pulse = true }
        }
    }

    private var playersSubtitle: String {
        let joined = scene.slots.prefix(scene.capacity).filter { $0.phase == .ready || $0.phase == .releasing }.count
        return joined == 0 ? "Pick a slot with the D-pad, then press Menu" : "\(joined) of \(scene.capacity) joined"
    }
}

struct CouchBackdrop: View {
    var body: some View {
        ZStack {
            LinearGradient(colors: [Couch.canvasTop, Couch.canvasBottom], startPoint: .top, endPoint: .bottom)
            Circle().fill(Couch.flameB.opacity(0.10)).frame(width: 620).blur(radius: 90).offset(x: 420, y: -320)
            Circle().fill(Couch.players[1].opacity(0.07)).frame(width: 560).blur(radius: 100).offset(x: -480, y: 360)
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
    }
}

struct TopBar: View {
    let scene: LobbyScene
    let actions: LobbyActions

    var body: some View {
        HStack(spacing: 14) {
            LogoMark(cornerFraction: 0.26)
                .frame(width: 46, height: 46)
                .shadow(color: Couch.flameA.opacity(0.35), radius: 8, y: 4)
            VStack(alignment: .leading, spacing: 1) {
                Text("Smash Couch").font(Couch.font(24, .heavy)).foregroundStyle(Couch.ink)
                Text("Native Metal · smooth 60").font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkFaint)
            }
            Spacer()
            Chip(icon: "gamecontroller", text: scene.connectedCount == 1 ? "1 controller" : "\(scene.connectedCount) controllers",
                 tint: scene.connectedCount > 0 ? Couch.ready : Couch.inkFaint)
            PlayerCountStepper(capacity: scene.capacity, disabled: scene.running, set: actions.setCapacity)
        }
    }
}

struct Chip: View {
    let icon: String
    let text: String
    let tint: Color

    var body: some View {
        HStack(spacing: 7) {
            Circle().fill(tint).frame(width: 8, height: 8)
            Image(systemName: icon).font(.system(size: 13, weight: .semibold)).foregroundStyle(Couch.inkSoft)
            Text(text).font(Couch.font(13, .semibold)).foregroundStyle(Couch.ink)
        }
        .padding(.horizontal, 14).frame(height: 36)
        .background(Capsule().fill(Color.white))
        .overlay(Capsule().strokeBorder(Couch.hairline))
        .shadow(color: .black.opacity(0.04), radius: 6, y: 2)
    }
}

struct PlayerCountStepper: View {
    let capacity: Int
    let disabled: Bool
    let set: (Int) -> Void

    var body: some View {
        HStack(spacing: 0) {
            stepButton("minus", enabled: capacity > 1) { set(capacity - 1) }
            Text("\(capacity) \(capacity == 1 ? "Player" : "Players")")
                .font(Couch.font(13, .bold)).foregroundStyle(Couch.ink).frame(minWidth: 82)
            stepButton("plus", enabled: capacity < 8) { set(capacity + 1) }
        }
        .frame(height: 36)
        .background(Capsule().fill(Color.white))
        .overlay(Capsule().strokeBorder(Couch.hairline))
        .shadow(color: .black.opacity(0.04), radius: 6, y: 2)
        .opacity(disabled ? 0.5 : 1)
        .disabled(disabled)
    }

    private func stepButton(_ icon: String, enabled: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: icon).font(.system(size: 13, weight: .heavy))
                .frame(width: 40, height: 36).contentShape(Rectangle())
        }
        .buttonStyle(.plain).foregroundStyle(enabled ? Couch.ink : Couch.inkFaint).disabled(!enabled)
        .help(icon == "minus" ? "One fewer player (frees the last slot)" : "One more player")
    }
}

struct HeroCard: View {
    let scene: LobbyScene
    let actions: LobbyActions
    let pulse: Bool
    private var tall: Bool { scene.capacity <= 4 }

    var body: some View {
        ZStack(alignment: .leading) {
            RoundedRectangle(cornerRadius: 30, style: .continuous)
                .fill(LinearGradient(colors: [Couch.flameA, Couch.flameB], startPoint: .leading, endPoint: .trailing))
            // Decoration only. Its shapes extend past the card, and clipShape does not clip hit
            // testing, so without this the art swallowed clicks on the top bar's minus button.
            HeroArt().clipShape(RoundedRectangle(cornerRadius: 30, style: .continuous)).allowsHitTesting(false)
            HStack(alignment: .center, spacing: 24) {
                VStack(alignment: .leading, spacing: 10) {
                    Text(scene.running ? "NOW PLAYING" : "READY TO PLAY")
                        .font(Couch.font(12, .heavy)).kerning(2).foregroundStyle(.white.opacity(0.85))
                    Text(tall ? "Super Smash Bros.\nUltimate" : "Super Smash Bros. Ultimate")
                        .font(Couch.font(tall ? 36 : 30, .heavy)).foregroundStyle(.white).lineSpacing(-4)
                        .shadow(color: .black.opacity(0.18), radius: 8, y: 3)
                    HStack(spacing: 8) {
                        HeroTag(text: "60 FPS PACED")
                        HeroTag(text: "NATIVE METAL")
                        HeroTag(text: "\(scene.capacity)P")
                    }
                }
                Spacer()
                PlayButton(scene: scene, action: actions.launch, pulse: pulse)
            }
            .padding(.horizontal, 40)
        }
        .frame(height: tall ? 188 : 132)
        .shadow(color: Couch.flameA.opacity(0.28), radius: 24, y: 12)
    }
}

struct HeroTag: View {
    let text: String
    var body: some View {
        Text(text).font(Couch.font(11, .heavy)).kerning(1).foregroundStyle(.white)
            .padding(.horizontal, 10).padding(.vertical, 5)
            .background(Capsule().fill(Color.white.opacity(0.22)))
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.35)))
    }
}

struct HeroArt: View {
    var body: some View {
        GeometryReader { g in
            ZStack {
                Circle().fill(Color.white.opacity(0.10)).frame(width: g.size.height * 1.9)
                    .offset(x: g.size.width * 0.30, y: -g.size.height * 0.15)
                Circle().stroke(Color.white.opacity(0.22), lineWidth: 18).frame(width: g.size.height * 1.25)
                    .offset(x: g.size.width * 0.31, y: g.size.height * 0.05)
                Rectangle().fill(Color.white.opacity(0.16)).frame(width: 16, height: g.size.height * 1.8)
                    .rotationEffect(.degrees(28)).offset(x: g.size.width * 0.14)
                Rectangle().fill(Color.white.opacity(0.10)).frame(width: 46, height: g.size.height * 1.8)
                    .rotationEffect(.degrees(28)).offset(x: g.size.width * 0.20)
                Circle().fill(Couch.flameC.opacity(0.55)).frame(width: g.size.height * 1.1).blur(radius: 60)
                    .offset(x: g.size.width * 0.45, y: g.size.height * 0.55)
            }
            .frame(width: g.size.width, height: g.size.height)
        }
    }
}

struct PlayButton: View {
    let scene: LobbyScene
    let action: () -> Void
    let pulse: Bool

    var body: some View {
        let enabled = scene.canLaunch && !scene.running
        Button(action: action) {
            HStack(spacing: 12) {
                ZStack {
                    Circle().fill(enabled ? Couch.flameA : Couch.inkFaint.opacity(0.35)).frame(width: 46, height: 46)
                    Image(systemName: scene.running ? "dot.radiowaves.left.and.right" : "play.fill")
                        .font(.system(size: 19, weight: .heavy)).foregroundStyle(.white).offset(x: scene.running ? 0 : 2)
                }
                VStack(alignment: .leading, spacing: 2) {
                    Text(scene.running ? "Playing" : "Play").font(Couch.font(26, .heavy)).foregroundStyle(Couch.ink)
                    HStack(spacing: 5) {
                        if scene.running {
                            Text("Quit with ⌘Q to return").font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkSoft)
                        } else if enabled {
                            Text("Press").font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkSoft)
                            MenuGlyph(family: scene.guideFamily).scaleEffect(0.82)
                            Text("or click").font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkSoft)
                        } else {
                            Text("Join a player first").font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkSoft)
                        }
                    }
                }
            }
            .padding(.leading, 14).padding(.trailing, 30).frame(height: 76)
            .background(Capsule().fill(Color.white))
            .overlay(Capsule().strokeBorder(Color.white.opacity(0.9), lineWidth: enabled && pulse ? 5 : 0).blur(radius: 2).opacity(0.6))
            .shadow(color: .black.opacity(0.18), radius: 16, y: 8)
            .scaleEffect(enabled && pulse ? 1.025 : 1)
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
    }
}

struct PlayerGrid: View {
    let scene: LobbyScene
    let actions: LobbyActions
    let pulse: Bool

    var body: some View {
        let count = scene.capacity
        let columns = Array(repeating: GridItem(.flexible(), spacing: 16), count: count <= 4 ? max(count, 1) : 4)
        LazyVGrid(columns: columns, spacing: 16) {
            ForEach(scene.slots.prefix(count)) { slot in
                PlayerCard(slot: slot, compact: count > 4, running: scene.running, actions: actions, pulse: pulse)
            }
        }
        .animation(.spring(response: 0.45, dampingFraction: 0.82), value: count)
    }
}

struct PlayerCard: View {
    let slot: PlayerSlotScene
    let compact: Bool
    let running: Bool
    let actions: LobbyActions
    let pulse: Bool
    @State private var label = ""
    @State private var editing = false
    @State private var choosingLayout = false

    private var color: Color { Couch.player(slot.id) }
    private var joined: Bool { slot.phase == .ready || slot.phase == .releasing }
    private var glyphFamily: ControllerFamily { slot.family.map { $0 == .generic ? .xbox : $0 } ?? .xbox }
    private var removable: Bool { !running && (slot.controllerName != nil || slot.phase == .reserved) }

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 8 : 12) {
            HStack(spacing: 8) {
                Text("P\(slot.id + 1)").font(Couch.font(compact ? 18 : 24, .heavy)).foregroundStyle(.white)
                    .padding(.horizontal, compact ? 10 : 12).frame(height: compact ? 30 : 38)
                    .background(Capsule().fill(joined ? color : color.opacity(0.35)))
                nameField
                Spacer(minLength: 0)
                if slot.controllerName != nil {
                    Button { actions.identifySlot(slot.id) } label: {
                        Image(systemName: "waveform").font(.system(size: 13, weight: .bold))
                            .frame(width: 30, height: 30).background(Circle().fill(Couch.canvasBottom))
                    }
                    .buttonStyle(.plain).foregroundStyle(Couch.inkSoft).help("Identify: vibrate this controller")
                }
                if removable {
                    Button { actions.removePlayer(slot.id) } label: {
                        Image(systemName: "xmark").font(.system(size: 11, weight: .heavy))
                            .frame(width: 30, height: 30).background(Circle().fill(Couch.canvasBottom))
                    }
                    .buttonStyle(.plain).foregroundStyle(Couch.inkSoft).help("Remove this player and free the slot")
                }
            }
            if !compact {
                SlotIllustration(slot: slot, color: color, compact: false, pulse: pulse)
                    .frame(maxWidth: .infinity)
            }
            HStack(spacing: 10) {
                if compact {
                    SlotIllustration(slot: slot, color: color, compact: true, pulse: pulse)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(headline).font(Couch.font(compact ? 15 : 17, .bold)).foregroundStyle(Couch.ink).lineLimit(1)
                    Text(detail).font(Couch.font(compact ? 11 : 12, .medium)).foregroundStyle(Couch.inkSoft).lineLimit(compact ? 1 : 2)
                }
            }
            if !compact {
                LayoutMapping(layout: slot.layout, family: glyphFamily, size: 20, pressed: slot.pressedFaces, tint: color)
            }
            layoutButton
        }
        .padding(compact ? 14 : 18)
        .frame(maxWidth: .infinity, minHeight: compact ? 0 : 236, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 24, style: .continuous).fill(Couch.card))
        .overlay(RoundedRectangle(cornerRadius: 24, style: .continuous)
            .strokeBorder(borderColor, lineWidth: joined || selecting ? 3 : 1))
        .shadow(color: (joined ? color : Color.black).opacity(joined ? 0.18 : 0.06), radius: slot.activity ? 18 : 12, y: 6)
        .scaleEffect(slot.activity ? 1.015 : 1)
        .animation(.spring(response: 0.35, dampingFraction: 0.7), value: slot.activity)
        .animation(.spring(response: 0.45, dampingFraction: 0.8), value: slot.phase)
        .onChange(of: running) { _, isRunning in
            if isRunning { editing = false; choosingLayout = false }
        }
    }

    /// Player name in the card header; the pencil turns it into a text field (lobby only).
    private var nameField: some View {
        HStack(spacing: 5) {
            if editing {
                TextField("Name", text: $label, onCommit: { actions.setLabel(label, slot.id); editing = false })
                    .textFieldStyle(.plain).font(Couch.font(13, .bold)).foregroundStyle(Couch.ink)
                    .frame(maxWidth: 140)
            } else {
                Text(slot.label).font(Couch.font(13, .bold)).foregroundStyle(joined ? Couch.ink : Couch.inkSoft).lineLimit(1)
            }
            if !running {
                Button {
                    if editing { actions.setLabel(label, slot.id) } else { label = slot.label }
                    editing.toggle()
                } label: {
                    Image(systemName: editing ? "checkmark" : "pencil").font(.system(size: 10, weight: .bold))
                        .frame(width: 18, height: 22).contentShape(Rectangle())
                }
                .buttonStyle(.plain).foregroundStyle(Couch.inkFaint).help("Rename this player")
            }
        }
    }

    private var selecting: Bool { if case .selecting = slot.phase { return true } else { return false } }

    private var layoutButton: some View {
        Button { if !running { choosingLayout = true } } label: {
            HStack(spacing: 6) {
                Image(systemName: "gamecontroller.fill").font(.system(size: 10, weight: .bold)).foregroundStyle(color)
                Text(slot.layout.title(southLabel: glyphFamily.southLabel))
                    .font(Couch.font(12, .bold)).foregroundStyle(Couch.ink).lineLimit(1)
                Spacer(minLength: 4)
                if !running {
                    Image(systemName: "chevron.up.chevron.down").font(.system(size: 9, weight: .bold)).foregroundStyle(Couch.inkFaint)
                }
            }
            .padding(.horizontal, 10).frame(height: 28)
            .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(color.opacity(0.09)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .frame(maxWidth: .infinity)
        .help(running ? "Layouts can change between games" : "Choose this player's button layout (or press LB / RB)")
        .popover(isPresented: $choosingLayout, arrowEdge: .bottom) {
            LayoutPicker(slot: slot, family: glyphFamily, color: color) { layout in
                actions.setLayout(slot.id, layout)
                choosingLayout = false
            }
        }
    }

    private var borderColor: Color {
        switch slot.phase {
        case .ready: return color
        case .releasing: return Couch.caution
        case .selecting: return color.opacity(pulse ? 0.9 : 0.35)
        default: return Couch.hairline
        }
    }

    private var headline: String {
        switch slot.phase {
        case .open: return "Open"
        case .selecting(let names): return names.count == 1 ? "\(shortName(names[0])) is here" : "\(names.count) controllers here"
        case .releasing: return "Let go of all buttons"
        case .ready: return "Ready"
        case .reserved: return "Reconnect to reclaim"
        }
    }

    private var detail: String {
        let menu = (slot.family ?? .xbox).menuName
        switch slot.phase {
        case .open: return "Move here with the D-pad, then press \(menu)"
        case .selecting: return "Press \(menu) to join as P\(slot.id + 1)"
        case .releasing: return "\(shortName(slot.controllerName ?? "Controller")) joined"
        case .ready: return shortName(slot.controllerName ?? "Controller")
        case .reserved: return "Turn the controller back on and press \(menu)"
        }
    }
}

func shortName(_ name: String) -> String {
    let n = name.lowercased()
    if n.contains("xbox") { return "Xbox Controller" }
    if n.contains("dualsense edge") { return "DualSense Edge" }
    if n.contains("dualsense") { return "DualSense" }
    if n.contains("dualshock") { return "DualShock 4" }
    if n.contains("pro controller") { return "Switch Pro Controller" }
    if n.contains("joy-con") { return "Joy-Con" }
    return name
}

struct SlotIllustration: View {
    let slot: PlayerSlotScene
    let color: Color
    let compact: Bool
    let pulse: Bool

    var body: some View {
        let size: CGFloat = compact ? 40 : 80
        ZStack {
            switch slot.phase {
            case .open:
                Circle().strokeBorder(style: StrokeStyle(lineWidth: 2.5, dash: [7, 6])).foregroundStyle(Couch.inkFaint.opacity(0.6))
                Image(systemName: "plus").font(.system(size: size * 0.28, weight: .bold)).foregroundStyle(Couch.inkFaint)
            case .selecting:
                Circle().fill(color.opacity(0.12))
                Circle().stroke(color.opacity(pulse ? 0.65 : 0.2), lineWidth: 6).scaleEffect(pulse ? 1.06 : 0.94)
                Image(systemName: "gamecontroller.fill").font(.system(size: size * 0.34, weight: .bold)).foregroundStyle(color)
            case .releasing:
                Circle().fill(Couch.caution.opacity(0.14))
                Image(systemName: "hand.raised.fill").font(.system(size: size * 0.34, weight: .bold)).foregroundStyle(Couch.caution)
            case .ready:
                Circle().fill(LinearGradient(colors: [color, color.opacity(0.75)], startPoint: .topLeading, endPoint: .bottomTrailing))
                Image(systemName: "gamecontroller.fill").font(.system(size: size * 0.36, weight: .bold)).foregroundStyle(.white)
                Image(systemName: "checkmark.circle.fill").font(.system(size: size * 0.26, weight: .bold))
                    .foregroundStyle(.white, Couch.ready).offset(x: size * 0.34, y: size * 0.34)
            case .reserved:
                Circle().fill(Couch.canvasBottom)
                Image(systemName: "bolt.horizontal.circle").font(.system(size: size * 0.36, weight: .semibold)).foregroundStyle(Couch.inkFaint)
            }
        }
        .frame(width: size, height: size)
        .shadow(color: slot.phase == .ready ? color.opacity(0.35) : .clear, radius: 10, y: 4)
    }
}

struct StatusToast: View {
    let message: String
    let running: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: running ? "dot.radiowaves.left.and.right" : "info.circle.fill")
                .font(.system(size: 15, weight: .bold)).foregroundStyle(running ? Couch.ready : Couch.inkFaint)
            Text(message).font(Couch.font(13, .medium)).foregroundStyle(Couch.inkSoft)
                .fixedSize(horizontal: false, vertical: true).lineLimit(3)
        }
        .padding(14).frame(maxWidth: 520, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 16, style: .continuous).fill(Color.white.opacity(0.75)))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(Couch.hairline))
    }
}

/// Compact "Attack [B][X]  Special [A]  Jump [Y]" row for one layout.
struct LayoutMapping: View {
    let layout: ControllerLayout
    let family: ControllerFamily
    var size: CGFloat = 20
    var pressed: [Int] = []
    var tint: Color = Couch.ink

    var body: some View {
        HStack(spacing: 10) {
            ForEach([SmashAction.attack, .special, .jump], id: \.self) { action in
                HStack(spacing: 3) {
                    Text(action.title).font(Couch.font(10, .bold)).foregroundStyle(Couch.inkSoft)
                    ForEach(layout.faces(for: action), id: \.self) { face in
                        let held = pressed.contains(face)
                        FaceGlyph(family: family, button: FaceButton.layoutOrder[face], size: size)
                            .overlay(Circle().strokeBorder(tint, lineWidth: held ? 2.5 : 0).padding(-3))
                            .scaleEffect(held ? 1.22 : 1)
                            .animation(.spring(response: 0.18, dampingFraction: 0.6), value: held)
                    }
                }
            }
        }
    }
}

struct LayoutPicker: View {
    let slot: PlayerSlotScene
    let family: ControllerFamily
    let color: Color
    let choose: (ControllerLayout) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("P\(slot.id + 1) button layout").font(Couch.font(15, .heavy)).foregroundStyle(Couch.ink)
            Text("Shield, grab, sticks and D-pad stay the same.").font(Couch.font(11, .medium)).foregroundStyle(Couch.inkFaint)
            ForEach(ControllerLayout.allCases) { layout in
                Button { choose(layout) } label: {
                    HStack(alignment: .center, spacing: 12) {
                        Image(systemName: layout == slot.layout ? "checkmark.circle.fill" : "circle")
                            .font(.system(size: 16, weight: .bold)).foregroundStyle(layout == slot.layout ? color : Couch.inkFaint)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(layout.title(southLabel: family.southLabel)).font(Couch.font(13, .bold)).foregroundStyle(Couch.ink)
                            Text(layout.summary).font(Couch.font(11, .medium)).foregroundStyle(Couch.inkSoft)
                            LayoutMapping(layout: layout, family: family, size: 18)
                        }
                        Spacer(minLength: 0)
                    }
                    .padding(10)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .fill(layout == slot.layout ? color.opacity(0.10) : Couch.canvasTop))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(16).frame(width: 340)
    }
}

struct ControlsCard: View {
    let family: ControllerFamily
    var slot: PlayerSlotScene? = nil

    var body: some View {
        let layout = slot?.layout ?? .default
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(slot.map { "Controls · P\($0.id + 1)" } ?? "Controls").font(Couch.font(15, .bold)).foregroundStyle(Couch.ink)
                Spacer()
                Text(layout.title(southLabel: glyphFamily.southLabel)).font(Couch.font(12, .semibold)).foregroundStyle(Couch.inkFaint)
            }
            HStack(spacing: 18) {
                ForEach([SmashAction.attack, .special, .jump], id: \.self) { action in
                    row(action.title) {
                        ForEach(layout.faces(for: action), id: \.self) { face in
                            FaceGlyph(family: glyphFamily, button: FaceButton.layoutOrder[face])
                        }
                    }
                }
                row("Shield") { TriggerGlyphs(family: glyphFamily) }
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(Color.white))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(Couch.hairline))
        .shadow(color: .black.opacity(0.05), radius: 10, y: 4)
    }

    private var glyphFamily: ControllerFamily {
        let shown = slot?.family ?? family
        return shown == .generic ? .xbox : shown
    }

    private func row<G: View>(_ title: String, @ViewBuilder glyphs: () -> G) -> some View {
        VStack(spacing: 6) {
            HStack(spacing: 4) { glyphs() }.frame(height: 28)
            Text(title).font(Couch.font(11, .bold)).foregroundStyle(Couch.inkSoft)
        }
    }
}

struct HintBar: View {
    let family: ControllerFamily
    let running: Bool

    var body: some View {
        HStack(spacing: 22) {
            hint { Image(systemName: "dpad.fill").font(.system(size: 17)).foregroundStyle(Couch.ink) } text: { Text("Choose player") }
            hint { MenuGlyph(family: family) } text: { Text("Join · Play") }
            hint { Image(systemName: "hand.raised.fill").font(.system(size: 14)).foregroundStyle(Couch.ink) } text: { Text("Let go to get ready") }
            if !running {
                hint { ShoulderGlyphs(family: family) } text: { Text("Layout") }
                hint { KeyCap(text: family.viewName) } text: { Text("Leave") }
            }
            Spacer()
            Text(running ? "Quit the game with ⌘Q to come back" : "Reconnect + \(family.menuName) reclaims a slot")
                .font(Couch.font(12, .medium)).foregroundStyle(Couch.inkFaint)
        }
        .padding(.top, 4)
    }

    private func hint<I: View, T: View>(@ViewBuilder icon: () -> I, @ViewBuilder text: () -> T) -> some View {
        HStack(spacing: 7) { icon(); text().font(Couch.font(13, .semibold)).foregroundStyle(Couch.inkSoft) }
    }
}
