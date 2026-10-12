import Foundation

// How the launcher reads controllers, chosen once at launch from UserDefaults (key "inputSampling"):
//   poll (default)    The 4 ms poll on the main thread, exactly as before events existed: capture() in the
//                     background is what every real session so far has used.
//   events (opt-in)   Each controller's GameController input states arrive on one serial user-interactive
//                     queue, and a packet goes out as soon as a state changes what that controller sends.
//                     Input no longer waits up to 4 ms for the next poll, or for the main thread when it is
//                     busy with the lobby. A heartbeat on the same queue (every 4 ms while a game runs or the
//                     lobby is in front, else 100 ms; skipped right after a packet) keeps the game's 150 ms
//                     freshness rule and carries the lobby's own changes: joins, the start hand-over, stops,
//                     the game starting and ending. Opt-in until a run with real controllers shows that
//                     GameController hands buffered states to the launcher promptly while the game is in
//                     front (the launcher is then a background app).
//   defaults write local.smash.couch.couch55-r1 inputSampling events  (try events; restart the launcher)
//   defaults delete local.smash.couch.couch55-r1 inputSampling        (back to the poll)
// Both modes run the same spots, trigger latches, hand-over and packet code (InputPipeState); only who
// calls it, and when, differs. The packet format is the same SCINPUT2.

enum InputSampling: String {
    case events, poll

    static let defaultsKey = "inputSampling"
    static let `default` = InputSampling.poll

    /// "poll" or "events" (any case, surrounding spaces ignored); anything else, or nothing, is the default.
    static func from(_ value: String?) -> InputSampling {
        InputSampling(rawValue: value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() ?? "") ?? .default
    }

    /// Heartbeat period, and how soon after any packet a heartbeat is skipped: packets are never more
    /// than 6 ms apart (plus timer slack) while a game runs.
    static let heartbeatPeriod: UInt64 = 4_000_000
    static let heartbeatSkip: UInt64 = 2_000_000
    /// The heartbeat follows the lobby poll's pace (LobbyFocus.fastPoll): every 4 ms while a game runs or the
    /// lobby is in front, else every 100 ms. Nothing is sent then (no game), so it only drains and watches.
    static let idleHeartbeatPeriod: UInt64 = 100_000_000
    static func heartbeatPeriod(fast: Bool) -> UInt64 { fast ? heartbeatPeriod : idleHeartbeatPeriod }
    /// Buffered states drained per controller per call, so a misbehaving queue can never spin forever.
    static let drainLimit = 64

    /// The session log's first launcher line in events mode (the poll mode adds none).
    static func sessionLine() -> String {
        "00:00:00.000 |I| Launcher InputSampling: mode=events;heartbeatMs=\(heartbeatPeriod / 1_000_000);" +
            "heartbeatSkipMs=\(heartbeatSkip / 1_000_000);queue=userInteractive"
    }
}

/// Where packets go while a game runs: the session token and one datagram send (couch_send's result).
struct InputTransport {
    let token: [UInt8]
    let send: (Data) -> Int32
}

/// Events mode counters for one timing window (one session-log line a minute).
struct EventInputCounts: Equatable {
    var states = 0
    var eventSends = 0
    var heartbeatSends = 0
    var heartbeatsSkipped = 0
    /// Controllers currently read by capture() on the heartbeat because their buffered states stopped
    /// coming (BufferedInputWatch); not reset per window.
    var polledPads = 0

    mutating func resetWindow() {
        states = 0
        eventSends = 0
        heartbeatSends = 0
        heartbeatsSkipped = 0
    }
}

/// Everything the controller input path shares between the lobby (main thread) and the events-mode input
/// queue: player spots, trigger latches, timing, the packet sequence and transport, and in events mode each
/// controller's newest sample. Only ever used inside InputPipe's lock, so neither side sees it half-changed.
struct InputPipeState {
    var book = SlotBook()
    /// Shield on analog triggers, latched to exactly pressed or released (see TriggerLatch).
    var latches = TriggerLatchBook()
    /// Input timing for the session log while a game runs (see InputTiming.swift).
    var timing: InputTimingSession?
    var transport: InputTransport?
    var sequence: UInt64 = 0

    // Events mode only; the poll mode never fills these.
    /// Each connected controller's newest sample, triggers latched. The only sample the spots ever get.
    private(set) var latest: [UUID: PadSample] = [:]
    /// The same, before latching.
    private(set) var latestRaw: [UUID: PadSample] = [:]
    /// Packets carry live input (the game runs); the lobby mirrors this every tick.
    var live = false
    private(set) var lastSendAt: UInt64?
    /// The heartbeat timer's own grid, to time how late each heartbeat runs.
    var heartbeatGrid = PollLateness()
    var counts = EventInputCounts()

    /// One packet of every spot as it stands, then its timing. This is the poll's send, unchanged.
    @discardableResult mutating func send(captureTime: UInt64, enabled: Bool) -> Int32? {
        guard let transport else { return nil }
        sequence &+= 1
        let packet = book.packet(token: transport.token, sequence: sequence, captureTime: captureTime, enabled: enabled)
        let sent = transport.send(packet)
        timing?.recordSend(sent)
        return sent
    }

    /// A game starts: packets go out from now on, numbered from 1.
    mutating func startTransport(_ transport: InputTransport) {
        self.transport = transport
        sequence = 0
        lastSendAt = nil
        counts.resetWindow()
    }

    /// A controller arrives (events mode): its first sample, latched and timed like every later one.
    @discardableResult mutating func attach(_ id: UUID, name: String, raw: PadSample, eventTimestamp: Double,
                                            eventLatency: Double) -> PadSample {
        let latched = latches.latch(id, raw)
        timing?.record(id, name: name, eventTimestamp: eventTimestamp, eventLatency: eventLatency, raw: raw, latched: latched)
        latest[id] = latched
        latestRaw[id] = raw
        return latched
    }

    /// The controller is gone (events mode): any state still queued for it is ignored.
    mutating func detach(_ id: UUID) {
        latest.removeValue(forKey: id)
        latestRaw.removeValue(forKey: id)
    }

    /// The controller left its spot: its triggers start from rest. Events mode latches its newest state again
    /// at once, as the next poll would latch the state it reads.
    mutating func resetLatches(_ id: UUID) {
        latches.reset(id)
        if let raw = latestRaw[id] { latest[id] = latches.latch(id, raw) }
    }

    /// Every connected controller's newest sample through its spot's join gate and hand-over, as each poll
    /// applies that poll's sample. Applying the same sample again changes nothing, so the lobby and the
    /// input queue can both call this whenever they like.
    mutating func applyLatest() {
        for (id, sample) in latest { book.update(id, sample: sample) }
    }

    /// One input state from a controller (events mode, on the input queue): latch its triggers, time it,
    /// and if that changes what the controller sends, send a packet now. True when a packet went out.
    @discardableResult mutating func receive(_ id: UUID, name: String, raw: PadSample, eventTimestamp: Double,
                                             eventLatency: Double, at now: UInt64, captureTime: UInt64) -> Bool {
        guard let previous = latest[id] else { return false }
        let latched = latches.latch(id, raw)
        timing?.record(id, name: name, eventTimestamp: eventTimestamp, eventLatency: eventLatency, raw: raw, latched: latched)
        latest[id] = latched
        latestRaw[id] = raw
        counts.states += 1
        guard latched != previous else { return false }
        applyLatest()
        guard sendNow(at: now, captureTime: captureTime) else { return false }
        counts.eventSends += 1
        return true
    }

    /// The heartbeat (events mode, every 4 ms on the input queue): a packet unless one went out under 2 ms
    /// ago, so the game's freshness rule holds and the lobby's own changes reach it even when nobody moves.
    @discardableResult mutating func heartbeat(at now: UInt64, captureTime: UInt64) -> Bool {
        if let late = heartbeatGrid.tick(at: now) { timing?.recordTick(late: late.late, missed: late.missed) }
        applyLatest()
        if let last = lastSendAt, now >= last, now - last < InputSampling.heartbeatSkip {
            counts.heartbeatsSkipped += 1
            return false
        }
        guard sendNow(at: now, captureTime: captureTime) else { return false }
        counts.heartbeatSends += 1
        return true
    }

    private mutating func sendNow(at now: UInt64, captureTime: UInt64) -> Bool {
        guard send(captureTime: captureTime, enabled: live) != nil else { return false }
        lastSendAt = now
        return true
    }

    /// Events mode's own line for the timing window that is about to flush, then a fresh window.
    mutating func eventLine(at now: UInt64, reason: String) -> String? {
        guard let timing else { return nil }
        let stamp = InputTiming.elapsed(now >= timing.startedAt ? now - timing.startedAt : 0)
        let seconds = String(format: "%.1f", Double(now >= timing.windowStart ? now - timing.windowStart : 0) / 1e9)
        defer { counts.resetWindow() }
        return "\(stamp) |I| Launcher EventInput: seconds=\(seconds);states=\(counts.states);" +
            "eventSends=\(counts.eventSends);heartbeatSends=\(counts.heartbeatSends);" +
            "heartbeatsSkipped=\(counts.heartbeatsSkipped);polledPads=\(counts.polledPads);reason=\(reason)"
    }
}

/// The input pipe's state behind one lock. Callers keep the closures short and never lock twice: the only
/// work done inside, besides plain state, is one non-blocking datagram send.
final class InputPipe: @unchecked Sendable {
    private let lock = NSLock()
    private var state = InputPipeState()

    func read<T>(_ body: (InputPipeState) throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body(state)
    }

    @discardableResult func modify<T>(_ body: (inout InputPipeState) throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body(&state)
    }
}

/// Events mode safety net, one per controller. GameController should hand every report to the input queue
/// as a buffered state. If a controller's live state (capture()) differs from the last state applied while
/// no buffered state has arrived for `quietBeats` heartbeats in a row (8-12 ms), its buffered states are not
/// coming, and from then on that controller is read by capture() on every heartbeat instead: the 4 ms poll,
/// still off the main thread. A wrong guess only costs that controller its event sends.
struct BufferedInputWatch: Equatable {
    static let quietBeats = 3

    private(set) var polling = false
    private var quietDivergent = 0

    /// One heartbeat, after draining: whether any buffered state arrived since the last heartbeat, and
    /// whether capture() now differs from the last state applied. True once, when the controller switches.
    mutating func beat(bufferedArrived: Bool, captureDiffers: Bool) -> Bool {
        guard !polling else { return false }
        guard !bufferedArrived && captureDiffers else {
            quietDivergent = 0
            return false
        }
        quietDivergent += 1
        guard quietDivergent >= Self.quietBeats else { return false }
        polling = true
        return true
    }
}
