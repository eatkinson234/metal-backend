import Foundation
import Darwin

// Passive input timing for the session log (the file the game's own output goes to). While a game runs,
// once a minute: one line per controller (how often its reports arrive, how old a report is when the
// 4 ms poll reads it, and how long a trigger pull takes to reach the shield latch and the old 0.5
// threshold) and one line for the poll itself (how late the timer fires, missed ticks, and failed sends
// by errno). Everything is counted into fixed buckets; nothing is written per tick.

/// Millisecond histogram with fixed buckets. A percentile is the nearest-rank sample, placed inside its
/// bucket between the smallest and largest value seen there: identical or evenly spread values read
/// back exactly, anything else is off by less than the bucket's width.
struct TimingHistogram: Equatable {
    /// Inclusive upper bounds (ms); one more bucket holds everything above the last bound.
    static let bounds: [Double] = [0.25, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 3.5, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13,
                                   14, 15, 16, 18, 20, 24, 28, 32, 40, 50, 64, 80, 100, 128, 160, 200, 250, 500, 1000]

    private(set) var counts = Array(repeating: 0, count: TimingHistogram.bounds.count + 1)
    private var lows = Array(repeating: 0.0, count: TimingHistogram.bounds.count + 1)
    private var highs = Array(repeating: 0.0, count: TimingHistogram.bounds.count + 1)
    private(set) var count = 0
    private(set) var minimum = 0.0
    private(set) var maximum = 0.0

    mutating func record(_ milliseconds: Double) {
        guard milliseconds.isFinite else { return }
        let value = max(milliseconds, 0)
        var bucket = 0, high = Self.bounds.count  // first bound at or above the value
        while bucket < high {
            let middle = (bucket + high) / 2
            if Self.bounds[middle] < value { bucket = middle + 1 } else { high = middle }
        }
        lows[bucket] = counts[bucket] == 0 ? value : min(lows[bucket], value)
        highs[bucket] = counts[bucket] == 0 ? value : max(highs[bucket], value)
        counts[bucket] += 1
        minimum = count == 0 ? value : min(minimum, value)
        maximum = count == 0 ? value : max(maximum, value)
        count += 1
    }

    /// The value that `fraction` (0...1) of the samples are at or below, or nil without samples.
    func percentile(_ fraction: Double) -> Double? {
        guard count > 0 else { return nil }
        let rank = min(count, max(1, Int((min(max(fraction, 0), 1) * Double(count)).rounded(.up))))
        var below = 0
        for (bucket, n) in counts.enumerated() where n > 0 {
            if below + n >= rank {
                guard n > 1 else { return lows[bucket] }
                return lows[bucket] + (highs[bucket] - lows[bucket]) * Double(rank - below - 1) / Double(n - 1)
            }
            below += n
        }
        return maximum
    }

    /// Samples above `lower` and at or below `upper` ms. Both must be bucket bounds (or 0 and .infinity).
    func count(above lower: Double, atOrBelow upper: Double) -> Int {
        var total = 0
        for (index, n) in counts.enumerated() {
            let bucketUpper = index < Self.bounds.count ? Self.bounds[index] : .infinity
            if bucketUpper > lower && bucketUpper <= upper { total += n }
        }
        return total
    }

    /// "p50:8.90,p95:15.20,max:31.00", or "none" without samples.
    var summary: String {
        guard let p50 = percentile(0.5), let p95 = percentile(0.95) else { return "none" }
        return "p50:\(InputTiming.ms(p50)),p95:\(InputTiming.ms(p95)),max:\(InputTiming.ms(maximum))"
    }
}

/// How late the strict poll timer fires against its own grid (first deadline + k × period). A tick that
/// comes after grid points were skipped (the main thread was busy) reports how many it swallowed.
struct PollLateness {
    private(set) var period: UInt64 = 0
    private var due: UInt64?

    /// The timer was (re)scheduled with its first deadline at `start` (uptime nanoseconds).
    mutating func start(at start: UInt64, period: UInt64) {
        self.period = period
        due = period > 0 ? start : nil
    }

    /// Lateness (ns) of the oldest grid point this tick serves, and how many later grid points it swallowed.
    mutating func tick(at now: UInt64) -> (late: UInt64, missed: Int)? {
        guard let due else { return nil }
        guard now >= due else {
            self.due = due + period  // fired inside the timer's own slack, before our estimate
            return (0, 0)
        }
        let late = now - due
        let missed = Int(late / period)
        self.due = due + UInt64(missed + 1) * period
        return (late, missed)
    }
}

/// couch_send results by errno, kept apart before the game's receiver first takes a packet (ENOENT or
/// ECONNREFUSED while the game boots are expected) and after (ENOBUFS there means the game fell behind).
struct SendOutcomes: Equatable {
    private(set) var sends = 0
    private(set) var delivered = false
    private(set) var beforeFirstDelivery: [Int32: Int] = [:]
    private(set) var afterFirstDelivery: [Int32: Int] = [:]

    mutating func record(_ result: Int32) {
        sends += 1
        if result >= 0 {
            delivered = true
        } else if delivered {
            afterFirstDelivery[0 &- result, default: 0] += 1
        } else {
            beforeFirstDelivery[0 &- result, default: 0] += 1
        }
    }

    /// Starts a new window; whether anything was ever delivered is kept.
    mutating func resetWindow() {
        sends = 0
        beforeFirstDelivery = [:]
        afterFirstDelivery = [:]
    }
}

/// Times one trigger pull from its first report above 0.05 (from rest) to the shield latch pressing and
/// to the old 0.5 threshold. A pull already under way when timing starts, or a re-press that never went
/// back to rest, is not timed.
struct TriggerPullTimer: Equatable {
    static let startAbove: Float = 0.05

    private var rested = false
    private var startedAt: Double?
    private var wasPressed = false
    private var latchTimed = false
    private var halfTimed = false

    /// One poll: raw travel, the latch's state and the report's time (s). Returns finished times in ms.
    mutating func step(raw: Float, pressed: Bool, at time: Double) -> (toLatch: Double?, toHalf: Double?) {
        defer { wasPressed = pressed }
        guard raw > Self.startAbove else {
            rested = true
            startedAt = nil
            latchTimed = false
            halfTimed = false
            return (nil, nil)
        }
        if rested {
            startedAt = time
            rested = false
        }
        guard let start = startedAt else { return (nil, nil) }
        var result: (toLatch: Double?, toHalf: Double?) = (nil, nil)
        if pressed && !wasPressed && !latchTimed {
            result.toLatch = (time - start) * 1000
            latchTimed = true
        }
        if raw > TriggerLatch.releaseDropCeiling && !halfTimed {
            result.toHalf = (time - start) * 1000
            halfTimed = true
        }
        return result
    }
}

/// One controller's timing for the current window.
struct ControllerTiming {
    /// A longer gap between reports is the controller sitting idle, not its report rate.
    static let idleGapMs = 100.0

    let name: String
    private(set) var polls = 0
    private(set) var reports = 0
    private(set) var longGaps = 0
    private(set) var pulls = 0
    private(set) var reportInterval = TimingHistogram()
    private(set) var eventAge = TimingHistogram()
    private(set) var pullToLatch = TimingHistogram()
    private(set) var pullToHalf = TimingHistogram()
    private var lastEvent: Double?
    private var left = TriggerPullTimer()
    private var right = TriggerPullTimer()

    init(name: String) { self.name = name }

    /// One poll: the captured state's lastEventTimestamp and lastEventLatency (s), the raw sample and the
    /// sample with its triggers latched. A report is counted when the event time changes.
    mutating func record(eventTimestamp: Double, eventLatency: Double, raw: PadSample, latched: PadSample) {
        polls += 1
        guard eventTimestamp.isFinite, eventTimestamp > 0 else { return }
        if eventTimestamp != lastEvent {
            reports += 1
            if let last = lastEvent {
                let gap = (eventTimestamp - last) * 1000
                if gap > 0 && gap <= Self.idleGapMs { reportInterval.record(gap) } else { longGaps += 1 }
            }
            if eventLatency.isFinite && eventLatency >= 0 { eventAge.record(eventLatency * 1000) }
            lastEvent = eventTimestamp
        }
        guard raw.axes.count == 6, latched.axes.count == 6 else { return }
        note(left.step(raw: raw.axes[4], pressed: latched.axes[4] >= 0.5, at: eventTimestamp))
        note(right.step(raw: raw.axes[5], pressed: latched.axes[5] >= 0.5, at: eventTimestamp))
    }

    private mutating func note(_ pull: (toLatch: Double?, toHalf: Double?)) {
        if let ms = pull.toLatch {
            pulls += 1
            pullToLatch.record(ms)
        }
        if let ms = pull.toHalf { pullToHalf.record(ms) }
    }

    /// Starts a new window; the last report time and any pull in progress carry over.
    mutating func resetWindow() {
        polls = 0
        reports = 0
        longGaps = 0
        pulls = 0
        reportInterval = TimingHistogram()
        eventAge = TimingHistogram()
        pullToLatch = TimingHistogram()
        pullToHalf = TimingHistogram()
    }
}

/// Everything timed while one game runs, flushed to the session log once a minute and when it exits.
struct InputTimingSession {
    static let window: UInt64 = 60_000_000_000

    /// Uptime nanoseconds when the game started; line stamps count from here, like the game's own.
    let startedAt: UInt64
    private(set) var windowStart: UInt64
    private(set) var ticks = 0
    private(set) var missedTicks = 0
    private(set) var lateness = TimingHistogram()
    private(set) var sends = SendOutcomes()
    private(set) var controllers: [UUID: ControllerTiming] = [:]
    /// The first tick after the game starts is late by the launch work on the main thread (signature
    /// check, config preparation), not by the poll, so it is not counted.
    private var launchTickSkipped = false

    init(startedAt: UInt64) {
        self.startedAt = startedAt
        windowStart = startedAt
    }

    mutating func recordTick(late: UInt64, missed: Int) {
        guard launchTickSkipped else {
            launchTickSkipped = true
            return
        }
        ticks += 1
        missedTicks += missed
        lateness.record(Double(late) / 1_000_000)
    }

    mutating func recordSend(_ result: Int32) { sends.record(result) }

    mutating func record(_ connection: UUID, name: @autoclosure () -> String, eventTimestamp: Double,
                         eventLatency: Double, raw: PadSample, latched: PadSample) {
        if controllers[connection] == nil { controllers[connection] = ControllerTiming(name: name()) }
        controllers[connection]?.record(eventTimestamp: eventTimestamp, eventLatency: eventLatency,
                                        raw: raw, latched: latched)
    }

    func isDue(at now: UInt64) -> Bool { now >= windowStart &+ Self.window }

    /// This window's lines (controllers by player spot, then the poll line), then a fresh window.
    /// Controllers that are no longer connected are reported one last time and dropped.
    mutating func flush(at now: UInt64, players: [UUID: Int], connected: Set<UUID>, reason: String) -> [String] {
        let stamp = InputTiming.elapsed(now >= startedAt ? now - startedAt : 0)
        let seconds = String(format: "%.1f", Double(now >= windowStart ? now - windowStart : 0) / 1e9)
        var lines: [String] = []
        let order = controllers.keys.sorted {
            (players[$0] ?? SlotBook.slotCount, controllers[$0]?.name ?? "", $0.uuidString) <
                (players[$1] ?? SlotBook.slotCount, controllers[$1]?.name ?? "", $1.uuidString)
        }
        for id in order {
            guard let pad = controllers[id], pad.polls > 0 else { continue }
            let player = players[id].map { "P\($0 + 1)" } ?? (connected.contains(id) ? "none" : "disconnected")
            lines.append("\(stamp) |I| Launcher InputTiming: player=\(player);pad=\(InputTiming.field(pad.name));" +
                "seconds=\(seconds);polls=\(pad.polls);reports=\(pad.reports);reportMs=\(pad.reportInterval.summary);" +
                "longGaps=\(pad.longGaps);eventAgeMs=\(pad.eventAge.summary);triggerPulls=\(pad.pulls);" +
                "pullToLatchMs=\(pad.pullToLatch.summary);pullToHalfMs=\(pad.pullToHalf.summary);reason=\(reason)")
        }
        let histogram = InputTiming.lateRanges.map { "\($0.label):\(lateness.count(above: $0.lower, atOrBelow: $0.upper))" }
        lines.append("\(stamp) |I| Launcher PollTiming: seconds=\(seconds);ticks=\(ticks);missedTicks=\(missedTicks);" +
            "lateMs=\(lateness.summary);lateHistMs=\(histogram.joined(separator: ","));sends=\(sends.sends);" +
            "sendErrors=\(InputTiming.errors(sends.afterFirstDelivery));" +
            "sendErrorsBeforeFirstDelivery=\(InputTiming.errors(sends.beforeFirstDelivery));reason=\(reason)")

        windowStart = now
        ticks = 0
        missedTicks = 0
        lateness = TimingHistogram()
        sends.resetWindow()
        for id in Array(controllers.keys) {
            if connected.contains(id) { controllers[id]?.resetWindow() } else { controllers.removeValue(forKey: id) }
        }
        return lines
    }
}

enum InputTiming {
    /// Poll lateness ranges (ms) for the log; each is (lower, upper].
    static let lateRanges: [(lower: Double, upper: Double, label: String)] = [
        (0, 0.25, "0-0.25"), (0.25, 0.5, "0.25-0.5"), (0.5, 1, "0.5-1"), (1, 2, "1-2"), (2, 4, "2-4"),
        (4, 8, "4-8"), (8, 16, "8-16"), (16, 32, "16-32"), (32, .infinity, "32+")]

    static func ms(_ value: Double) -> String { String(format: "%.2f", value) }

    /// Elapsed time in the game log's own "HH:MM:SS.mmm" form.
    static func elapsed(_ nanoseconds: UInt64) -> String {
        let millis = nanoseconds / 1_000_000
        return String(format: "%02llu:%02llu:%02llu.%03llu", millis / 3_600_000, millis / 60_000 % 60,
                      millis / 1000 % 60, millis % 1000)
    }

    /// A controller name as one key=value field.
    static func field(_ text: String) -> String {
        String(text.map { $0 == ";" || $0.isNewline ? "_" : $0 })
    }

    static func errors(_ counts: [Int32: Int]) -> String {
        counts.isEmpty ? "none" : counts.sorted { $0.key < $1.key }.map { "\(errnoName($0.key)):\($0.value)" }
            .joined(separator: ",")
    }

    static func errnoName(_ code: Int32) -> String {
        switch code {
        case ENOENT: return "ENOENT"
        case ECONNREFUSED: return "ECONNREFUSED"
        case ENOBUFS: return "ENOBUFS"
        case EAGAIN: return "EAGAIN"
        case EMSGSIZE: return "EMSGSIZE"
        case ENAMETOOLONG: return "ENAMETOOLONG"
        case EBADF: return "EBADF"
        case EINVAL: return "EINVAL"
        case EPERM: return "EPERM"
        case EACCES: return "EACCES"
        case ENOTSOCK: return "ENOTSOCK"
        default: return "errno\(code)"
        }
    }

    /// The game writes its own output through the same open file as the launcher's session log, so the
    /// file is made append-only: the launcher's lines then always land at the end, never over the game's.
    @discardableResult static func makeAppendOnly(_ fd: Int32) -> Bool {
        let flags = fcntl(fd, F_GETFL)
        return flags >= 0 && fcntl(fd, F_SETFL, flags | O_APPEND) == 0
    }

    /// Appends the lines in one write. Telemetry never fails the session, so the result is only reported.
    @discardableResult static func append(_ lines: [String], to fd: Int32) -> Bool {
        guard fd >= 0, !lines.isEmpty else { return false }
        let bytes = Array(lines.map { $0 + "\n" }.joined().utf8)
        return bytes.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) == $0.count }
    }
}
