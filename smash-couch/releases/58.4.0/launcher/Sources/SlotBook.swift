import Foundation

// One layout for every controller: Smash's own defaults. Physical face positions are GameController
// bits: south=1 (Xbox A), east=2 (B), west=3 (X), north=4 (Y). The native receiver's Config is
// positional, so output bit 1 is Switch B (Special), bit 2 is Switch A (Attack/confirm) and bits 3/4
// are Switch Y/X (Jump). Every button passes straight through; players who want different buttons
// change them in Smash (Options → Controls). Saved layout choices from 58.2.0 and earlier are ignored.

struct PadSample: Equatable {
    var buttons: UInt32 = 0
    var axes: [Float] = Array(repeating: 0, count: 6)

    static let neutral = PadSample()
    var isNeutral: Bool { axes.count == 6 && buttons == 0 && axes.allSatisfy { abs($0) < 0.08 } }
    func down(_ bit: Int) -> Bool { buttons & (1 << bit) != 0 }

    /// Face positions held right now (0 south, 1 east, 2 west, 3 north), for the lobby's glyphs.
    var pressedFaces: [Int] { (0..<4).filter { down($0 + 1) } }

    /// Converts Nintendo label order (bit 1 = A on the right, 2 = B bottom, 3 = X top, 4 = Y left)
    /// to positional order (1 = bottom, 2 = right, 3 = left, 4 = top). Other bits pass through.
    static func positionalFaces(fromLabelOrdered bits: UInt32) -> UInt32 {
        let a = bits & (1 << 1), b = bits & (1 << 2), x = bits & (1 << 3), y = bits & (1 << 4)
        return bits & ~UInt32(0b11110) | (a << 1) | (b >> 1) | (x << 1) | (y >> 1)
    }
}

/// Shield (ZL/ZR) is digital on a Pro Controller but an analog trigger on Xbox and PlayStation pads, and
/// the emulator counts a trigger only above its 0.5 trigger_threshold, so shield+special, shield+stick
/// (roll, spot dodge, air dodge) and shield grabs reached Smash a frame or more after the other button.
/// Each trigger is latched to exactly 1 or 0 instead (both pass the emulator's 0.5 check unchanged):
/// pressed early, let go at the old threshold after any press past 0.58, and with enough hysteresis that
/// a wobbling finger never drops shield or flickers it on. The price of that hysteresis: a let-go from a
/// press peaking at 0.50-0.58 comes at most 0.08 of travel later than the old 0.5, and a re-press after a
/// partial let-go (lowest point 0.35-0.49) needs a 0.15 rise, at most 0.14 later than the old 0.5.
struct TriggerLatch: Equatable {
    /// Press from rest at this much travel.
    static let pressAt: Float = 0.20
    /// After a partial let-go, press again once the trigger rises this far above its lowest point.
    static let repressRise: Float = 0.15
    /// Always released at or below this much travel.
    static let releaseAt: Float = 0.10
    /// Also released at or below the emulator's own 0.5 threshold once the trigger has fallen below its
    /// highest point since the press by...
    static let releaseDropCeiling: Float = 0.5
    /// ...0.08 when that point was past 0.5 (so from a press past 0.58 the let-go is exactly the old one)...
    static let releaseDropAboveCeiling: Float = 0.08
    /// ...or 0.20 from a lighter press, which the old threshold never counted at all.
    static let releaseDrop: Float = 0.20

    private(set) var pressed = false
    /// Highest value since the press, and lowest value since the release.
    private var peak: Float = 0
    private var trough: Float = 0

    mutating func step(_ value: Float) -> Bool {
        let travel = value.isFinite ? min(max(value, 0), 1) : 0
        if pressed {
            peak = max(peak, travel)
            let drop = peak > Self.releaseDropCeiling ? Self.releaseDropAboveCeiling : Self.releaseDrop
            if travel <= Self.releaseAt || (travel <= Self.releaseDropCeiling && travel <= peak - drop) {
                pressed = false
                trough = travel
            }
        } else {
            trough = min(trough, travel)
            // From rest this is pressAt; after a partial let-go it is a fresh rise above the lowest point.
            if travel >= max(Self.pressAt, trough + Self.repressRise) {
                pressed = true
                peak = travel
            }
        }
        return pressed
    }
}

/// One pair of trigger latches per connected controller: left is axis 4, right is axis 5.
struct TriggerLatchBook {
    private struct Pair { var left = TriggerLatch(), right = TriggerLatch() }
    private var latches: [UUID: Pair] = [:]

    /// The sample with both triggers latched to exactly 0 or 1; buttons and sticks are unchanged.
    mutating func latch(_ connection: UUID, _ sample: PadSample) -> PadSample {
        guard sample.axes.count == 6 else { return sample }
        var latched = sample
        latched.axes[4] = latches[connection, default: Pair()].left.step(sample.axes[4]) ? 1 : 0
        latched.axes[5] = latches[connection, default: Pair()].right.step(sample.axes[5]) ? 1 : 0
        return latched
    }

    /// The controller left its spot or disconnected: its next sample starts from rest.
    mutating func reset(_ connection: UUID) { latches.removeValue(forKey: connection) }

    var count: Int { latches.count }
}

struct SlotState {
    var connection: UUID?
    var reserved = false
    var ready = false
    var sample = PadSample.neutral
    /// Buttons that were held at a hand-over (the Menu press that started the game, or the game
    /// exiting). Each stays out of the game until it is released; everything else keeps flowing.
    var withheld: UInt32 = 0
}

struct SlotJoinRequest {
    let connection: UUID
    /// Connection order (first connected, first numbered); breaks ties between presses in one poll.
    let order: UInt64
}

enum SlotJoinOutcome: Equatable {
    case joined(Int)
    /// Took a disconnected player's held spot.
    case reclaimed(Int)
    case alreadyJoined(Int)
    case full
}

struct SlotBook {
    // SCINPUT2 in little-endian order. The receiver rejects the prior SCINPUT1 format.
    static let packetMagicV2: UInt64 = 0x325455504e494353
    static let packetHeaderBytes = 40
    static let packetBytes = packetHeaderBytes + 8 * 32
    static let slotCount = 8
    /// Any face button (south, east, west, north) or Menu joins a controller that has not joined.
    static let joinButtons: UInt32 = 0b1_1110 | 1 << 16
    /// Menu cannot start the game until this long after the latest join, so a friend still
    /// joining (or a double tap on Menu) never starts the game by accident.
    static let joinSettle: TimeInterval = 1.0

    var slots = Array(repeating: SlotState(), count: SlotBook.slotCount)
    /// Spots open in this session: all eight in the lobby, and the launched profile count while the
    /// game runs (the emulator reads its controller profiles only at boot).
    private(set) var capacity = SlotBook.slotCount
    private(set) var lastJoinAt: Date?
    /// Set by suspend() (a stop request or supervisor incident) until the session ends or a new one begins:
    /// every pad stays neutral, even after its player lets go and presses again, while the exact child exits.
    private(set) var stopping = false

    /// Whether every launch writes all eight Pro Controller profiles, so friends can join mid-game without a
    /// restart. On since the rig runs c58r15-cap8 and r16-cap8 (2026-10-11/12): with eight connected
    /// controllers Smash's title, versus fighter screen, a stock match, Classic, Spirits and its Controllers
    /// menu all behave, with no ControllerApplet line in the log. The emulator reports every profile as
    /// connected, and its controller applet shows a blocking host dialog if a mode asks for fewer players than
    /// are connected, so set this back to false (one line) if a mode ever does.
    static let launchesAllEight = true

    /// How many Pro Controller profiles a launch writes: enough for the highest joined or held spot (spots
    /// never move), as 58.2.0's chosen count did. Mid-game joins then fill open or held spots up to that
    /// count until the game exits. With launchesAllEight, always all eight.
    static func launchProfileCount(joinedOrHeld: [Int], allEight: Bool = launchesAllEight) -> Int {
        allEight ? slotCount : min(max(1, (joinedOrHeld.max() ?? 0) + 1), slotCount)
    }

    /// The lobby message once a game starts.
    static func playingMessage(joined: Int, profiles: Int) -> String {
        let playing = "Playing with \(joined) \(joined == 1 ? "player" : "players")."
        guard profiles < slotCount else {
            return playing + " Late? Press A on any controller to join, even mid-game. Quit the game with ⌘Q to come back and save new shaders."
        }
        return playing + " To add players, quit the game with ⌘Q (it saves new shaders), then press A to join. A dropped controller can rejoin mid-game."
    }

    /// Spots that are joined or held for a disconnected player.
    var takenSlots: [Int] { slots.indices.filter { slots[$0].reserved || slots[$0].connection != nil } }

    mutating func beginSession(profiles: Int) {
        capacity = min(max(profiles, 1), Self.slotCount)
        stopping = false
    }

    mutating func endSession() {
        capacity = Self.slotCount
        stopping = false
    }

    /// Frees one slot completely: its controller (if any) becomes unassigned and the reservation ends.
    mutating func clear(_ slot: Int) {
        guard slots.indices.contains(slot) else { return }
        slots[slot] = SlotState()
    }

    static func isJoinPress(_ now: PadSample, _ before: PadSample) -> Bool {
        now.buttons & ~before.buttons & joinButtons != 0
    }

    /// A disconnected player's held spot first (lowest), then the lowest open spot.
    func nextOpenSlot() -> Int? {
        let open = slots.indices.prefix(capacity)
        return open.first { slots[$0].reserved && slots[$0].connection == nil }
            ?? open.first { !slots[$0].reserved && slots[$0].connection == nil }
    }

    /// Accepts every join pressed in one poll. Requests are taken in connection order, whatever order
    /// they arrive in, so simultaneous joins always number the same way. A new player starts neutral
    /// and not ready until it releases everything, so the join press never reaches the game.
    mutating func joinAll(_ requests: [SlotJoinRequest], at now: Date) -> [(UUID, SlotJoinOutcome)] {
        var seen = Set<UUID>()
        var outcomes: [(UUID, SlotJoinOutcome)] = []
        let ordered = requests.sorted { ($0.order, $0.connection.uuidString) < ($1.order, $1.connection.uuidString) }
        for request in ordered where seen.insert(request.connection).inserted {
            if let existing = slots.firstIndex(where: { $0.connection == request.connection }) {
                outcomes.append((request.connection, .alreadyJoined(existing)))
                continue
            }
            guard let slot = nextOpenSlot() else {
                outcomes.append((request.connection, .full))
                continue
            }
            let held = slots[slot].reserved
            slots[slot] = SlotState(connection: request.connection, reserved: true, ready: false, sample: .neutral)
            lastJoinAt = now
            outcomes.append((request.connection, held ? .reclaimed(slot) : .joined(slot)))
        }
        return outcomes
    }

    mutating func disconnect(_ connection: UUID) {
        for i in slots.indices where slots[i].connection == connection {
            slots[i].connection = nil
            slots[i].ready = false
            slots[i].sample = .neutral
            slots[i].withheld = 0
            // Reservations never compact or transfer when a controller disconnects.
        }
    }

    mutating func update(_ connection: UUID, sample: PadSample) {
        guard let i = slots.firstIndex(where: { $0.connection == connection }) else { return }
        if stopping {
            slots[i].ready = false
            slots[i].sample = .neutral
            slots[i].withheld = 0
        } else if !slots[i].ready {
            // Join gate: a new or returning player sends nothing until it lets go of everything,
            // so the press that joined never reaches the game.
            slots[i].ready = sample.isNeutral
            slots[i].sample = .neutral
            slots[i].withheld = 0
        } else {
            // A withheld button counts again once it has been released; new presses pass at once.
            slots[i].withheld &= sample.buttons
            var live = sample
            live.buttons &= ~slots[i].withheld
            slots[i].sample = live
        }
    }

    /// Hand-over without freezing anyone (the Menu press that starts the game, the game exiting): the
    /// buttons each ready player holds right now are withheld until that button is released, while
    /// sticks, triggers and new presses keep flowing. A player still in the join gate stays in it.
    mutating func withholdHeldButtons() {
        for i in slots.indices where slots[i].ready {
            slots[i].withheld |= slots[i].sample.buttons
            slots[i].sample.buttons = 0
        }
    }

    /// A fresh Menu press alone (sticks and triggers at rest) on a joined, ready controller.
    func isLaunchPress(_ connection: UUID, current: PadSample, previous: PadSample) -> Bool {
        guard current.buttons == (1 << 16), !previous.down(16),
              current.axes.count == 6, current.axes.allSatisfy({ abs($0) < 0.08 }),
              let i = slots.firstIndex(where: { $0.connection == connection }), i < capacity else {
            return false
        }
        return slots[i].ready
    }

    func isSettling(at now: Date) -> Bool {
        lastJoinAt.map { now < $0.addingTimeInterval(Self.joinSettle) } ?? false
    }

    func canLaunch(_ connection: UUID, current: PadSample, previous: PadSample, at now: Date) -> Bool {
        isLaunchPress(connection, current: current, previous: previous) && !isSettling(at: now)
    }

    /// Four cards until a fifth spot is needed: a player in P5-P8, or P1-P4 taken with a controller waiting.
    func visibleSlotCount(waiting: Int) -> Int {
        let taken = takenSlots
        return taken.contains(where: { $0 >= 4 }) || (taken.count >= 4 && waiting > 0) ? 8 : 4
    }

    /// Whole pads neutral until the session ends (see stopping). Only for stopping a game that is being
    /// closed (a stop request or a supervisor incident); starting and exiting use withholdHeldButtons().
    mutating func suspend() {
        stopping = true
        for i in slots.indices {
            slots[i].ready = false
            slots[i].sample = .neutral
            slots[i].withheld = 0
        }
    }

    func packet(token: [UInt8], sequence: UInt64, captureTime: UInt64, enabled: Bool) -> Data {
        precondition(token.count == 16)
        precondition(captureTime != 0)
        var data = Data()
        func append32(_ value: UInt32) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }
        func append64(_ value: UInt64) {
            var little = value.littleEndian
            withUnsafeBytes(of: &little) { data.append(contentsOf: $0) }
        }

        append64(Self.packetMagicV2)
        data.append(contentsOf: token)
        append64(sequence)
        append64(captureTime)
        for (index, slot) in slots.enumerated() {
            let live = enabled && index < capacity && slot.connection != nil && slot.ready
            append32(live ? 1 : 0)
            append32(live ? slot.sample.buttons : 0)
            let axes = live ? slot.sample.axes : PadSample.neutral.axes
            if live { precondition(axes.count == 6, "Every live slot must contain six axes") }
            for axis in 0..<6 { append32(axes[axis].bitPattern) }
        }
        precondition(data.count == Self.packetBytes)
        return data
    }

    /// The lobby message when a controller connects. Presses only count with the lobby (or a running game) in
    /// front, so someone who just paired it in System Settings is told to click back on the lobby first.
    static func connectedMessage(_ name: String, actionsAllowed: Bool) -> String {
        actionsAllowed ? "\(name) connected. Press A to join." : "\(name) connected. Click this window, then press A to join."
    }

    /// The lobby message for one poll's joins, or nil when nothing changed.
    static func joinMessage(_ outcomes: [SlotJoinOutcome], capacity: Int = slotCount, running: Bool = false) -> String? {
        var joined: [Int] = [], back: [Int] = [], full = false
        for outcome in outcomes {
            switch outcome {
            case let .joined(slot): joined.append(slot)
            case let .reclaimed(slot): back.append(slot)
            case .alreadyJoined: break
            case .full: full = true
            }
        }
        func names(_ slots: [Int]) -> String {
            let all = slots.sorted().map { "P\($0 + 1)" }
            return all.count < 2 ? all.joined() : all.dropLast().joined(separator: ", ") + " and " + all.last!
        }
        var parts: [String] = []
        if !joined.isEmpty { parts.append("\(names(joined)) joined") }
        if !back.isEmpty { parts.append("\(names(back)) \(back.count == 1 ? "is" : "are") back") }
        // Mid-game the game only has the spots it launched with, and View (leave) works only in the lobby.
        let fullText = running
            ? "All \(capacity) spots in this game are taken. To add players, quit the game with ⌘Q, then press A."
            : "All \(capacity) spots are taken. Press View on a joined controller in the lobby to free one."
        if parts.isEmpty { return full ? fullText : nil }
        return parts.joined(separator: " and ") + ". Let go of all buttons to get ready." + (full ? " " + fullText : "")
    }
}

/// Which app is in front decides only whether controller presses act on the lobby (join, start,
/// leave). While the game runs, its players stay live whatever is in front, so a click on another
/// window or a helper that briefly takes focus never freezes the couch.
struct LobbyFocus: Equatable {
    var launcherInFront: Bool
    var gameInFront: Bool
    var running: Bool
    var hasControllers: Bool

    init(frontmostPID: pid_t?, launcherPID: pid_t, gamePID: pid_t?, running: Bool, hasControllers: Bool) {
        launcherInFront = frontmostPID != nil && frontmostPID == launcherPID
        gameInFront = gamePID != nil && frontmostPID == gamePID
        self.running = running
        self.hasControllers = hasControllers
    }

    /// Join, start and leave need the lobby in front; mid-game joins also count from the game window.
    var allowsActions: Bool { launcherInFront || (running && gameInFront) }
    /// Packets carry live input whenever the game runs.
    var inputLive: Bool { running }
    /// The 4 ms poll runs while the game runs or the lobby is in front, otherwise 100 ms.
    var fastPoll: Bool { hasControllers && (running || launcherInFront) }
    var latencyCritical: Bool { running && hasControllers }
}

/// What tick() packs per controller to decide whether the lobby must redraw. While the game runs,
/// button activity is left out: the lobby is hidden and must not re-render at the 4 ms input rate.
enum LobbyFingerprint {
    static func slot(_ index: Int, _ state: SlotState, sample: PadSample?, active: Bool, running: Bool) -> Int {
        var bits = (state.connection != nil ? 1 : 0) | (state.ready ? 2 : 0) | (state.reserved ? 4 : 0)
        if !running && state.connection != nil {
            if active { bits |= 8 }
            bits |= faces(sample) << 4  // face buttons light up on the card
        }
        return index << 8 | bits
    }

    /// A controller that has not joined: its face buttons animate the "controllers here" strip.
    static func waiting(sample: PadSample?, active: Bool, running: Bool) -> Int {
        running ? 0x10000 : 0x10000 | faces(sample) << 1 | (active ? 1 : 0)
    }

    private static func faces(_ sample: PadSample?) -> Int { Int((sample?.buttons ?? 0) >> 1 & 0xF) }
}

struct LobbyPreferences: Codable {
    var labels: [String]
    /// 58.2.0's player count, only carried for a rollback (see below); this launcher never uses it.
    private(set) var rollbackCapacity = 1

    static let defaults = LobbyPreferences(labels: (1...8).map { "Controller \($0)" })

    init(labels: [String]) { self.labels = labels }

    // Files from 58.2.0 and earlier also hold a player count and per-player layouts; both are
    // ignored. The count is still written because 58.2.0 requires it (1...8) and launches with it:
    // the count 58.2.0 last saved goes back unchanged (or its default, 1), so a rollback starts with
    // the player names and the same count as before, never an untried eight.
    private enum CodingKeys: String, CodingKey { case capacity, labels }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        labels = try container.decode([String].self, forKey: .labels)
        if let saved = try? container.decodeIfPresent(Int.self, forKey: .capacity), (1...8).contains(saved) {
            rollbackCapacity = saved
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(rollbackCapacity, forKey: .capacity)
        try container.encode(labels, forKey: .labels)
    }

    var isValid: Bool { labels.count == 8 && labels.allSatisfy(Self.validLabel) }

    static func validLabel(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && trimmed.count <= 48 && !trimmed.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
    }
}
