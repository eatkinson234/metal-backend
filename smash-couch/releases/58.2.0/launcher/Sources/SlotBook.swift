import Foundation

struct PadSample: Equatable {
    var buttons: UInt32 = 0
    var axes: [Float] = Array(repeating: 0, count: 6)

    static let neutral = PadSample()
    var isNeutral: Bool { axes.count == 6 && buttons == 0 && axes.allSatisfy { abs($0) < 0.08 } }
    func down(_ bit: Int) -> Bool { buttons & (1 << bit) != 0 }

    /// The default layout's mapping (Smash standard: every face button in its Switch position).
    func mappedForNativeLobby() -> PadSample { mapped(for: .default) }
}

struct SlotState {
    var connection: UUID?
    var reserved = false
    var ready = false
    var sample = PadSample.neutral
}

struct SlotJoinRequest {
    let connection: UUID
    let slot: Int
}

enum SlotJoinOutcome: Equatable {
    case none
    case joined(Int)
    case occupied(Int)
    case invalidSlot(Int)
    case duplicateConnection(Int)
    case ambiguous
}

struct SlotBook {
    // SCINPUT2 in little-endian order. The receiver rejects the prior SCINPUT1 format.
    static let packetMagicV2: UInt64 = 0x325455504e494353
    static let packetHeaderBytes = 40
    static let packetBytes = packetHeaderBytes + 8 * 32

    var slots = Array(repeating: SlotState(), count: 8)
    var capacity = 1

    mutating func setCapacity(_ requested: Int) -> Bool {
        guard (1...8).contains(requested) else { return false }
        if requested < capacity && slots[requested...].contains(where: { $0.reserved || $0.connection != nil }) {
            return false
        }
        capacity = requested
        return true
    }

    /// Lowers or raises capacity, releasing every joined or reserved slot above it.
    /// Returns the released slot indices; released controllers can join again with Menu.
    mutating func setCapacity(_ requested: Int, evicting: Bool) -> [Int]? {
        guard (1...8).contains(requested) else { return nil }
        guard evicting else { return setCapacity(requested) ? [] : nil }
        let released = (requested..<8).filter { slots[$0].reserved || slots[$0].connection != nil }
        for slot in released { clear(slot) }
        capacity = requested
        return released
    }

    /// Frees one slot completely: its controller (if any) becomes unassigned and the reservation ends.
    mutating func clear(_ slot: Int) {
        guard slots.indices.contains(slot) else { return }
        slots[slot] = SlotState()
    }

    mutating func join(_ connection: UUID, at slot: Int) -> SlotJoinOutcome {
        guard (0..<capacity).contains(slot) else { return .invalidSlot(slot) }
        if let existing = slots.firstIndex(where: { $0.connection == connection }) {
            return .duplicateConnection(existing)
        }
        guard slots[slot].connection == nil else { return .occupied(slot) }
        // Joining a reserved-but-disconnected player is always an explicit action
        // against the chosen slot. The new connection starts neutral until release.
        slots[slot] = SlotState(connection: connection, reserved: true, ready: false, sample: .neutral)
        return .joined(slot)
    }

    mutating func applyJoinRequests(_ requests: [SlotJoinRequest]) -> SlotJoinOutcome {
        guard !requests.isEmpty else { return .none }
        guard requests.count == 1 else { return .ambiguous }
        let request = requests[0]
        return join(request.connection, at: request.slot)
    }

    mutating func disconnect(_ connection: UUID) {
        for i in slots.indices where slots[i].connection == connection {
            slots[i].connection = nil
            slots[i].ready = false
            slots[i].sample = .neutral
            // Reservations never compact or transfer when a controller disconnects.
        }
    }

    mutating func update(_ connection: UUID, sample: PadSample) {
        guard let i = slots.firstIndex(where: { $0.connection == connection }) else { return }
        if !slots[i].ready {
            slots[i].ready = sample.isNeutral
            slots[i].sample = .neutral
        } else {
            slots[i].sample = sample
        }
    }

    func canLaunch(_ connection: UUID, current: PadSample, previous: PadSample) -> Bool {
        guard current.buttons == (1 << 16), !previous.down(16),
              current.axes.count == 6, current.axes.allSatisfy({ abs($0) < 0.08 }),
              let i = slots.firstIndex(where: { $0.connection == connection }), i < capacity else {
            return false
        }
        return slots[i].ready
    }

    mutating func suspend() {
        for i in slots.indices {
            slots[i].ready = false
            slots[i].sample = .neutral
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

    static func movedSelection(_ current: Int, direction: Int, capacity: Int) -> Int {
        guard (1...8).contains(capacity), direction == -1 || direction == 1 else { return current }
        return (current + direction + capacity) % capacity
    }
}

struct LobbyPreferences: Codable {
    var capacity: Int
    var labels: [String]
    /// Per-player ControllerLayout raw values; absent in files written before layouts existed.
    var layouts: [String]?

    static let defaults = LobbyPreferences(capacity: 1, labels: (1...8).map { "Controller \($0)" })

    var isValid: Bool {
        // Unknown or missing layout entries fall back to the default in layout(_:), so a file
        // written by a newer launcher never resets capacity or player names.
        (1...8).contains(capacity) && labels.count == 8 && labels.allSatisfy(Self.validLabel)
    }

    func layout(_ slot: Int) -> ControllerLayout {
        guard let layouts, layouts.indices.contains(slot) else { return .default }
        return ControllerLayout(rawValue: layouts[slot]) ?? .default
    }

    mutating func setLayout(_ layout: ControllerLayout, slot: Int) {
        guard (0..<8).contains(slot) else { return }
        var all = layouts ?? []
        if all.count < 8 { all += Array(repeating: ControllerLayout.default.rawValue, count: 8 - all.count) }
        all[slot] = layout.rawValue
        layouts = all
    }

    static func validLabel(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return !trimmed.isEmpty && trimmed.count <= 48 && !trimmed.unicodeScalars.contains(where: CharacterSet.controlCharacters.contains)
    }
}
