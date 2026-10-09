import Foundation

struct PadSample: Equatable {
    var buttons: UInt32 = 0
    var axes: [Float] = Array(repeating: 0, count: 6)
    static let neutral = PadSample()
    var isNeutral: Bool { buttons == 0 && axes.allSatisfy { abs($0) < 0.08 } }
    func down(_ bit: Int) -> Bool { buttons & (1 << bit) != 0 }
    func mapped(for layout: String) -> PadSample {
        guard let preset = ControllerLayout(rawValue: layout), preset != .positions, preset != .letters else { return self }
        var result = self
        let faceMask: UInt32 = (1 << 1) | (1 << 2) | (1 << 3) | (1 << 4)
        result.buttons &= ~faceMask
        // Recompute from the complete raw snapshot. Releasing one of two buttons
        // mapped to the same action must not release the other held button.
        for source in FaceButton.allCases where down(source.rawValue) {
            result.buttons |= 1 << UInt32(preset.output(for: source).rawValue)
        }
        return result
    }
}
struct PlayerProfile: Codable {
    var label: String
    var layout: String = "positions"
    var controllerFamily: ControllerFamily = .automatic
    init(label: String, layout: String = "positions", controllerFamily: ControllerFamily = .automatic) {
        self.label = label; self.layout = layout; self.controllerFamily = controllerFamily
    }
    enum CodingKeys: String, CodingKey { case label, layout, controllerFamily }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        label = try values.decode(String.self, forKey: .label)
        layout = try values.decodeIfPresent(String.self, forKey: .layout) ?? "positions"
        let family = try values.decodeIfPresent(String.self, forKey: .controllerFamily)
        controllerFamily = family.flatMap(ControllerFamily.init(rawValue:)) ?? .automatic
    }
}
struct SlotState {
    var connection: UUID?
    var reserved = false
    var ready = false
    var sample = PadSample.neutral
}
struct SlotBook {
    // "SCINPUT2" in little-endian order. The magic carries the packet version;
    // receivers must reject the previous SCINPUT1/288-byte format.
    static let packetMagicV2: UInt64 = 0x325455504e494353
    static let packetHeaderBytes = 40
    static let packetBytes = packetHeaderBytes + 8 * 32
    var slots = Array(repeating: SlotState(), count: 8)
    var selected = 0
    var capacity = 1
    mutating func join(_ connection: UUID, at slot: Int) -> Bool {
        guard slot >= 0, slot < capacity, slots[slot].connection == nil,
              !slots.contains(where: { $0.connection == connection }) else { return false }
        slots[slot] = SlotState(connection: connection, reserved: true, ready: false, sample: .neutral)
        return true
    }
    mutating func disconnect(_ connection: UUID) {
        for i in slots.indices where slots[i].connection == connection {
            slots[i].connection = nil; slots[i].ready = false; slots[i].sample = .neutral
            // Never compact or automatically reclaim the reserved slot.
        }
    }
    mutating func update(_ connection: UUID, sample: PadSample) {
        guard let i = slots.firstIndex(where: { $0.connection == connection }) else { return }
        if !slots[i].ready { slots[i].ready = sample.isNeutral; slots[i].sample = .neutral }
        else { slots[i].sample = sample }
    }
    func canLaunch(_ connection: UUID, current: PadSample, previous: PadSample) -> Bool {
        guard current.buttons == (1 << 16), !previous.down(16),
              current.axes.allSatisfy({ abs($0) < 0.08 }),
              let i=slots.firstIndex(where:{$0.connection==connection}),i<capacity else { return false }
        return slots[i].ready
    }
    mutating func suspend() {
        for i in slots.indices { slots[i].ready = false; slots[i].sample = .neutral }
    }
    mutating func clear(_ slot: Int) { guard slots.indices.contains(slot) else { return }; slots[slot] = SlotState() }
    func packet(token: [UInt8], sequence: UInt64, captureTime: UInt64, enabled: Bool) -> Data {
        precondition(token.count == 16)
        precondition(captureTime != 0)
        var d = Data()
        func u32(_ x: UInt32) { var n=x.littleEndian; withUnsafeBytes(of:&n){d.append(contentsOf:$0)} }
        func u64(_ x: UInt64) { var n=x.littleEndian; withUnsafeBytes(of:&n){d.append(contentsOf:$0)} }
        u64(Self.packetMagicV2); d.append(contentsOf:token); u64(sequence); u64(captureTime)
        for (i,s) in slots.enumerated() {
            let live = enabled && i < capacity && s.connection != nil && s.ready
            u32(live ? 1 : 0); u32(live ? s.sample.buttons : 0)
            let axes = live ? s.sample.axes : PadSample.neutral.axes
            if live { precondition(axes.count == 6, "Every live slot must contain exactly six axes") }
            for axis in 0..<6 { u32(axes[axis].bitPattern) }
        }
        precondition(d.count == Self.packetBytes)
        return d
    }
}
