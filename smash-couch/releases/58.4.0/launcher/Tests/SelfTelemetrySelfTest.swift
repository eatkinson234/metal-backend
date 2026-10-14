import Foundation

enum NativeSelfTelemetrySelfTest {
    static let nonce = "0123456789abcdef0123456789abcdef"

    static func run(root: URL) throws {
        try testStrictFrameAndIdentity()
        try testPrivatePathAndBoundedFileRead(root: root)
        try testWatchdogPolicy()
        try testExactOwnedChildLifecycle()
    }

    static func require(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        try LobbySelfTest.require(condition(), message)
    }

    static func frame(pid: Int32 = 4242, sequence: UInt64 = 1,
                      footprint: UInt64 = 1024, nonce: String = nonce,
                      schema: Int = 1) -> Data {
        Data("{\"schema_version\":\(schema),\"nonce\":\"\(nonce)\",\"pid\":\(pid),\"sequence\":\(sequence),\"physical_footprint_bytes\":\(footprint)}\n".utf8)
    }

    static func testStrictFrameAndIdentity() throws {
        let sample = try NativeSelfMemorySample.parse(frame(), expectedPID: 4242, expectedNonce: nonce)
        try require(sample.schemaVersion == 1 && sample.pid == 4242 && sample.sequence == 1 &&
                    sample.physicalFootprintBytes == 1024,
                    "Producer-format telemetry fields were not decoded")

        for raw in [
            "{\"schema_version\":1,\"nonce\":\"\(nonce)\",\"pid\":4242,\"pid\":4242,\"sequence\":1,\"physical_footprint_bytes\":1024}",
            "{\"schema_version\":1,\"nonce\":\"\(nonce)\",\"pid\":4242,\"sequence\":1,\"physical_footprint_bytes\":1024,\"extra\":true}",
            String(repeating: "x", count: 4097)
        ] {
            var refused = false
            do { _ = try NativeSelfMemorySample.parse(Data(raw.utf8), expectedPID: 4242, expectedNonce: nonce) }
            catch { refused = true }
            try require(refused, "Duplicate, extra, or oversized telemetry was accepted")
        }
        var wrongIdentityRefused = false
        do { _ = try NativeSelfMemorySample.parse(frame(pid: 4243), expectedPID: 4242, expectedNonce: nonce) }
        catch { wrongIdentityRefused = true }
        try require(wrongIdentityRefused, "Telemetry from another PID was accepted")
        var wrongNonceRefused = false
        do {
            _ = try NativeSelfMemorySample.parse(frame(nonce: "fedcba9876543210fedcba9876543210"),
                                                expectedPID: 4242, expectedNonce: nonce)
        } catch { wrongNonceRefused = true }
        try require(wrongNonceRefused, "Telemetry with a different per-run nonce was accepted")
        print("PASS strict self-telemetry schema, bounded size, unique fields, nonce, and direct-child PID")
    }

    static func testPrivatePathAndBoundedFileRead(root: URL) throws {
        let fixtureRoot = root.appendingPathComponent("Tests/artifacts/self-telemetry-\(UUID().uuidString)",
                                                       isDirectory: true)
        let data = fixtureRoot.appendingPathComponent("Data", isDirectory: true)
        try FileManager.default.createDirectory(at: data, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let target = data.appendingPathComponent("self-memory.json")
        try NativeSelfTelemetryFile.validateAbsentTarget(target, under: data)
        try frame().write(to: target, options: .atomic)
        let sample = try NativeSelfTelemetryFile.read(target, under: data,
            expectedPID: 4242, expectedNonce: nonce)
        try require(sample?.sequence == 1 && sample?.physicalFootprintBytes == 1024,
                    "Atomic regular telemetry file did not read successfully")

        var existingTargetRefused = false
        do { try NativeSelfTelemetryFile.validateAbsentTarget(target, under: data) }
        catch { existingTargetRefused = true }
        try require(existingTargetRefused, "A reused telemetry target was accepted")

        let symlink = data.appendingPathComponent("symlink.json")
        try FileManager.default.createSymbolicLink(at: symlink, withDestinationURL: target)
        var symlinkRefused = false
        do { _ = try NativeSelfTelemetryFile.read(symlink, under: data,
            expectedPID: 4242, expectedNonce: nonce) }
        catch { symlinkRefused = true }
        try require(symlinkRefused, "A symlink telemetry file was followed")

        let parentAlias = data.appendingPathComponent("Data-alias", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: parentAlias, withDestinationURL: data)
        var symlinkAncestorRefused = false
        do {
            try NativeSelfTelemetryFile.validateAbsentTarget(
                parentAlias.appendingPathComponent("new.json"), under: data)
        } catch { symlinkAncestorRefused = true }
        try require(symlinkAncestorRefused, "A symlink ancestor was accepted for telemetry output")

        let oversized = data.appendingPathComponent("oversized.json")
        try Data(repeating: 0x20, count: 4097).write(to: oversized, options: .atomic)
        var oversizedRefused = false
        do { _ = try NativeSelfTelemetryFile.read(oversized, under: data,
            expectedPID: 4242, expectedNonce: nonce) }
        catch { oversizedRefused = true }
        try require(oversizedRefused, "An oversized telemetry file was read")
        print("PASS telemetry output path, no-follow reader, immutable target, and 4096-byte bound")
    }

    static func testWatchdogPolicy() throws {
        let base: UInt64 = 10_000_000_000
        try require(NativeSelfTelemetryPolicy.firstSampleTimeoutNanoseconds == 5 * 1_000_000_000 &&
                    NativeSelfTelemetryPolicy.staleAfterNanoseconds == 3 * 1_000_000_000 &&
                    NativeSelfTelemetryPolicy.threshold(physicalMemory: 8 << 30) == 8 << 30 &&
                    NativeSelfTelemetryPolicy.threshold(physicalMemory: 16 << 30) == 8 << 30 &&
                    NativeSelfTelemetryPolicy.threshold(physicalMemory: 36 << 30) == 18 << 30 &&
                    NativeSelfTelemetryPolicy.threshold(physicalMemory: 48 << 30) == 24 << 30 &&
                    NativeSelfTelemetryPolicy.threshold(physicalMemory: 128 << 30) == 24 << 30 &&
                    NativeSelfTelemetryPolicy.sampledFootprintThreshold ==
                        NativeSelfTelemetryPolicy.threshold(physicalMemory: ProcessInfo.processInfo.physicalMemory),
                    "Native Metal preview supervisor policy differs from the RAM-scaled 8-24 GiB limit")
        let missing = NativeSelfTelemetryPolicy(startedAt: base)
        try require(missing.timeout(at: base + NativeSelfTelemetryPolicy.firstSampleTimeoutNanoseconds - 1) == nil &&
                    missing.timeout(at: base + NativeSelfTelemetryPolicy.firstSampleTimeoutNanoseconds) == .telemetryMissing,
                    "Missing-first-sample deadline did not use the strict configured bound")

        var healthy = NativeSelfTelemetryPolicy(startedAt: base)
        let first = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242,
                                           sequence: 1, physicalFootprintBytes: 1024)
        try require(healthy.accept(first, at: base + 1_000_000_000, expectedPID: 4242) == nil,
                    "Fresh telemetry triggered a stop")
        try require(healthy.accept(first, at: base + 2_000_000_000, expectedPID: 4242) == nil &&
                    healthy.sampleCount == 1 &&
                    healthy.timeout(at: base + 4_000_000_000) == .telemetryStale,
                    "A repeated old sequence incorrectly refreshed heartbeat freshness")

        var longHealthyRun = NativeSelfTelemetryPolicy(startedAt: base)
        for sequence in UInt64(1)...UInt64(542) {
            let sample = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242,
                sequence: sequence, physicalFootprintBytes: 1024)
            let sampleTime = base + sequence * 1_000_000_000
            try require(longHealthyRun.accept(sample, at: sampleTime, expectedPID: 4242) == nil &&
                        longHealthyRun.timeout(at: sampleTime) == nil,
                        "Fresh healthy telemetry was stopped after the former 540-second window")
        }
        try require(longHealthyRun.sampleCount == 542,
                    "Healthy repeated-session policy did not keep accepting fresh telemetry past 540 seconds")

        var regression = NativeSelfTelemetryPolicy(startedAt: base)
        _ = regression.accept(first, at: base + 1, expectedPID: 4242)
        let rewritten = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242,
            sequence: 1, physicalFootprintBytes: 2048)
        try require(regression.accept(rewritten, at: base + 2, expectedPID: 4242) == .telemetryInvalid,
                    "Same-sequence rewritten telemetry was accepted")
        let lower = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242,
            sequence: 0, physicalFootprintBytes: 1024)
        try require(regression.accept(lower, at: base + 3, expectedPID: 4242) == .telemetryInvalid,
                    "Regressed telemetry sequence was accepted")

        var threshold = NativeSelfTelemetryPolicy(startedAt: base)
        let below = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242, sequence: 1,
            physicalFootprintBytes: NativeSelfTelemetryPolicy.sampledFootprintThreshold - 1)
        try require(threshold.accept(below, at: base + 1, expectedPID: 4242) == nil,
                    "The sampled 8 GiB threshold stopped below its exact boundary")
        let highNextSequence = NativeSelfMemorySample(schemaVersion: 1, nonce: nonce, pid: 4242, sequence: 2,
            physicalFootprintBytes: NativeSelfTelemetryPolicy.sampledFootprintThreshold)
        try require(threshold.accept(highNextSequence, at: base + 2, expectedPID: 4242) == .sampledThreshold,
                    "The sampled 8 GiB threshold did not trigger at its exact boundary")
        print("PASS first-sample/stale/RAM-scaled memory boundaries, healthy telemetry beyond 540 seconds, and sequence-regression refusal")
    }

    static func testExactOwnedChildLifecycle() throws {
        var lifecycle = ExactOwnedChildTerminationPolicy(expectedPID: 4242)
        try require(lifecycle.nextAction(at: 100, childIsRunning: false, observedPID: 4242) == .childExited,
                    "Exited child was signaled")
        var unrelated = ExactOwnedChildTerminationPolicy(expectedPID: 4242)
        try require(unrelated.nextAction(at: 100, childIsRunning: true, observedPID: 4243) == .identityMismatch,
                    "An unrelated process identity received an owned-child signal")

        try require(lifecycle.nextAction(at: 100, childIsRunning: true, observedPID: 4242) == .sendTerminate &&
                    lifecycle.nextAction(at: 100 + ExactOwnedChildTerminationPolicy.termGraceNanoseconds - 1,
                                         childIsRunning: true, observedPID: 4242) == .wait &&
                    lifecycle.nextAction(at: 100 + ExactOwnedChildTerminationPolicy.termGraceNanoseconds,
                                         childIsRunning: true, observedPID: 4242) == .sendKill &&
                    lifecycle.nextAction(at: 100 + ExactOwnedChildTerminationPolicy.termGraceNanoseconds +
                                         ExactOwnedChildTerminationPolicy.killGraceNanoseconds,
                                         childIsRunning: true, observedPID: 4242) == .survivorHold &&
                    lifecycle.nextAction(at: 100 + 4 * ExactOwnedChildTerminationPolicy.termGraceNanoseconds,
                                         childIsRunning: true, observedPID: 4242) == .wait,
                    "TERM/KILL grace or survivor hold targeted the wrong lifecycle stage")
        print("PASS TERM/KILL policy is bound to retained child PID and stops signaling in survivor hold")
    }
}
