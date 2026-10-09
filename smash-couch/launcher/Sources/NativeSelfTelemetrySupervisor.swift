import Foundation
import Darwin
import Security

struct NativeSelfMemorySample: Equatable {
    let schemaVersion: Int
    let nonce: String
    let pid: Int32
    let sequence: UInt64
    let physicalFootprintBytes: UInt64

    private static let frame = try! NSRegularExpression(pattern:
        #"\A\s*\{\s*"schema_version"\s*:\s*(0|[1-9][0-9]*)\s*,\s*"nonce"\s*:\s*"([0-9a-fA-F]{32})"\s*,\s*"pid"\s*:\s*(0|[1-9][0-9]*)\s*,\s*"sequence"\s*:\s*(0|[1-9][0-9]*)\s*,\s*"physical_footprint_bytes"\s*:\s*(0|[1-9][0-9]*)\s*\}\s*\z"#)

    static func parse(_ data: Data, expectedPID: Int32, expectedNonce: String) throws -> NativeSelfMemorySample {
        guard data.count <= 4096, expectedPID > 0,
              expectedNonce.count == 32,
              expectedNonce.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }),
              let text = String(data: data, encoding: .utf8) else {
            throw NativeSelfTelemetryError.malformed
        }
        let whole = NSRange(text.startIndex..<text.endIndex, in: text)
        guard let match = frame.firstMatch(in: text, range: whole), match.range == whole,
              let schema = Int(capture(1, from: match, in: text) ?? ""),
              let nonce = capture(2, from: match, in: text),
              let pid = Int32(capture(3, from: match, in: text) ?? ""),
              let sequence = UInt64(capture(4, from: match, in: text) ?? ""),
              let footprint = UInt64(capture(5, from: match, in: text) ?? "") else {
            throw NativeSelfTelemetryError.malformed
        }
        guard schema == 1, nonce == expectedNonce, pid == expectedPID,
              sequence > 0 else {
            throw NativeSelfTelemetryError.identityOrSchemaMismatch
        }
        return NativeSelfMemorySample(schemaVersion: schema, nonce: nonce, pid: pid,
                                      sequence: sequence, physicalFootprintBytes: footprint)
    }

    private static func capture(_ index: Int, from match: NSTextCheckingResult,
                                in text: String) -> String? {
        let range = match.range(at: index)
        guard let swiftRange = Range(range, in: text) else { return nil }
        return String(text[swiftRange])
    }
}

enum NativeSelfTelemetryError: LocalizedError {
    case malformed
    case identityOrSchemaMismatch
    case unsafePath
    case fileUnavailable
    case sequenceRegressedOrRewritten

    var errorDescription: String? {
        switch self {
        case .malformed: return "Self-memory telemetry frame is malformed or exceeds 4096 bytes"
        case .identityOrSchemaMismatch: return "Self-memory telemetry schema, nonce, or child PID mismatched"
        case .unsafePath: return "Self-memory telemetry path is unsafe or already exists"
        case .fileUnavailable: return "Self-memory telemetry file could not be safely read"
        case .sequenceRegressedOrRewritten: return "Self-memory telemetry sequence regressed or was rewritten"
        }
    }
}

enum NativeSelfTelemetryFile {
    static func targetURL(sessionID: String, under privateData: URL) throws -> URL {
        guard let uuid = UUID(uuidString: sessionID), uuid.uuidString.lowercased() == sessionID,
              privateData.path == privateData.standardizedFileURL.path else {
            throw NativeSelfTelemetryError.unsafePath
        }
        return privateData.appendingPathComponent("native-self-memory-\(sessionID).json")
    }

    static func isNormalizedChild(_ target: URL, under privateData: URL) -> Bool {
        let rootPath = privateData.standardizedFileURL.path
        let targetPath = target.standardizedFileURL.path
        let prefix = rootPath == "/" ? "/" : rootPath + "/"
        return privateData.path == rootPath && target.path == targetPath &&
            targetPath.hasPrefix(prefix) && targetPath != rootPath
    }

    private static func validateParentChain(_ target: URL, under privateData: URL) throws {
        guard isNormalizedChild(target, under: privateData) else { throw NativeSelfTelemetryError.unsafePath }

        var parent = target.deletingLastPathComponent()
        while true {
            var info = stat()
            let result = parent.path.withCString { Darwin.lstat($0, &info) }
            guard result == 0, (info.st_mode & S_IFMT) == S_IFDIR else {
                throw NativeSelfTelemetryError.unsafePath
            }
            if parent.path == "/" { break }
            parent = parent.deletingLastPathComponent()
        }
    }

    static func validateAbsentTarget(_ target: URL, under privateData: URL) throws {
        try validateParentChain(target, under: privateData)

        var targetInfo = stat()
        let targetResult = target.path.withCString { Darwin.lstat($0, &targetInfo) }
        guard targetResult != 0, errno == ENOENT else {
            throw NativeSelfTelemetryError.unsafePath
        }
    }

    static func read(_ url: URL, under privateData: URL, expectedPID: Int32,
                     expectedNonce: String) throws -> NativeSelfMemorySample? {
        try validateParentChain(url, under: privateData)
        var targetInfo = stat()
        let targetResult = url.path.withCString { Darwin.lstat($0, &targetInfo) }
        if targetResult != 0 {
            if errno == ENOENT { return nil }
            throw NativeSelfTelemetryError.fileUnavailable
        }
        guard (targetInfo.st_mode & S_IFMT) == S_IFREG,
              targetInfo.st_size >= 0, targetInfo.st_size <= 4096 else {
            throw NativeSelfTelemetryError.malformed
        }
        let fd = url.path.withCString { Darwin.open($0, O_RDONLY | O_NOFOLLOW | O_NONBLOCK) }
        guard fd >= 0 else {
            if errno == ENOENT { return nil }
            throw NativeSelfTelemetryError.fileUnavailable
        }
        defer { Darwin.close(fd) }

        var info = stat()
        guard Darwin.fstat(fd, &info) == 0,
              (info.st_mode & S_IFMT) == S_IFREG,
              info.st_size >= 0, info.st_size <= 4096 else {
            throw NativeSelfTelemetryError.malformed
        }

        var data = Data()
        while true {
            var buffer = [UInt8](repeating: 0, count: 512)
            let count = buffer.withUnsafeMutableBytes { raw in
                Darwin.read(fd, raw.baseAddress, raw.count)
            }
            if count == 0 { break }
            if count < 0 {
                if errno == EINTR { continue }
                throw NativeSelfTelemetryError.fileUnavailable
            }
            guard data.count + count <= 4096 else { throw NativeSelfTelemetryError.malformed }
            data.append(contentsOf: buffer.prefix(count))
        }
        return try NativeSelfMemorySample.parse(data, expectedPID: expectedPID,
                                                expectedNonce: expectedNonce)
    }
}

enum NativeSelfTelemetryStopReason: String, Equatable {
    case telemetryMissing
    case telemetryStale
    case telemetryInvalid
    case sampledThreshold
    case launcherFailure
}

struct NativeSelfTelemetryPolicy {
    static let firstSampleTimeoutNanoseconds: UInt64 = 5 * 1_000_000_000
    static let staleAfterNanoseconds: UInt64 = 3 * 1_000_000_000
    static let sampledFootprintThreshold: UInt64 = 8 * 1024 * 1024 * 1024

    let startedAt: UInt64
    private(set) var lastAcceptedAt: UInt64?
    private(set) var lastSequence: UInt64 = 0
    private(set) var lastFootprint: UInt64?
    private(set) var sampleCount = 0
    private(set) var peakFootprint: UInt64 = 0

    init(startedAt: UInt64) { self.startedAt = startedAt }

    func timeout(at now: UInt64) -> NativeSelfTelemetryStopReason? {
        guard now >= startedAt else { return .telemetryInvalid }
        if let lastAcceptedAt {
            if now < lastAcceptedAt { return .telemetryInvalid }
            if now - lastAcceptedAt >= Self.staleAfterNanoseconds { return .telemetryStale }
        } else if now - startedAt >= Self.firstSampleTimeoutNanoseconds {
            return .telemetryMissing
        }
        return nil
    }

    mutating func accept(_ sample: NativeSelfMemorySample, at now: UInt64,
                         expectedPID: Int32) -> NativeSelfTelemetryStopReason? {
        guard now >= startedAt, sample.pid == expectedPID, sample.sequence > 0 else {
            return .telemetryInvalid
        }
        if sample.sequence < lastSequence ||
            (sample.sequence == lastSequence && lastFootprint != sample.physicalFootprintBytes) {
            return .telemetryInvalid
        }
        if sample.sequence > lastSequence {
            lastSequence = sample.sequence
            lastFootprint = sample.physicalFootprintBytes
            lastAcceptedAt = now
            sampleCount += 1
            peakFootprint = max(peakFootprint, sample.physicalFootprintBytes)
        }
        return sample.physicalFootprintBytes >= Self.sampledFootprintThreshold ? .sampledThreshold : nil
    }
}

enum ExactOwnedChildAction: Equatable {
    case wait
    case childExited
    case identityMismatch
    case sendTerminate
    case sendKill
    case survivorHold
}

struct ExactOwnedChildTerminationPolicy {
    static let termGraceNanoseconds: UInt64 = 1 * 1_000_000_000
    static let killGraceNanoseconds: UInt64 = 1 * 1_000_000_000

    let expectedPID: Int32
    private(set) var termSentAt: UInt64?
    private(set) var killSentAt: UInt64?
    private(set) var survivorWasRecorded = false

    init(expectedPID: Int32) { self.expectedPID = expectedPID }

    mutating func nextAction(at now: UInt64, childIsRunning: Bool,
                             observedPID: Int32) -> ExactOwnedChildAction {
        guard childIsRunning else { return .childExited }
        guard expectedPID > 0, observedPID == expectedPID else { return .identityMismatch }
        if termSentAt == nil {
            termSentAt = now
            return .sendTerminate
        }
        if let termSentAt, killSentAt == nil {
            guard now >= termSentAt, now - termSentAt >= Self.termGraceNanoseconds else { return .wait }
            killSentAt = now
            return .sendKill
        }
        if let killSentAt, !survivorWasRecorded,
           now >= killSentAt, now - killSentAt >= Self.killGraceNanoseconds {
            survivorWasRecorded = true
            return .survivorHold
        }
        return .wait
    }
}

struct NativeSelfTelemetrySummary: Equatable {
    var status: String
    let childPID: Int32
    var sampleCount = 0
    var lastSequence: UInt64 = 0
    var lastFootprintBytes: UInt64?
    var peakFootprintBytes: UInt64 = 0
    var stopReason: String?
    var survivorObserved = false
}

final class NativeSelfTelemetrySupervisor {
    private let queue = DispatchQueue(label: "local.smash.couch.self-telemetry-supervisor", qos: .utility)
    private var process: Process?
    private var telemetryURL: URL?
    private var privateDataURL: URL?
    private var nonce: String?
    private var policy: NativeSelfTelemetryPolicy?
    private var terminationPolicy: ExactOwnedChildTerminationPolicy?
    private var timer: DispatchSourceTimer?
    private var onIncident: ((NativeSelfTelemetrySummary) -> Void)?
    private var summary: NativeSelfTelemetrySummary?
    private var stopReason: NativeSelfTelemetryStopReason?
    private var incidentSent = false
    private var identityMismatchReported = false

    static func freshNonce() throws -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else {
            throw LobbyError("Could not create a private self-telemetry nonce")
        }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    func start(process: Process, telemetryURL: URL, privateData: URL, nonce: String,
               onIncident: @escaping (NativeSelfTelemetrySummary) -> Void) -> Bool {
        guard process.isRunning, process.processIdentifier > 0,
              NativeSelfTelemetryFile.isNormalizedChild(telemetryURL, under: privateData),
              nonce.count == 32,
              nonce.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else {
            return false
        }
        return queue.sync {
            guard self.process == nil else { return false }
            let pid = process.processIdentifier
            let started = DispatchTime.now().uptimeNanoseconds
            self.process = process
            self.telemetryURL = telemetryURL
            self.privateDataURL = privateData
            self.nonce = nonce
            self.policy = NativeSelfTelemetryPolicy(startedAt: started)
            self.terminationPolicy = ExactOwnedChildTerminationPolicy(expectedPID: pid)
            self.summary = NativeSelfTelemetrySummary(status: "monitoring", childPID: pid)
            self.onIncident = onIncident
            self.installTimerLocked()
            return true
        }
    }

    // Adopt a retained direct child when normal telemetry setup failed after
    // Process.run. No telemetry path, nonce, or sample policy is required;
    // this mode can only stop and observe this exact Process/PID.
    func startStopping(process: Process,
                       reason: NativeSelfTelemetryStopReason = .launcherFailure,
                       onIncident: @escaping (NativeSelfTelemetrySummary) -> Void) -> Bool {
        guard process.isRunning, process.processIdentifier > 0 else { return false }
        return queue.sync {
            guard self.process == nil else { return false }
            let pid = process.processIdentifier
            self.process = process
            self.terminationPolicy = ExactOwnedChildTerminationPolicy(expectedPID: pid)
            self.summary = NativeSelfTelemetrySummary(status: "monitoring", childPID: pid)
            self.onIncident = onIncident
            self.stopReason = nil
            self.incidentSent = false
            self.identityMismatchReported = false
            self.installTimerLocked()
            self.beginStop(reason, at: DispatchTime.now().uptimeNanoseconds)
            return true
        }
    }

    // Called only while holding the supervisor's serial queue.
    private func installTimerLocked() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: .milliseconds(250), leeway: .milliseconds(50))
        timer.setEventHandler { [weak self] in self?.poll() }
        self.timer = timer
        timer.resume()
    }

    func requestStop(reason: NativeSelfTelemetryStopReason = .launcherFailure) {
        queue.async {
            guard self.process?.isRunning == true else { return }
            self.beginStop(reason, at: DispatchTime.now().uptimeNanoseconds)
        }
    }

    func childExited(pid: Int32) -> NativeSelfTelemetrySummary? {
        queue.sync {
            guard var current = summary, current.childPID == pid else { return nil }
            current.status = current.stopReason == nil ? "child_exited" : "child_exited_after_stop"
            summary = current
            timer?.setEventHandler {}
            timer?.cancel()
            timer = nil
            process = nil
            telemetryURL = nil
            privateDataURL = nil
            nonce = nil
            return current
        }
    }

    func currentSummary() -> NativeSelfTelemetrySummary? {
        queue.sync { summary }
    }

    private func poll() {
        guard let child = process, var current = summary else { return }
        if !child.isRunning {
            current.status = stopReason == nil ? "child_exited" : "child_exited_after_stop"
            summary = current
            timer?.setEventHandler {}
            timer?.cancel()
            timer = nil
            return
        }

        let now = DispatchTime.now().uptimeNanoseconds
        if stopReason != nil {
            advanceTermination(child, at: now)
            return
        }
        guard var policy, let telemetryURL, let privateDataURL, let nonce else {
            beginStop(.telemetryInvalid, at: now)
            return
        }
        if let reason = policy.timeout(at: now) {
            self.policy = policy
            beginStop(reason, at: now)
            return
        }

        do {
            if let sample = try NativeSelfTelemetryFile.read(telemetryURL, under: privateDataURL,
                expectedPID: child.processIdentifier, expectedNonce: nonce) {
                if let reason = policy.accept(sample, at: now, expectedPID: child.processIdentifier) {
                    self.policy = policy
                    current.sampleCount = policy.sampleCount
                    current.lastSequence = policy.lastSequence
                    current.lastFootprintBytes = policy.lastFootprint
                    current.peakFootprintBytes = policy.peakFootprint
                    summary = current
                    beginStop(reason, at: now)
                    return
                }
                current.sampleCount = policy.sampleCount
                current.lastSequence = policy.lastSequence
                current.lastFootprintBytes = policy.lastFootprint
                current.peakFootprintBytes = policy.peakFootprint
                self.policy = policy
                summary = current
            }
        } catch {
            self.policy = policy
            beginStop(.telemetryInvalid, at: now)
            return
        }

        if let reason = policy.timeout(at: now) {
            self.policy = policy
            beginStop(reason, at: now)
        }
    }

    private func beginStop(_ reason: NativeSelfTelemetryStopReason, at now: UInt64) {
        guard stopReason == nil else { advanceTermination(process, at: now); return }
        stopReason = reason
        if var current = summary {
            current.status = "stopping"
            current.stopReason = reason.rawValue
            summary = current
            if !incidentSent {
                incidentSent = true
                onIncident?(current)
            }
        }
        advanceTermination(process, at: now)
    }

    private func advanceTermination(_ child: Process?, at now: UInt64) {
        guard let child, var term = terminationPolicy, var current = summary else { return }
        let action = term.nextAction(at: now, childIsRunning: child.isRunning,
                                     observedPID: child.processIdentifier)
        terminationPolicy = term
        switch action {
        case .wait, .childExited:
            if action == .childExited { current.status = stopReason == nil ? "child_exited" : "child_exited_after_stop" }
        case .identityMismatch:
            current.status = "child_identity_mismatch_no_signal"
            if !identityMismatchReported {
                identityMismatchReported = true
                if current.stopReason == nil { current.stopReason = "child_identity_mismatch" }
                onIncident?(current)
            }
        case .sendTerminate:
            child.terminate()
        case .sendKill:
            guard child.isRunning, child.processIdentifier == term.expectedPID else {
                current.status = "child_identity_mismatch_no_signal"
                summary = current
                if !identityMismatchReported {
                    identityMismatchReported = true
                    onIncident?(current)
                }
                return
            }
            if Darwin.kill(term.expectedPID, SIGKILL) != 0 {
                current.status = "force_signal_failed"
            }
        case .survivorHold:
            current.status = "survivor_hold"
            current.survivorObserved = true
            onIncident?(current)
        }
        summary = current
    }
}
