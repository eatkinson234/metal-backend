import Foundation
import Darwin
import AppKit

@main struct LobbySelfTest {
    static func main() throws {
        guard CommandLine.arguments.count == 2 else { throw LobbyError("Expected candidate root path") }
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        let seedURL = root.appendingPathComponent("Fixtures/seed-config.json")
        let seed = try Data(contentsOf: seedURL)
        let labels = Array(repeating: "Same controller label", count: 8)
        var serializedByCapacity: [Int: Data] = [:]
        var expectedPreparedByCapacity: [Int: Data] = [:]
        for capacity in [1, 4, 8] {
            serializedByCapacity[capacity] = try Data(contentsOf: root.appendingPathComponent(
                "Fixtures/native-serialized-capacity-\(capacity).json"))
            expectedPreparedByCapacity[capacity] = try Data(contentsOf: root.appendingPathComponent(
                "Fixtures/expected-prepared-capacity-\(capacity).json"))
        }
        try testConfiguration(seed: seed, expectedPreparedByCapacity: expectedPreparedByCapacity,
                              serializedByCapacity: serializedByCapacity, labels: labels,
                              artifacts: root.appendingPathComponent("Tests/artifacts"))
        try testLauncherFullscreenArguments()
        try testCloseGuardUsesRealProcessIdentity()
        try testSlotPolicy()
        try testTriggerLatch()
        try testHandOverWithholdsHeldButtons()
        try testInputTiming(root: root)
        try testEventInput()
        try testControllerWiring(root: root)
        try testEventInputWiring(root: root)
        try testReusablePreviewLease(root: root.appendingPathComponent("Tests/artifacts"))
        try testNativePreviewEnvironment()
        try testExactPoint53PackagePin()
        try testGate(seed: seed, root: root.appendingPathComponent("Tests/artifacts"))
        try testConfigTransaction(seed: seed, labels: labels, root: root.appendingPathComponent("Tests/artifacts"))
        try testTwoReusableConfigSessions(seed: seed, serializedByCapacity: serializedByCapacity,
            labels: labels, root: root.appendingPathComponent("Tests/artifacts"))
        try testStopOnlySupervisorFallback(root: root.appendingPathComponent("Tests/artifacts"))
        try testLeaseMismatchAfterProcessRun(seed: seed, labels: labels,
            root: root.appendingPathComponent("Tests/artifacts"))
        try NativeSelfTelemetrySelfTest.run(root: root)
        print("PASS native lobby pure policy, config, transaction, and gate tests")
    }

    static func require(_ value: @autoclosure () -> Bool, _ message: String) throws {
        guard value() else { throw LobbyError(message) }
    }

    static func testCloseGuardUsesRealProcessIdentity() throws {
        let neverStarted = Process()
        try require(neverStarted.processIdentifier == 0 && !neverStarted.isRunning &&
                    !NativeLobbyLifecycle.closeMustWait(modelIsRunning: false, retainedChild: neverStarted),
                    "A never-started Process with PID 0 blocked lobby close")
        print("PASS close guard permits never-started Process with PID 0")

        let failedStart = Process()
        failedStart.executableURL = URL(fileURLWithPath: "/nonexistent-smash-couch-spawn-probe-r2")
        var runFailed = false
        do { try failedStart.run() } catch { runFailed = true }
        try require(runFailed && failedStart.processIdentifier == 0 && !failedStart.isRunning &&
                    !NativeLobbyLifecycle.closeMustWait(modelIsRunning: false, retainedChild: failedStart),
                    "A failed Process.run() with PID 0 blocked lobby close")
        print("PASS close guard permits failed Process.run() with PID 0")

        let live = Process()
        live.executableURL = URL(fileURLWithPath: "/bin/sleep")
        live.arguments = ["10"]
        live.standardOutput = FileHandle.nullDevice
        live.standardError = FileHandle.nullDevice
        try live.run()
        let livePID = live.processIdentifier
        defer {
            if live.isRunning { live.terminate() }
            live.waitUntilExit()
        }
        try require(livePID > 0 && live.isRunning &&
                    NativeLobbyLifecycle.closeMustWait(modelIsRunning: false, retainedChild: live),
                    "A live child Process did not block lobby close")
        print("PASS close guard blocks a live child with a real PID")
        live.terminate()
        live.waitUntilExit()

        let exited = Process()
        exited.executableURL = URL(fileURLWithPath: "/usr/bin/true")
        try exited.run()
        exited.waitUntilExit()
        try require(exited.processIdentifier > 0 && !exited.isRunning &&
                    NativeLobbyLifecycle.closeMustWait(modelIsRunning: false, retainedChild: exited),
                    "An exited-but-retained Process did not block close until finished clears it")
        print("PASS close guard blocks exited-but-retained child until lifecycle cleanup")

        try require(!NativeLobbyLifecycle.closeMustWait(modelIsRunning: false, retainedChild: nil),
                    "Clearing the finished child did not permit lobby close")
        print("PASS close guard permits close after finished clears the child")

        try require(NativeLobbyLifecycle.closeMustWait(modelIsRunning: true, retainedChild: nil),
                    "The running lifecycle marker did not block lobby close")
        print("PASS close guard retains the running lifecycle barrier without a child reference")
    }

    static func jsonData(_ value: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: value, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    }

    static func jsonObject(_ data: Data) throws -> [String: Any] {
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw LobbyError("Test fixture JSON was not an object")
        }
        return object
    }

    static func expectRefusal(_ name: String, _ body: () throws -> Void) throws {
        do {
            try body()
            throw LobbyError("Negative test was accepted: \(name)")
        } catch let error as LobbyError where error.reason == "Negative test was accepted: \(name)" {
            throw error
        } catch {
            print("PASS refused \(name)")
        }
    }

    static func testConfiguration(seed: Data, expectedPreparedByCapacity: [Int: Data],
                                  serializedByCapacity: [Int: Data], labels: [String],
                                  artifacts: URL) throws {
        try FileManager.default.createDirectory(at: artifacts, withIntermediateDirectories: true)
        try require(LobbyGate.digest(data: seed) == "968bee7beba874d89c57ee0f535a6a6d70724f97604a991d845d10d47ef0b21a",
                    "Seed fixture bytes differ from the exact root-preserved current .36 profile fixture")
        let expectedPreparedInputPins = [
            1: "fdced6bce46375dab371e799639351370c7e50683eea17a6b642e408718cb0ef",
            4: "75d3cc43e09961add6b6a646779054f7b9d2550338e0d6894c20ea5e15864d34",
            8: "c58ca038bdbd3bd5681227ae968981a5e60fed4693f7aea24dc3d7adb00c6b1f"
        ]
        let projectionPins = [
            1: "d3f3555fe2f8748f5dccafe1707f2b25360e0318aabccb50d8b58ee438cdc8b2",
            4: "fe3cdfdfbad3fe5cdc012b3896986edf07aa8054dcf513f9ac63fa4522f52bfb",
            8: "16158ea41350e6afa350e4f1bef3b02279119c69b3a170dc0324f3a07cfc2f91"
        ]
        let serializedPins = [
            1: "eab1ac76a03d21684780e34fb99cb6ba13963af45de7b4f01cc839e1ef641185",
            4: "37d3da05fad91d42847d45cabee096383dd5867a6ab5ee21a6da48df80b8d6ef",
            8: "3b025496eddc5c195afc169b4657328ba9ea84cdae958fffbbc181aa8089e5c9"
        ]
        _ = try NativeLobbyConfiguration.validateSeed(seed)
        print("PASS exact one-profile schema-67 Metal seed admission for the root-preserved current profile fixture")

        for capacity in [1, 4, 8] {
            guard let expectedInputPin = expectedPreparedInputPins[capacity],
                  let expectedPrepared = expectedPreparedByCapacity[capacity],
                  let projectionPin = projectionPins[capacity],
                  let serializedPin = serializedPins[capacity] else {
                throw LobbyError("Missing accepted .53 config evidence hash for capacity \(capacity)")
            }
            let prepared = try NativeLobbyConfiguration.prepare(seed: seed, capacity: capacity, labels: labels)
            try NativeLobbyConfiguration.validatePrepared(prepared, againstSeed: seed, capacity: capacity, labels: labels)
            let canonicalPrepared = try NativeLobbyConfiguration.canonicalJSON(prepared)
            let canonicalExpectedPrepared = try NativeLobbyConfiguration.canonicalJSON(expectedPrepared)
            try require(LobbyGate.digest(data: expectedPrepared) == expectedInputPin &&
                        canonicalPrepared == canonicalExpectedPrepared &&
                        LobbyGate.digest(data: canonicalExpectedPrepared) == projectionPin,
                        "Capacity \(capacity) preparation differs from the independent full semantic projection")
            let output = artifacts.appendingPathComponent("prepared-capacity-\(capacity).json")
            try prepared.write(to: output, options: .atomic)
            let config = try jsonObject(prepared)
            let profiles = config["input_config"] as! [[String: Any]]
            try require(profiles.count == capacity, "Prepared profile count differed at capacity \(capacity)")
            try require(config["start_fullscreen"] as? Bool == false,
                        "Prepared Config.json start_fullscreen must remain false; fullscreen is a launch argument")
            try require(config["remember_window_state"] as? Bool == false,
                        "Prepared Config.json must not save transient fullscreen geometry")
            try require(profiles.allSatisfy { $0["name"] == nil },
                        "Unsupported user labels were injected into native InputConfig")
            let expectedRoundTrip = try NativeLobbyConfiguration.expectedNativeRoundTrip(prepared)
            guard let capturedSerialized = serializedByCapacity[capacity] else {
                throw LobbyError("Missing captured .53 serializer fixture for capacity \(capacity)")
            }
            let canonicalCaptured = try NativeLobbyConfiguration.canonicalJSON(capturedSerialized)
            try require(canonicalCaptured == expectedRoundTrip,
                        "Captured .53 serializer changed or omitted seed fields at capacity \(capacity)")
            try require(LobbyGate.digest(data: expectedRoundTrip) == projectionPin &&
                        LobbyGate.digest(data: capturedSerialized) == serializedPin,
                        "Capacity \(capacity) no longer matches the accepted .53 serializer evidence hashes")
            try expectedRoundTrip.write(to: artifacts.appendingPathComponent(
                "expected-native-roundtrip-capacity-\(capacity).json"), options: .atomic)
            print("PASS capacity \(capacity) preparation and exact-field .53 serializer projection: \(output.lastPathComponent)")
        }
        var fullscreenPrepared = try jsonObject(NativeLobbyConfiguration.prepare(
            seed: seed, capacity: 1, labels: labels))
        fullscreenPrepared["start_fullscreen"] = true
        try expectRefusal("fullscreen prepared preview config") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(fullscreenPrepared), againstSeed: seed,
                                                           capacity: 1, labels: labels)
        }
        var confirmExitPrepared = try jsonObject(NativeLobbyConfiguration.prepare(
            seed: seed, capacity: 1, labels: labels))
        try require(confirmExitPrepared["show_confirm_exit"] as? Bool == false,
                    "Prepared Config.json must disable the exit prompt that deadlocks Cmd-Q")
        confirmExitPrepared["show_confirm_exit"] = true
        try expectRefusal("exit-prompt prepared preview config") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(confirmExitPrepared), againstSeed: seed,
                                                           capacity: 1, labels: labels)
        }
        print("PASS prepared sessions quit without the Cmd-Q-deadlocking exit prompt")
        var persistentWindowPrepared = try jsonObject(NativeLobbyConfiguration.prepare(
            seed: seed, capacity: 1, labels: labels))
        persistentWindowPrepared["remember_window_state"] = true
        try expectRefusal("saved-window-state prepared preview config") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(persistentWindowPrepared), againstSeed: seed,
                                                           capacity: 1, labels: labels)
        }
        let preferences = LobbyPreferences(labels: labels)
        let preferenceRoundTrip = try JSONDecoder().decode(LobbyPreferences.self,
            from: JSONEncoder().encode(preferences))
        try require(preferenceRoundTrip.isValid && preferenceRoundTrip.labels == labels,
                    "User slot labels did not remain in private lobby preferences")
        print("PASS user labels remain private lobby preferences, separate from native device config")

        var config = try jsonObject(seed)
        config["version"] = 70
        try expectRefusal("schema 70 seed") { _ = try NativeLobbyConfiguration.validateSeed(jsonData(config)) }
        config = try jsonObject(seed)
        config["remember_window_state"] = false
        try expectRefusal("seed without remembered window-state preference") {
            _ = try NativeLobbyConfiguration.validateSeed(jsonData(config))
        }
        config = try jsonObject(seed)
        var seedProfiles = config["input_config"] as! [[String: Any]]
        seedProfiles.append(seedProfiles[0])
        config["input_config"] = seedProfiles
        try expectRefusal("duplicate seed profiles") { _ = try NativeLobbyConfiguration.validateSeed(jsonData(config)) }
        config = try jsonObject(seed)
        var wrongSeed = (config["input_config"] as! [[String: Any]])[0]
        wrongSeed["id"] = "couch-2"
        config["input_config"] = [wrongSeed]
        try expectRefusal("wrong seed slot") { _ = try NativeLobbyConfiguration.validateSeed(jsonData(config)) }
        config = try jsonObject(seed)
        var missingSeedMapping = (config["input_config"] as! [[String: Any]])[0]
        var seedButtons = missingSeedMapping["right_joycon"] as! [String: Any]
        seedButtons.removeValue(forKey: "button_x")
        missingSeedMapping["right_joycon"] = seedButtons
        config["input_config"] = [missingSeedMapping]
        try expectRefusal("missing seed positional mapping") { _ = try NativeLobbyConfiguration.validateSeed(jsonData(config)) }
        config = try jsonObject(seed)
        var namedSeedProfile = (config["input_config"] as! [[String: Any]])[0]
        namedSeedProfile["name"] = "unapproved-profile-metadata"
        config["input_config"] = [namedSeedProfile]
        try expectRefusal("seed profile name outside the pinned .53 bytes") {
            _ = try NativeLobbyConfiguration.validateSeed(jsonData(config))
        }

        let prepared = try NativeLobbyConfiguration.prepare(seed: seed, capacity: 4, labels: labels)
        config = try jsonObject(prepared)
        var profiles = config["input_config"] as! [[String: Any]]
        profiles[1]["id"] = profiles[0]["id"]
        profiles[1]["player_index"] = profiles[0]["player_index"]
        config["input_config"] = profiles
        try expectRefusal("duplicate prepared ID and player index") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }
        config = try jsonObject(prepared)
        profiles = config["input_config"] as! [[String: Any]]
        profiles[1]["id"] = "couch-3"
        config["input_config"] = profiles
        try expectRefusal("wrong prepared slot") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }
        config = try jsonObject(prepared)
        profiles = config["input_config"] as! [[String: Any]]
        var wrongButtons = profiles[2]["right_joycon"] as! [String: Any]
        wrongButtons.removeValue(forKey: "button_y")
        profiles[2]["right_joycon"] = wrongButtons
        config["input_config"] = profiles
        try expectRefusal("missing prepared mapping") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }
        config = try jsonObject(prepared)
        config["vsync_mode"] = 1
        try expectRefusal("changed native option outside input_config") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }
        config = try jsonObject(prepared)
        profiles = config["input_config"] as! [[String: Any]]
        profiles[0]["deadzone_left"] = 0.4
        config["input_config"] = profiles
        try expectRefusal("changed profile template field") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }
        config = try jsonObject(prepared)
        profiles = config["input_config"] as! [[String: Any]]
        profiles[0]["name"] = "unsupported-injected-label"
        config["input_config"] = profiles
        try expectRefusal("unsupported name injected into prepared input profile") {
            try NativeLobbyConfiguration.validatePrepared(jsonData(config), againstSeed: seed, capacity: 4, labels: labels)
        }

    }

    static func testLauncherFullscreenArguments() throws {
        let privateData = URL(fileURLWithPath: "/private/native-lab/Data", isDirectory: true)
        let gamePath = "/private/native-lab/Data/games/Smash.xci"
        let arguments = NativeLobbyConfiguration.launchArguments(privateData: privateData, gamePath: gamePath)
        let expected = ["-r", privateData.path, "--graphics-backend", "metal", "--fullscreen", gamePath]
        try require(arguments == expected && arguments.filter { $0 == "--fullscreen" }.count == 1,
                    "Launcher must pass one supported fullscreen override while config start_fullscreen stays false")
        print("PASS one supported --fullscreen CLI override with prepared Config.json start_fullscreen=false")
    }

    static func testReusablePreviewLease(root artifacts: URL) throws {
        let privateRoot = artifacts.appendingPathComponent("preview-lease-\(UUID().uuidString)",
                                                           isDirectory: true)
        try FileManager.default.createDirectory(at: privateRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let firstID = UUID().uuidString.lowercased()
        let first = try NativePreviewSessionClaim.acquire(in: privateRoot, sessionID: firstID,
            at: Date(timeIntervalSince1970: 1_800_000_000))
        let leaseURL = privateRoot.appendingPathComponent(NativePreviewSessionClaim.fileName)
        let stored = try JSONDecoder().decode(NativePreviewSessionLease.self, from: Data(contentsOf: leaseURL))
        try require(stored == first && stored.sessionID == firstID && stored.childPID == nil,
                    "Active-session identity was not durably recorded")
        try expectRefusal("concurrent Native Metal session claim") {
            _ = try NativePreviewSessionClaim.acquire(in: privateRoot, sessionID: UUID().uuidString.lowercased())
        }
        let firstWithChild = try NativePreviewSessionClaim.recordChildStarted(in: privateRoot,
            lease: first, childPID: 4242)
        let recordedChild = try JSONDecoder().decode(NativePreviewSessionLease.self, from: Data(contentsOf: leaseURL))
        try require(recordedChild == firstWithChild && recordedChild.childPID == 4242,
                    "Durable lease did not retain the exact direct-child PID")
        var stopPolicy = NativeSelfTelemetryPolicy(startedAt: 50_000_000_000)
        let thresholdSample = NativeSelfMemorySample(schemaVersion: 1, nonce: NativeSelfTelemetrySelfTest.nonce,
            pid: 4242, sequence: 1, physicalFootprintBytes: NativeSelfTelemetryPolicy.sampledFootprintThreshold)
        try require(stopPolicy.accept(thresholdSample, at: 51_000_000_000, expectedPID: 4242) == .sampledThreshold,
                    "Failure-stop lease case did not exercise the sampled threshold")
        var stopLifecycle = ExactOwnedChildTerminationPolicy(expectedPID: 4242)
        try require(stopLifecycle.nextAction(at: 51_000_000_000, childIsRunning: true,
            observedPID: 4242) == .sendTerminate &&
            stopLifecycle.nextAction(at: 52_000_000_000, childIsRunning: true,
                observedPID: 4242) == .sendKill &&
            stopLifecycle.nextAction(at: 53_000_000_000, childIsRunning: true,
                observedPID: 4242) == .survivorHold,
            "Failure-stop simulation did not progress to survivor hold")
        try expectRefusal("active lease release while stop-requested child survives") {
            try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: privateRoot,
                lease: firstWithChild, exitedChildPID: nil)
        }
        try expectRefusal("new session while failed child stop remains held") {
            _ = try NativePreviewSessionClaim.acquire(in: privateRoot, sessionID: UUID().uuidString.lowercased())
        }
        try expectRefusal("lease release for a different child identity") {
            try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: privateRoot,
                lease: firstWithChild, exitedChildPID: 4243)
        }
        try expectRefusal("lease release before recording child identity") {
            try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: privateRoot,
                lease: first, exitedChildPID: nil)
        }
        try require(FileManager.default.fileExists(atPath: leaseURL.path),
                    "Stop-requested child failure removed its held active-session lease")

        let staleRoot = artifacts.appendingPathComponent("preview-lease-stale-\(UUID().uuidString)",
                                                         isDirectory: true)
        try FileManager.default.createDirectory(at: staleRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        _ = try NativePreviewSessionClaim.acquire(in: staleRoot,
            sessionID: "4a9d5790-1000-49b5-b21e-264c9ca3a100",
            at: Date(timeIntervalSince1970: 1))
        try expectRefusal("stale lease auto-reclaim") {
            _ = try NativePreviewSessionClaim.acquire(in: staleRoot, sessionID: UUID().uuidString.lowercased())
        }
        let malformedRoot = artifacts.appendingPathComponent("preview-lease-malformed-\(UUID().uuidString)",
                                                             isDirectory: true)
        try FileManager.default.createDirectory(at: malformedRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        try Data("malformed lease".utf8).write(to:
            malformedRoot.appendingPathComponent(NativePreviewSessionClaim.fileName), options: .atomic)
        try expectRefusal("malformed lease recovery") {
            _ = try NativePreviewSessionClaim.acquire(in: malformedRoot, sessionID: UUID().uuidString.lowercased())
        }
        print("PASS exclusive child lease, failure/survivor hold, and fail-closed stale or malformed markers")
    }

    static func testNativePreviewEnvironment() throws {
        let base = [
            "HOME": "/private/test-home", "PATH": "/usr/bin",
            "DOTNET_gcServer": "0", "DOTNET_GCHeapCount": "1",
            "COMPlus_gcServer": "0", "COMPlus_GCHeapCount": "2",
            "dotnet_gcheapcount": "3", "DOTNET_GCHeapHardLimit": "1",
            "DOTNET_ReadyToRun": "0",
            "METAL_PERFORMANCE_PER_FRAME_SIGNPOSTS": "1",
            "SMASH_COUCH_TIMING_DIR": "/private/timing",
            "SMASH_COUCH_POINT_TRANSPORT_EVIDENCE_DIR": "/private/point-evidence",
            "SMASH_COUCH_DEPTH_SNAPSHOT_EVIDENCE_DIR": "/private/depth-evidence",
            "SMASH_COUCH_DEPTH_SNAPSHOT_ADMISSION_EVIDENCE_DIR": "/private/admission-evidence",
            "SMASH_COUCH_CUBE_EVIDENCE_DIR": "/private/cube-evidence",
            "SMASH_COUCH_FEEDBACK_EVIDENCE_DIR": "/private/feedback-evidence",
            "MTL_DEBUG_LAYER": "1", "MTL_SHADER_VALIDATION": "1",
            "MTL_HUD_ENABLED": "1", "MTL_CAPTURE_ENABLED": "1",
            "MTLCAPTURE_WAIT_FOR_SIGNAL": "1", "SMASH_MVK_WAIT": "1"
        ]
        let data = URL(fileURLWithPath: "/private/profile/Data")
        let firstSessionID = "d02b0662-61d7-4e53-a8c0-d5b72e4df601"
        let secondSessionID = "d02b0662-61d7-4e53-a8c0-d5b72e4df602"
        let telemetry = try NativeSelfTelemetryFile.targetURL(sessionID: firstSessionID, under: data)
        let secondTelemetry = try NativeSelfTelemetryFile.targetURL(sessionID: secondSessionID, under: data)
        try require(telemetry != secondTelemetry && telemetry.lastPathComponent.contains(firstSessionID) &&
                    secondTelemetry.lastPathComponent.contains(secondSessionID),
                    "Per-session telemetry paths were not unique and identity-bound")
        try expectRefusal("noncanonical telemetry session ID") {
            _ = try NativeSelfTelemetryFile.targetURL(sessionID: firstSessionID.uppercased(), under: data)
        }
        let child = NativePreviewEnvironment.child(base: base, privateData: data, telemetry: telemetry,
            nonce: "0123456789abcdef0123456789abcdef", socket: "/tmp/sc-test/in",
            token: [0x00, 0x01, 0x2a, 0xff])
        let requiredLobbyKeys: Set<String> = [
            "SMASH_COUCH_REQUIRE_NATIVE_METAL", "SMASH_COUCH_PRIVATE_DATA",
            "SMASH_COUCH_SELF_TELEMETRY_PATH", "SMASH_COUCH_SELF_TELEMETRY_NONCE",
            "SMASH_COUCH_SOCKET", "SMASH_COUCH_TOKEN"
        ]
        let lobbyKeys = Set(child.keys.filter { $0.hasPrefix("SMASH_COUCH_") })
        let metalKeys: Set<String> = ["MTL_DEBUG_LAYER", "MTL_SHADER_VALIDATION", "MTL_HUD_ENABLED",
                                      "MTL_CAPTURE_ENABLED", "MTLCAPTURE_WAIT_FOR_SIGNAL"]
        let observedMetalKeys = Set(child.keys.filter { $0.hasPrefix("MTL_") || $0.hasPrefix("MTLCAPTURE_") })
        try require(child["HOME"] == base["HOME"] && child["PATH"] == base["PATH"],
                    "Preview environment did not preserve unrelated inherited settings")
        try require(lobbyKeys == requiredLobbyKeys && child["SMASH_COUCH_PRIVATE_DATA"] == data.path &&
                    child["SMASH_COUCH_SELF_TELEMETRY_PATH"] == telemetry.path &&
                    child["SMASH_COUCH_SOCKET"] == "/tmp/sc-test/in" && child["SMASH_COUCH_TOKEN"] == "00012aff",
                    "Preview environment leaked diagnostic variables or lost the private transport contract")
        try require(child["METAL_PERFORMANCE_PER_FRAME_SIGNPOSTS"] == nil,
                    "Preview environment inherited diagnostic per-frame signposts")
        try require(observedMetalKeys == metalKeys && metalKeys.allSatisfy({ child[$0] == "0" }) &&
                    !child.keys.contains(where: { $0.hasPrefix("SMASH_MVK_") }),
                    "Preview environment differs from the exact normal .53 Metal settings")
        print("PASS isolated normal .53 Metal environment keeps only native, telemetry, and controller transport keys")
        let gcKeys = child.filter { key, _ in
            key.uppercased().hasPrefix("DOTNET_GC") || key.uppercased().hasPrefix("COMPLUS_GC")
        }
        try require(gcKeys == ["DOTNET_gcServer": "1", "DOTNET_GCHeapCount": "8", "DOTNET_GCHeapHardLimit": "0x200000000"] &&
                    child["DOTNET_ReadyToRun"] == "0",
                    "Child GC profile leaked conflicting overrides or changed unrelated .NET settings")
        print("PASS native capped GC8 child profile restores exact 8 GiB and overrides inherited aliases and preserves unrelated .NET settings")
    }

    static func testExactPoint53PackagePin() throws {
        let expectedRuntimeRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Smash Couch/Runtime/native-metal-58-couch-r16", isDirectory: true)
        try require(LobbyGate.privateRootName == "native-metal-controller-lobby53-preview-r1" &&
                    LobbyGate.approvedSeedConfigSHA256 == "968bee7beba874d89c57ee0f535a6a6d70724f97604a991d845d10d47ef0b21a" &&
                    LobbyGate.approvedPackageManifestSHA256 == "cc2ededa8db860f23078ce71d0049aa699dbab3ad124385d02e648e411c535e9" &&
                    LobbyGate.approvedPackageOutputDirectory == "native53-preview-packageoutputs" &&
                    LobbyGate.approvedNativeAppName == "Ryujinx Native Metal VI Cancel R1.app" &&
                    LobbyGate.approvedNativeBundleIdentifier == "local.smash.couch.native-metal-vi-cancel-r1" &&
                    LobbyGate.approvedNativeBundleName == "Ryujinx Native Metal VI Cancel R1" &&
                    LobbyGate.approvedNativeVersion == "1.3.3-metal.53-vi-cancel-r1" &&
                    LobbyGate.runtimeRoot.standardizedFileURL == expectedRuntimeRoot.standardizedFileURL &&
                    LobbyGate.approvedNativeApp.standardizedFileURL == expectedRuntimeRoot.appendingPathComponent(LobbyGate.approvedNativeAppName).standardizedFileURL &&
                    LobbyGate.approvedManifest.standardizedFileURL == expectedRuntimeRoot.appendingPathComponent("native53-preview-packageoutputs/app-manifest.json").standardizedFileURL,
                    "Private seed/package pin or standalone runtime paths differ from native53 private preview plan")
        print("PASS exact .53 seed/package identity and workspace-independent runtime-root paths")
    }

    static func testSlotPolicy() throws {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        /// One poll's join presses; orders default to the request position (connection order).
        func join(_ book: inout SlotBook, _ ids: [UUID], orders: [UInt64]? = nil,
                  at time: Date = t0) -> [SlotJoinOutcome] {
            let requests = ids.enumerated().map { SlotJoinRequest(connection: $1, order: orders?[$0] ?? UInt64($0)) }
            let outcomes = book.joinAll(requests, at: time)
            return ids.map { id in outcomes.first(where: { $0.0 == id })?.1 ?? .full }
        }

        try require(SlotBook.slotCount == 8 && SlotBook().capacity == 8 && SlotBook().slots.count == 8,
                    "The lobby must always offer eight fixed spots")
        for taken in [[], [0], [0, 1, 2, 3], [1, 3], [5], Array(0..<8)] as [[Int]] {
            for allEight in [false, true] {
                let profiles = SlotBook.launchProfileCount(joinedOrHeld: taken, allEight: allEight)
                try require((1...8).contains(profiles) && profiles > (taken.max() ?? -1),
                            "Launch profile count must cover every joined or held spot and stay within 1...8")
            }
        }
        // Eight connected controllers cleared on the rig (c58r15-cap8, r16-cap8): every launch writes all eight,
        // so late friends join mid-game. The joined-count rule stays one line away.
        try require(SlotBook.launchesAllEight &&
                    [[], [0], [0, 1], [0, 1, 2, 3], [1, 3], [5], Array(0..<8)].map { SlotBook.launchProfileCount(joinedOrHeld: $0) } ==
                    [8, 8, 8, 8, 8, 8, 8] &&
                    [[], [0], [0, 1], [0, 1, 2, 3], [1, 3], [5], Array(0..<8)].map { SlotBook.launchProfileCount(joinedOrHeld: $0, allEight: false) } ==
                    [1, 1, 2, 4, 4, 6, 8],
                    "A launch must write all eight profiles (the joined-count rule only when launchesAllEight is off)")
        try require(SlotBook.playingMessage(joined: 1, profiles: 1) ==
                    "Playing with 1 player. To add players, quit the game with ⌘Q (it saves new shaders), then press A to join. A dropped controller can rejoin mid-game." &&
                    SlotBook.playingMessage(joined: 3, profiles: 8) ==
                    "Playing with 3 players. Late? Press A on any controller to join, even mid-game. Quit the game with ⌘Q to come back and save new shaders.",
                    "The playing message must only promise mid-game joins when all eight profiles were written")
        print("PASS eight fixed spots; every launch writes all eight profiles (joined-count rule: one line)")

        var eight = SlotBook()
        let ids = (0..<8).map { _ in UUID() }
        let shuffled: [Int] = [5, 0, 7, 2, 4, 1, 6, 3]
        let outcomes = join(&eight, shuffled.map { ids[$0] }, orders: shuffled.map { UInt64($0) })
        try require(outcomes == shuffled.map { .joined($0) } &&
                    (0..<8).allSatisfy { eight.slots[$0].connection == ids[$0] && eight.slots[$0].reserved &&
                        !eight.slots[$0].ready && eight.slots[$0].sample.isNeutral },
                    "Eight simultaneous joins must fill P1-P8 in connection order, neutral and not ready")
        try require(SlotBook.joinMessage(outcomes) == "P1, P2, P3, P4, P5, P6, P7 and P8 joined. Let go of all buttons to get ready.",
                    "Simultaneous join message changed")
        let before = eight.slots.map(\.connection)
        let ninth = UUID()
        try require(join(&eight, [ninth]) == [.full] && eight.slots.map(\.connection) == before,
                    "A ninth join must be refused without changing any spot")
        try require(SlotBook.joinMessage([.full]) == "All 8 spots are taken. Press View on a joined controller in the lobby to free one.",
                    "Full message changed")
        print("PASS eight simultaneous joins fill P1-P8 by connection order; a ninth is refused cleanly")

        let a = UUID(), b = UUID(), c = UUID()
        let orderOf: [UUID: UInt64] = [a: 5, b: 2, c: 9]
        for permutation in [[a, b, c], [a, c, b], [b, a, c], [b, c, a], [c, a, b], [c, b, a]] {
            var book = SlotBook()
            _ = join(&book, permutation, orders: permutation.map { orderOf[$0]! })
            try require(book.slots[0].connection == b && book.slots[1].connection == a && book.slots[2].connection == c &&
                        book.slots[3].connection == nil && book.slots.prefix(3).allSatisfy({ !$0.ready && $0.sample.isNeutral }),
                        "Simultaneous joins depended on the order requests arrived in")
        }
        let tieLow = UUID(), tieHigh = UUID()
        let (first, second) = tieLow.uuidString < tieHigh.uuidString ? (tieLow, tieHigh) : (tieHigh, tieLow)
        for pair in [[tieLow, tieHigh], [tieHigh, tieLow]] {
            var book = SlotBook()
            _ = join(&book, pair, orders: [7, 7])
            try require(book.slots[0].connection == first && book.slots[1].connection == second,
                        "Equal connection orders must still number deterministically")
        }
        print("PASS simultaneous joins are numbered by connection order in all six arrival permutations, ties stable")

        var duplicate = SlotBook()
        let twice = UUID()
        let duplicateOutcomes = duplicate.joinAll([SlotJoinRequest(connection: twice, order: 0),
                                                   SlotJoinRequest(connection: twice, order: 0)], at: t0)
        try require(duplicateOutcomes.count == 1 && duplicate.slots.filter({ $0.connection == twice }).count == 1 &&
                    duplicate.slots[1].connection == nil, "One controller listed twice in a poll took two spots")
        try require(join(&duplicate, [twice], at: t0.addingTimeInterval(5)) == [.alreadyJoined(0)] &&
                    duplicate.lastJoinAt == t0 && duplicate.slots[1].connection == nil,
                    "A joined controller pressing again must keep its spot and not restart the start delay")
        try require(SlotBook.joinMessage([.alreadyJoined(0)]) == nil, "A repeat press must not change the message")
        print("PASS a controller joins once; pressing again keeps its spot")

        var held = SlotBook()
        let p1 = UUID(), p2 = UUID(), p3 = UUID(), newcomer = UUID()
        _ = join(&held, [p1, p2, p3])
        held.disconnect(p2)
        try require(held.slots[1].reserved && held.slots[1].connection == nil, "Disconnect must hold the spot")
        try require(join(&held, [newcomer]) == [.reclaimed(1)] && held.slots[0].connection == p1 &&
                    held.slots[2].connection == p3 && held.slots[1].connection == newcomer && !held.slots[1].ready,
                    "A held spot must be filled first without moving P1 or P3")
        try require(SlotBook.joinMessage([.reclaimed(1)]) == "P2 is back. Let go of all buttons to get ready.",
                    "Reclaim message changed")
        var twoHeld = SlotBook()
        let four = (0..<4).map { _ in UUID() }
        _ = join(&twoHeld, four)
        twoHeld.disconnect(four[1])
        twoHeld.disconnect(four[3])
        let batch = (0..<3).map { _ in UUID() }
        try require(join(&twoHeld, batch, orders: [10, 11, 12]) == [.reclaimed(1), .reclaimed(3), .joined(4)] &&
                    twoHeld.slots[0].connection == four[0] && twoHeld.slots[2].connection == four[2],
                    "Held P2 and P4 must be filled before the open P5")
        try require(SlotBook.joinMessage([.reclaimed(1), .reclaimed(3), .joined(4)]) ==
                    "P5 joined and P2 and P4 are back. Let go of all buttons to get ready.", "Mixed join message changed")
        var cleared = SlotBook()
        let c1 = UUID(), c2 = UUID(), c3 = UUID(), c4 = UUID(), c5 = UUID()
        _ = join(&cleared, [c1, c2, c3])
        cleared.clear(1)
        try require(cleared.slots[1].connection == nil && !cleared.slots[1].reserved,
                    "View/remove must open the spot, not hold it")
        cleared.disconnect(c3)
        try require(join(&cleared, [c4]) == [.reclaimed(2)] && join(&cleared, [c5]) == [.joined(1)],
                    "Held spots come before a lower open spot; the open spot is next")
        print("PASS disconnected players' spots are held and filled first; a left spot is simply open")

        var release = SlotBook()
        let releaser = UUID()
        _ = join(&release, [releaser])
        release.update(releaser, sample: PadSample(buttons: 1 << 3))
        try require(!release.slots[0].ready && release.slots[0].sample.isNeutral,
                    "Joined controller was not neutral until release")
        release.update(releaser, sample: PadSample(buttons: 0, axes: [0, 0, 0, 0, 0.3, 0]))
        try require(!release.slots[0].ready, "A resting trigger counted as released")
        release.update(releaser, sample: .neutral)
        try require(release.slots[0].ready && release.slots[0].sample.isNeutral,
                    "Neutral release did not arm the joined controller")
        print("PASS join/reclaim neutral-until-release gate")

        var fixed = SlotBook()
        let packetIDs = (0..<8).map { _ in UUID() }
        _ = join(&fixed, packetIDs)
        let token = Array(UInt8(0)..<UInt8(16))
        let joinedPacket = fixed.packet(token: token, sequence: 6, captureTime: 1, enabled: true)
        try require((0..<8).allSatisfy { isNeutral(readSlot(joinedPacket, $0)) },
                    "A just-joined spot sent input before its controller was released")
        for (index, id) in packetIDs.enumerated() {
            fixed.update(id, sample: .neutral)
            fixed.update(id, sample: PadSample(buttons: (1 << 3) | (1 << 1) | (1 << 4),
                                               axes: [Float(index + 1) / 10, 0, 0, 0, 0, 0]))
        }
        let allPacket = fixed.packet(token: token, sequence: 7, captureTime: 1, enabled: true)
        try require(allPacket.count == SlotBook.packetBytes && read64(allPacket, 0) == SlotBook.packetMagicV2,
                    "SCINPUT2 packet header or size changed")
        for slot in 0..<8 {
            let record = readSlot(allPacket, slot)
            try require(record.enabled && record.buttons == ((1 << 1) | (1 << 3) | (1 << 4)),
                        "Positional pass-through failed in P\(slot + 1)")
            try require(abs(record.axes[0] - Float(slot + 1) / 10) < 0.0001,
                        "P\(slot + 1) axes were serialized into the wrong slot")
        }
        print("PASS all-eight packet slot order and positional pass-through")

        fixed.disconnect(packetIDs[1])
        let gapPacket = fixed.packet(token: token, sequence: 8, captureTime: 2, enabled: true)
        try require(!readSlot(gapPacket, 1).enabled && readSlot(gapPacket, 2).enabled &&
                    fixed.slots[2].connection == packetIDs[2], "Missing P2 shifted P3 or stayed active")
        let disabledPacket = fixed.packet(token: token, sequence: 9, captureTime: 3, enabled: false)
        try require((0..<8).allSatisfy { isNeutral(readSlot(disabledPacket, $0)) },
                    "A packet sent while the game is not running was not neutral across all eight slots")
        fixed.suspend()
        let suspendedPacket = fixed.packet(token: token, sequence: 10, captureTime: 4, enabled: true)
        try require((0..<8).allSatisfy { isNeutral(readSlot(suspendedPacket, $0)) },
                    "Suspension retained input or readiness")
        try require(fixed.slots[2].connection == packetIDs[2] && fixed.slots[2].reserved,
                    "Suspension changed P3's spot")
        print("PASS P2 disconnect preserves P3; packets outside a running game and after suspension are neutral")

        let axes: [Float] = [0.25, -0.5, 0.1, 0, 0.7, 0.2]
        for combo in 0..<16 {
            var buttons: UInt32 = (1 << 5) | (1 << 7) | (1 << 8) | (1 << 12) | (1 << 15) | (1 << 16)
            for (index, bit) in [1, 2, 3, 4].enumerated() where combo & (1 << index) != 0 { buttons |= 1 << UInt32(bit) }
            let sample = PadSample(buttons: buttons, axes: axes)
            var passThrough = SlotBook()
            let id = UUID()
            _ = join(&passThrough, [id])
            passThrough.update(id, sample: .neutral)
            passThrough.update(id, sample: sample)
            let record = readSlot(passThrough.packet(token: token, sequence: 1, captureTime: 1, enabled: true), 0)
            try require(record.enabled && record.buttons == buttons && record.axes == axes,
                        "Every button and axis must reach the game unchanged (Smash default controls)")
            try require(sample.pressedFaces == (0..<4).filter { combo & (1 << $0) != 0 },
                        "Pressed faces for the glyphs must follow the positional face bits")
        }
        let other: UInt32 = (1 << 7) | (1 << 16)
        try require(PadSample.positionalFaces(fromLabelOrdered: 1 << 1 | other) == 1 << 2 | other &&
                    PadSample.positionalFaces(fromLabelOrdered: 1 << 2) == 1 << 1 &&
                    PadSample.positionalFaces(fromLabelOrdered: 1 << 3) == 1 << 4 &&
                    PadSample.positionalFaces(fromLabelOrdered: 1 << 4) == 1 << 3 &&
                    PadSample.positionalFaces(fromLabelOrdered: 0b11110) == 0b11110,
                    "Nintendo label order must convert A->right, B->bottom, X->top, Y->left")
        var nintendo = SlotBook()
        let switchPad = UUID()
        _ = join(&nintendo, [switchPad])
        nintendo.update(switchPad, sample: .neutral)
        nintendo.update(switchPad, sample: PadSample(buttons: PadSample.positionalFaces(fromLabelOrdered: 1 << 1)))
        try require(readSlot(nintendo.packet(token: token, sequence: 1, captureTime: 1, enabled: true), 0).buttons == 1 << 2,
                    "A Switch pad's printed A (right) must attack, as on a Switch")
        print("PASS one layout: every button passes straight through; Switch label order converts to positions")

        func pad(_ bits: [Int], axes: [Float] = Array(repeating: 0, count: 6)) -> PadSample {
            PadSample(buttons: bits.reduce(UInt32(0)) { $0 | 1 << UInt32($1) }, axes: axes)
        }
        for bit in [1, 2, 3, 4, 16] {
            try require(SlotBook.isJoinPress(pad([bit]), .neutral), "Fresh button \(bit) must join")
            try require(!SlotBook.isJoinPress(pad([bit]), pad([bit])), "Held button \(bit) must not join again")
        }
        for bit in [5, 6, 7, 8, 11, 12, 13, 14, 15] {
            try require(!SlotBook.isJoinPress(pad([bit]), .neutral), "Button \(bit) must not join")
        }
        try require(!SlotBook.isJoinPress(pad([], axes: [1, 0, 0, 0, 1, 1]), .neutral) &&
                    SlotBook.isJoinPress(pad([1, 7]), pad([7])),
                    "Sticks and triggers must not join; A pressed while LB is held must join")
        print("PASS any face button or Menu joins; View, shoulders, sticks, direction pad and triggers do not")

        var launch = SlotBook()
        let starter = UUID(), stranger = UUID()
        _ = join(&launch, [starter])
        launch.update(starter, sample: .neutral)
        let menu = pad([16])
        try require(launch.isLaunchPress(starter, current: menu, previous: .neutral) &&
                    launch.isSettling(at: t0.addingTimeInterval(0.5)) &&
                    !launch.canLaunch(starter, current: menu, previous: .neutral, at: t0.addingTimeInterval(0.5)),
                    "Menu within a second of a join must not start the game")
        try require(launch.canLaunch(starter, current: menu, previous: .neutral, at: t0.addingTimeInterval(1.1)),
                    "Menu on a ready player after the join settle must start the game")
        try require(!launch.canLaunch(stranger, current: menu, previous: .neutral, at: t0.addingTimeInterval(5)) &&
                    !launch.canLaunch(starter, current: menu, previous: menu, at: t0.addingTimeInterval(5)) &&
                    !launch.canLaunch(starter, current: pad([16, 1]), previous: .neutral, at: t0.addingTimeInterval(5)) &&
                    !launch.canLaunch(starter, current: pad([16], axes: [0.5, 0, 0, 0, 0, 0]), previous: .neutral,
                                      at: t0.addingTimeInterval(5)),
                    "Only a fresh Menu alone with sticks at rest, on a joined controller, may start the game")
        let late = UUID()
        _ = join(&launch, [late], at: t0.addingTimeInterval(3))
        try require(!launch.canLaunch(starter, current: menu, previous: .neutral, at: t0.addingTimeInterval(3.5)) &&
                    launch.canLaunch(starter, current: menu, previous: .neutral, at: t0.addingTimeInterval(4.1)),
                    "Any new join must restart the start delay")
        launch.suspend()
        try require(!launch.canLaunch(starter, current: menu, previous: .neutral, at: t0.addingTimeInterval(9)),
                    "A suspended (not ready) player must not start the game")
        print("PASS Menu starts only from a ready player, never within 1.0 s of any join")

        var visible = SlotBook()
        try require(visible.visibleSlotCount(waiting: 0) == 4 && visible.visibleSlotCount(waiting: 5) == 4,
                    "An empty lobby shows four cards")
        let quad = (0..<4).map { _ in UUID() }
        _ = join(&visible, Array(quad.prefix(3)))
        try require(visible.visibleSlotCount(waiting: 1) == 4, "Three players and a waiting controller fit in four cards")
        _ = join(&visible, [quad[3]])
        try require(visible.visibleSlotCount(waiting: 0) == 4 && visible.visibleSlotCount(waiting: 1) == 8,
                    "Four players show four cards until a fifth controller is waiting")
        var highSpot = SlotBook()
        let five = (0..<5).map { _ in UUID() }
        _ = join(&highSpot, five)
        highSpot.disconnect(five[4])
        for id in five.prefix(4) { highSpot.disconnect(id) }
        highSpot.clear(0); highSpot.clear(1); highSpot.clear(2); highSpot.clear(3)
        try require(highSpot.visibleSlotCount(waiting: 0) == 8, "A held P5 must keep eight cards visible")
        print("PASS four cards until a fifth spot is needed")

        var session = SlotBook()
        let s1 = UUID(), s2 = UUID(), s3 = UUID(), s4 = UUID()
        _ = join(&session, [s1, s2, s3])
        for id in [s1, s2, s3] { session.update(id, sample: .neutral); session.update(id, sample: pad([1])) }
        session.beginSession(profiles: 3)
        try require(session.capacity == 3 && join(&session, [s4]) == [.full],
                    "A session launched with fewer profiles must not hand out spots the game cannot see")
        try require(SlotBook.joinMessage([.full], capacity: session.capacity)?.hasPrefix("All 3 spots are taken.") == true,
                    "Full message must use the session's spot count")
        try require(SlotBook.joinMessage([.full], capacity: 3, running: true) ==
                    "All 3 spots in this game are taken. To add players, quit the game with ⌘Q, then press A." &&
                    SlotBook.joinMessage([.joined(1), .full], capacity: 3, running: true) ==
                    "P2 joined. Let go of all buttons to get ready. All 3 spots in this game are taken. To add players, quit the game with ⌘Q, then press A.",
                    "Mid-game the full message must say how to add players (View only works in the lobby)")
        session.disconnect(s2)
        let rejoin = UUID()
        try require(join(&session, [rejoin, s4], orders: [20, 21]) == [.reclaimed(1), .full] &&
                    session.slots[1].connection == rejoin && session.slots[3].connection == nil,
                    "Mid-game a dropped player's held spot can be retaken, but no spot beyond the launched count")
        session.endSession()
        try require(session.capacity == 8 && join(&session, [s4]) == [.joined(3)], "After the game, all eight spots open again")
        session.beginSession(profiles: 0)
        try require(session.capacity == 1, "Session profiles clamp to at least one")
        session.beginSession(profiles: 12)
        try require(session.capacity == 8, "Session profiles clamp to eight")
        print("PASS the session spot count follows the launched profile count, and resets after the game")

        let launcherPID: pid_t = 100, gamePID: pid_t = 200, otherPID: pid_t = 300
        let lobbyFront = LobbyFocus(frontmostPID: launcherPID, launcherPID: launcherPID, gamePID: nil, running: false, hasControllers: true)
        let lobbyAway = LobbyFocus(frontmostPID: otherPID, launcherPID: launcherPID, gamePID: nil, running: false, hasControllers: true)
        let gameFront = LobbyFocus(frontmostPID: gamePID, launcherPID: launcherPID, gamePID: gamePID, running: true, hasControllers: true)
        let gameAway = LobbyFocus(frontmostPID: otherPID, launcherPID: launcherPID, gamePID: gamePID, running: true, hasControllers: true)
        let gameUnderLobby = LobbyFocus(frontmostPID: launcherPID, launcherPID: launcherPID, gamePID: gamePID, running: true, hasControllers: true)
        let nobody = LobbyFocus(frontmostPID: nil, launcherPID: launcherPID, gamePID: nil, running: false, hasControllers: true)
        let noPads = LobbyFocus(frontmostPID: gamePID, launcherPID: launcherPID, gamePID: gamePID, running: true, hasControllers: false)
        try require(lobbyFront.allowsActions && !lobbyFront.inputLive && lobbyFront.fastPoll && !lobbyFront.latencyCritical,
                    "The lobby in front must take controller actions at the fast poll")
        try require(!lobbyAway.allowsActions && !lobbyAway.fastPoll && !nobody.allowsActions,
                    "Lobby actions must need the lobby in front")
        try require(gameFront.allowsActions && gameFront.inputLive && gameFront.fastPoll && gameFront.latencyCritical,
                    "Mid-game joins and live input with the game in front")
        try require(gameAway.inputLive && gameAway.fastPoll && gameAway.latencyCritical && !gameAway.allowsActions,
                    "Another app in front must not freeze players while the game runs")
        try require(gameUnderLobby.inputLive && gameUnderLobby.allowsActions, "Clicking the lobby mid-game must not freeze players")
        try require(!noPads.fastPoll && !noPads.latencyCritical && noPads.inputLive, "No controllers, no fast poll")
        print("PASS focus policy: live input whenever the game runs; lobby actions only with the lobby (or game) in front")

        let faceA = PadSample(buttons: 1 << 1)
        try require(LobbyFingerprint.waiting(sample: faceA, active: false, running: false) !=
                    LobbyFingerprint.waiting(sample: .neutral, active: false, running: false) &&
                    LobbyFingerprint.waiting(sample: faceA, active: true, running: true) ==
                    LobbyFingerprint.waiting(sample: .neutral, active: false, running: true),
                    "Waiting controllers' face buttons must redraw the lobby only while it is showing")
        let joinedState = SlotState(connection: UUID(), reserved: true, ready: true, sample: .neutral)
        try require(LobbyFingerprint.slot(0, joinedState, sample: faceA, active: false, running: false) !=
                    LobbyFingerprint.slot(0, joinedState, sample: .neutral, active: false, running: false) &&
                    LobbyFingerprint.slot(0, joinedState, sample: faceA, active: true, running: true) ==
                    LobbyFingerprint.slot(0, joinedState, sample: .neutral, active: false, running: true) &&
                    LobbyFingerprint.slot(0, joinedState, sample: nil, active: false, running: false) !=
                    LobbyFingerprint.slot(1, joinedState, sample: nil, active: false, running: false),
                    "Joined cards' face buttons must redraw the lobby only while it is showing")
        print("PASS face-button glyphs redraw the lobby only while no game runs")

        try testPreferencesMigration()
        try testWindowChrome()
    }

    static func testTriggerLatch() throws {
        func run(_ latch: inout TriggerLatch, _ values: [Float]) -> [Bool] { values.map { latch.step($0) } }
        func flips(_ states: [Bool], from start: Bool) -> Int { zip([start] + states, states).filter { $0 != $1 }.count }
        var seed: UInt64 = 0x5EED_C0C4
        /// Deterministic noise in -amplitude...amplitude.
        func noise(_ amplitude: Float) -> Float {
            seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return (Float(seed >> 40) / Float(1 << 24) * 2 - 1) * amplitude
        }
        func pressed(at value: Float) -> TriggerLatch {
            var latch = TriggerLatch()
            _ = latch.step(value)
            return latch
        }

        try require(TriggerLatch.pressAt == 0.20 && TriggerLatch.repressRise == 0.15 && TriggerLatch.releaseAt == 0.10 &&
                    TriggerLatch.releaseDrop == 0.20 && TriggerLatch.releaseDropCeiling == 0.5 &&
                    TriggerLatch.releaseDropAboveCeiling == 0.08,
                    "Trigger latch parameters changed")
        var ramp = TriggerLatch()
        let rampStates = run(&ramp, (0...100).map { Float($0) / 100 })
        try require(rampStates.firstIndex(of: true) == 20 && flips(rampStates, from: false) == 1,
                    "A slow pull from rest must press exactly at 0.20 and stay pressed to the end")
        var shy = TriggerLatch()
        try require(run(&shy, [0.05, 0.10, 0.15, 0.19, 0.12, 0.19, 0.0, 0.19]).allSatisfy { !$0 },
                    "Travel below 0.20 from rest must never press")
        var snap = TriggerLatch()
        try require(snap.step(1) && !snap.step(0) && snap.step(1), "A full pull or let-go inside one report must flip at once")
        try require(!TriggerLatch().pressed && !pressed(at: .nan).pressed && pressed(at: 7).pressed && !pressed(at: -3).pressed,
                    "Out-of-range travel must be clamped (NaN counts as rest)")
        print("PASS trigger latch: ramps press at 0.20 from rest, never below, and a one-report pull flips at once")

        var nearPress = TriggerLatch()
        let pressNoise = run(&nearPress, (0..<2000).map { _ in 0.20 + noise(0.04) })
        var nearRelease = pressed(at: 1)
        let releaseNoise = run(&nearRelease, (0..<2000).map { _ in 0.10 + noise(0.04) })
        var nearHalf = pressed(at: 1)
        let halfNoise = run(&nearHalf, (0..<2000).map { _ in 0.50 + noise(0.04) })
        var fullWobble = pressed(at: 1)
        let fullHold = run(&fullWobble, (0..<2000).map { _ in 0.85 + noise(0.10) })
        var lightWobble = pressed(at: 0.30)
        let lightHold = run(&lightWobble, (0..<2000).map { _ in 0.30 + noise(0.06) })
        var barely = pressed(at: 0.22)
        let barelyHold = run(&barely, (0..<2000).map { _ in 0.22 + noise(0.05) })
        var halfWobble = pressed(at: 0.52)
        let halfHold = run(&halfWobble, (0..<2000).map { _ in 0.52 + noise(0.03) })
        try require(flips(pressNoise, from: false) == 1 && flips(releaseNoise, from: true) == 1 &&
                    flips(halfNoise, from: true) == 1, "Noise around 0.20, 0.10 or 0.5 must flip the latch once, never chatter")
        try require(fullHold.allSatisfy { $0 } && lightHold.allSatisfy { $0 } && barelyHold.allSatisfy { $0 } &&
                    halfHold.allSatisfy { $0 },
                    "Finger wobble while holding shield (full, just past half, light or barely pressed) must never drop it")
        print("PASS trigger latch: no chatter from noise around 0.10, 0.20 or 0.5; wobble on full and light holds keeps shield")

        for peak in [Float(0.75), 0.8, 0.9, 1.0] {
            for step in [Float(0.003), 0.01, 0.05, 0.13] {
                var values: [Float] = stride(from: Float(0), through: peak, by: 0.1).map { $0 } + [peak]
                var value = peak
                while value > 0 {
                    value = max(0, value - step)
                    values.append(value)
                }
                var latch = TriggerLatch()
                let states = run(&latch, values)
                let oldStates = values.map { $0 > 0.5 }
                guard let press = states.firstIndex(of: true), let oldPress = oldStates.firstIndex(of: true),
                      let release = states[press...].firstIndex(of: false),
                      let oldRelease = oldStates[oldPress...].firstIndex(of: false) else {
                    throw LobbyError("A full pull to \(peak) must press and then release")
                }
                try require(press <= oldPress && release <= oldRelease && flips(states, from: false) == 2,
                            "From a full press (\(peak), let go by \(step)) the latch must press no later and release no later than the old 0.5 threshold")
                var noisy = TriggerLatch()
                let noisyStates = run(&noisy, values.map { max(0, $0 + noise(0.03)) })
                try require(flips(noisyStates, from: false) == 2, "A noisy slow let-go must not flicker shield back on")
            }
        }
        print("PASS trigger latch: full press then slow or noisy release lets go no later than the old 0.5 threshold, once")

        // Light presses (a parry tap to 0.52-0.68): the price of the hysteresis is pinned on purpose. Past 0.58
        // the let-go is the old one; from 0.50-0.58 it waits for a 0.08 fall, so at most 0.08 (+ one report) later.
        for peak in [Float(0.52), 0.55, 0.58, 0.6, 0.62, 0.64, 0.66, 0.68] {
            for step in [Float(0.003), 0.01, 0.05] {
                var values: [Float] = stride(from: Float(0), through: peak, by: 0.1).map { $0 } + [peak]
                var value = peak
                while value > 0 {
                    value = max(0, value - step)
                    values.append(value)
                }
                var latch = TriggerLatch()
                let states = run(&latch, values)
                let oldStates = values.map { $0 > 0.5 }
                guard let press = states.firstIndex(of: true), let oldPress = oldStates.firstIndex(of: true),
                      let release = states[press...].firstIndex(of: false),
                      let oldRelease = oldStates[oldPress...].firstIndex(of: false) else {
                    throw LobbyError("A light pull to \(peak) must press and then release")
                }
                let releasedAt = values[release], expected = min(0.5, peak - TriggerLatch.releaseDropAboveCeiling)
                try require(press <= oldPress && flips(states, from: false) == 2 &&
                            releasedAt <= expected && values[release - 1] > expected &&
                            releasedAt >= 0.5 - TriggerLatch.releaseDropAboveCeiling - step - 0.0001 &&
                            (peak <= 0.58 || release <= oldRelease),
                            "A light pull to \(peak) let go by \(step) must release at \(expected): the old 0.5 past 0.58, at most 0.08 later below")
            }
        }
        // A re-press after a partial let-go needs a 0.15 rise from the lowest point: from 0.35-0.49 that is up to
        // 0.14 later than the old 0.5, which is what keeps a finger hovering around 0.5 from flickering shield.
        for trough in [Float(0.35), 0.40, 0.45, 0.49] {
            var latch = pressed(at: 1)
            let down = run(&latch, stride(from: Float(1), through: trough, by: -0.01).map { $0 } + [trough])
            let rise = (1...40).map { min(1, trough + Float($0) / 100) }
            let up = run(&latch, rise)
            guard let repress = up.firstIndex(of: true), let oldRepress = rise.firstIndex(where: { $0 > 0.5 }) else {
                throw LobbyError("A rise from \(trough) must re-press")
            }
            try require(down.last == false && flips(down, from: true) == 1 &&
                        rise[repress] >= trough + TriggerLatch.repressRise - 0.0001 &&
                        rise[repress] <= trough + TriggerLatch.repressRise + 0.0101 &&
                        rise[repress] - rise[oldRepress] <= 0.14 + 0.0101,
                        "After a let-go to \(trough) the re-press must come at a 0.15 rise, at most 0.14 later than 0.5")
        }
        print("PASS trigger latch: light presses let go at most 0.08 later than 0.5 (none past 0.58); re-press after a partial let-go at a 0.15 rise")

        var again = TriggerLatch()
        try require(run(&again, [1.0, 0.30, 0.55, 0.30, 0.55, 0.0]) == [true, false, true, false, true, false],
                    "A partial let-go and a fresh 0.15 rise must re-press without going back to rest")
        var taps = TriggerLatch()
        try require(run(&taps, [0, 0.8, 0, 0.8, 0, 0.8]) == [false, true, false, true, false, true],
                    "Taps on every report must each press and release")
        var stay = TriggerLatch()
        try require(run(&stay, [1.0, 0.6, 1.0, 0.55, 0.9]).allSatisfy { $0 },
                    "Easing off but staying above 0.5 must keep shield up")
        var sticky = TriggerLatch()
        try require(run(&sticky, [1.0, 0.45, 0.55, 0.45]) == [true, false, false, false],
                    "Wobble around 0.5 after a let-go must not re-press (needs a 0.15 rise)")
        print("PASS trigger latch: rapid re-press after a partial let-go, taps on every report, easing off above 0.5 keeps shield")

        var book = TriggerLatchBook()
        let pad = UUID(), other = UUID()
        func trig(_ left: Float, _ right: Float, buttons: UInt32 = 0, stick: Float = 0) -> PadSample {
            PadSample(buttons: buttons, axes: [stick, -stick, 0.25, 0, left, right])
        }
        let both = [book.latch(pad, trig(0.30, 0, buttons: 1 << 2, stick: 0.7)), book.latch(pad, trig(0.30, 0.15)),
                    book.latch(pad, trig(0.05, 0.25)), book.latch(pad, trig(0, 0.12)), book.latch(pad, trig(0, 0.05))]
        try require(both.map { [$0.axes[4], $0.axes[5]] } == [[1, 0], [1, 0], [0, 1], [0, 1], [0, 0]],
                    "Left and right triggers must latch independently")
        try require(both[0].buttons == 1 << 2 && both[0].axes.prefix(4) == [0.7, -0.7, 0.25, 0],
                    "Latching must leave buttons and sticks untouched")
        try require(book.latch(other, trig(0.15, 0.15)).axes.suffix(2) == [0, 0],
                    "Another controller's latch must start from rest")
        _ = book.latch(pad, trig(0.30, 0.30))
        try require(book.latch(pad, trig(0.15, 0.15)).axes.suffix(2) == [1, 1], "A light hold must stay pressed")
        book.reset(pad)
        try require(book.latch(pad, trig(0.15, 0.15)).axes.suffix(2) == [0, 0] && book.count == 2,
                    "After a leave or disconnect the controller's triggers must start from rest")
        book.reset(pad)
        book.reset(other)
        try require(book.count == 0, "Reset must drop the controller's latches")
        let short = PadSample(buttons: 1, axes: [0.5])
        try require(book.latch(pad, short) == short, "A malformed sample must pass through unlatched")
        print("PASS trigger latches: left and right independent, per controller, reset on leave/disconnect")

        var slots = SlotBook()
        let player = UUID()
        _ = slots.joinAll([SlotJoinRequest(connection: player, order: 0)], at: Date())
        slots.update(player, sample: .neutral)
        let token = Array(UInt8(0)..<UInt8(16))
        var latches = TriggerLatchBook()
        var oldTick: Int?, latchTick: Int?
        for (tick, rt) in [Float(0.15), 0.35, 0.55, 0.8].enumerated() {
            slots.update(player, sample: latches.latch(player, trig(0, rt)))
            let record = readSlot(slots.packet(token: token, sequence: UInt64(tick + 1), captureTime: 1, enabled: true), 0)
            try require(record.axes[4] == 0 && (record.axes[5] == 0 || record.axes[5] == 1),
                        "Packets must carry triggers as exactly 0 or 1")
            if record.axes[5] > 0.5 && latchTick == nil { latchTick = tick }
            if rt > 0.5 && oldTick == nil { oldTick = tick }
        }
        try require(latchTick == 1 && oldTick == 2, "A 4 ms-tick pull must reach the game a tick earlier than with 0.5")
        var grab = TriggerLatchBook()
        let attackWithShield = grab.latch(player, trig(0, 0.25, buttons: 1 << 2))
        slots.update(player, sample: attackWithShield)
        let grabPacket = slots.packet(token: token, sequence: 9, captureTime: 1, enabled: true)
        try require(grabPacket.count == SlotBook.packetBytes && read64(grabPacket, 0) == SlotBook.packetMagicV2 &&
                    readSlot(grabPacket, 0).buttons == 1 << 2 && readSlot(grabPacket, 0).axes[5] == 1,
                    "Shield (a quarter pull) and attack pressed together must reach the game in the same packet")
        let roll = grab.latch(player, trig(0, 0.3, stick: 0.9))
        slots.update(player, sample: roll)
        let rollRecord = readSlot(slots.packet(token: token, sequence: 10, captureTime: 1, enabled: true), 0)
        try require(rollRecord.axes[5] == 1 && rollRecord.axes[0] == 0.9, "Shield and a stick flick must arrive together")
        print("PASS shield combos: a quarter pull plus attack or stick reaches the game in one packet, a tick earlier than 0.5")
    }

    static func testHandOverWithholdsHeldButtons() throws {
        let token = Array(UInt8(0)..<UInt8(16))
        var sequence: UInt64 = 0
        func packetSlot(_ book: SlotBook, _ index: Int) -> DecodedSlot {
            sequence += 1
            return readSlot(book.packet(token: token, sequence: sequence, captureTime: 1, enabled: true), index)
        }
        let menu: UInt32 = 1 << 16, attack: UInt32 = 1 << 2, jump: UInt32 = 1 << 3

        var book = SlotBook()
        let starter = UUID(), shielder = UUID(), joiner = UUID()
        _ = book.joinAll([SlotJoinRequest(connection: starter, order: 0), SlotJoinRequest(connection: shielder, order: 1)],
                         at: Date())
        book.update(starter, sample: .neutral)
        book.update(shielder, sample: .neutral)
        // The poll that starts the game: P1 presses Menu; P2 holds attack, shield and the stick.
        let p2Holding = PadSample(buttons: attack, axes: [-0.9, 0, 0, 0, 0, 1])
        book.update(starter, sample: PadSample(buttons: menu))
        book.update(shielder, sample: p2Holding)
        book.withholdHeldButtons()
        let p1 = packetSlot(book, 0), p2 = packetSlot(book, 1)
        try require(p1.enabled && p1.buttons == 0 && p2.enabled && p2.buttons == 0 && p2.axes == [-0.9, 0, 0, 0, 0, 1],
                    "The Menu press that starts the game must not reach it; held sticks and shield must pass at once")
        try require(book.slots[0].ready && book.slots[1].ready, "Starting the game must not make anyone not ready")

        book.update(shielder, sample: p2Holding)
        try require(packetSlot(book, 1).buttons == 0, "A button held across the start stays out until released")
        book.update(shielder, sample: PadSample(buttons: attack | jump, axes: [0.5, 0, 0, 0, 0, 0]))
        let newPress = packetSlot(book, 1)
        try require(newPress.buttons == jump && newPress.axes[0] == 0.5 && newPress.axes[5] == 0,
                    "A new press, stick move or shield release must pass at once")
        book.update(shielder, sample: PadSample(buttons: jump))
        book.update(shielder, sample: PadSample(buttons: jump | attack))
        try require(packetSlot(book, 1).buttons == jump | attack, "A withheld button pressed again after release must pass")
        book.update(starter, sample: PadSample(buttons: menu | jump))
        try require(packetSlot(book, 0).buttons == jump, "Menu held since the start stays out; other buttons pass")
        book.update(starter, sample: PadSample(buttons: jump))
        book.update(starter, sample: PadSample(buttons: jump | menu))
        try require(packetSlot(book, 0).buttons == jump | menu && book.slots[0].withheld == 0,
                    "Menu pressed again after its release must reach the game")
        print("PASS hand-over: only buttons held at the start are withheld until released; sticks, shield and new presses pass")

        book.update(starter, sample: PadSample(buttons: attack))
        book.withholdHeldButtons()
        book.withholdHeldButtons()
        book.update(starter, sample: PadSample(buttons: attack | jump))
        try require(packetSlot(book, 0).buttons == jump, "A second hand-over must keep the first one's withheld buttons")
        book.disconnect(starter)
        try require(book.slots[0].withheld == 0 && !book.slots[0].ready, "A disconnect must clear withheld buttons")

        _ = book.joinAll([SlotJoinRequest(connection: joiner, order: 2)], at: Date())
        let joinedAt = book.slots.firstIndex { $0.connection == joiner }!
        book.update(joiner, sample: PadSample(buttons: attack))
        book.withholdHeldButtons()
        book.update(joiner, sample: PadSample(buttons: 0, axes: [0.9, 0, 0, 0, 0, 0]))
        try require(!book.slots[joinedAt].ready && isNeutral(packetSlot(book, joinedAt)),
                    "A brand-new join keeps the whole-pad release gate: the join press never reaches the game")
        book.update(joiner, sample: .neutral)
        book.update(joiner, sample: PadSample(buttons: attack))
        try require(book.slots[joinedAt].ready && packetSlot(book, joinedAt).buttons == attack,
                    "After the join gate opens, presses pass")
        book.update(shielder, sample: PadSample(buttons: jump, axes: [0.4, 0, 0, 0, 1, 0]))
        book.suspend()
        book.update(shielder, sample: PadSample(buttons: 0, axes: [0.4, 0, 0, 0, 0, 0]))
        try require(!book.slots[1].ready && isNeutral(packetSlot(book, 1)) && book.slots[1].withheld == 0,
                    "Stopping a game still makes whole pads neutral until everything is let go")
        print("PASS hand-over: join gate unchanged for new joins; stop paths still neutral until full release")
    }

    static func testInputTiming(root: URL) throws {
        func near(_ value: Double?, _ expected: Double, _ tolerance: Double = 0.001) -> Bool {
            value.map { abs($0 - expected) <= tolerance } ?? false
        }
        let empty = TimingHistogram()
        try require(empty.count == 0 && empty.percentile(0.5) == nil && empty.summary == "none",
                    "An empty histogram has no percentiles")
        var single = TimingHistogram()
        single.record(9.3)
        try require(single.percentile(0.5) == 9.3 && single.percentile(0.95) == 9.3 && single.maximum == 9.3 &&
                    single.summary == "p50:9.30,p95:9.30,max:9.30", "One sample must report itself exactly")
        var uniform = TimingHistogram()
        for value in 1...100 { uniform.record(Double(value)) }
        try require(near(uniform.percentile(0.5), 50) && near(uniform.percentile(0.95), 95) &&
                    uniform.percentile(1) == 100 && uniform.percentile(0) == 1 && uniform.count == 100,
                    "Percentiles of 1...100 ms must be 50 and 95")
        var bluetooth = TimingHistogram()
        for _ in 0..<80 { bluetooth.record(8) }
        for _ in 0..<15 { bluetooth.record(12) }
        for _ in 0..<5 { bluetooth.record(30) }
        try require(bluetooth.percentile(0.5) == 8 && near(bluetooth.percentile(0.95), 12) && bluetooth.maximum == 30 &&
                    bluetooth.summary == "p50:8.00,p95:12.00,max:30.00", "Clustered report intervals must read back exactly")
        var odd = TimingHistogram()
        odd.record(.nan)
        odd.record(.infinity)
        odd.record(-3)
        odd.record(5000)
        try require(odd.count == 2 && odd.minimum == 0 && odd.maximum == 5000 && odd.counts.first == 1 &&
                    odd.counts.last == 1 && odd.percentile(1) == 5000 && odd.percentile(0.5) == 0,
                    "NaN and infinity are ignored, negatives count as 0, huge values land in the last bucket")
        var spread = TimingHistogram()
        for value in [0.1, 0.25, 0.3, 0.5, 0.6, 1.5, 3, 6, 12, 20, 40, 2000] { spread.record(value) }
        let ranges = InputTiming.lateRanges.map { spread.count(above: $0.lower, atOrBelow: $0.upper) }
        try require(ranges == [2, 2, 1, 1, 1, 1, 1, 1, 2] && ranges.reduce(0, +) == spread.count,
                    "Lateness ranges must split the samples exactly, upper bounds inclusive")
        print("PASS timing histogram: exact single and clustered values, interpolated percentiles, bounds, NaN/negatives/overflow")

        var grid = PollLateness()
        try require(grid.tick(at: 1) == nil, "No lateness before the timer is scheduled")
        let base: UInt64 = 1_000_000_000, us: UInt64 = 1000
        grid.start(at: base, period: 4000 * us)
        var lateness: [UInt64] = [], missed: [Int] = []
        for at in [100, 4200, 8000, 12_300, 31_000, 32_100, 35_900, 40_050] as [UInt64] {
            guard let result = grid.tick(at: base + at * us) else { throw LobbyError("A scheduled grid must time every tick") }
            lateness.append(result.late / us)
            missed.append(result.missed)
        }
        try require(lateness == [100, 200, 0, 300, 15_000, 100, 0, 50] && missed == [0, 0, 0, 0, 3, 0, 0, 0],
                    "Tick lateness must follow the timer's own 4 ms grid, count swallowed ticks and tolerate early fires")
        grid.start(at: base + 100_000 * us, period: 100_000 * us)
        try require(grid.tick(at: base + 100_500 * us).map { [$0.late / us, UInt64($0.missed)] } == [500, 0],
                    "A rescheduled timer starts a fresh grid")
        print("PASS poll lateness: grid-relative lateness, swallowed ticks, early fires, rescheduling")

        var sends = SendOutcomes()
        for result in [-ENOENT, -ENOENT, -ECONNREFUSED, 296, -ENOBUFS, -ENOBUFS, 296, -EAGAIN] as [Int32] { sends.record(result) }
        try require(sends.sends == 8 && sends.delivered && sends.beforeFirstDelivery == [ENOENT: 2, ECONNREFUSED: 1] &&
                    sends.afterFirstDelivery == [ENOBUFS: 2, EAGAIN: 1], "Send failures must be counted by errno")
        try require(InputTiming.errors(sends.beforeFirstDelivery) == "ENOENT:2,ECONNREFUSED:1" &&
                    InputTiming.errors(sends.afterFirstDelivery) == "EAGAIN:1,ENOBUFS:2" && InputTiming.errors([:]) == "none" &&
                    InputTiming.errnoName(9999) == "errno9999", "Send failures must print by errno name")
        sends.resetWindow()
        sends.record(-ENOBUFS)
        try require(sends.sends == 1 && sends.delivered && sends.beforeFirstDelivery.isEmpty && sends.afterFirstDelivery == [ENOBUFS: 1],
                    "A new window keeps knowing the game already took packets")
        print("PASS send failures by errno, before and after the game's receiver first took a packet")

        var pull = TriggerPullTimer()
        func timed(_ raw: Float, _ pressed: Bool, _ time: Double) -> [Double?] {
            let result = pull.step(raw: raw, pressed: pressed, at: time)
            return [result.toLatch, result.toHalf]
        }
        func ms(_ values: [Double?]) -> [Double?] { values.map { $0.map { ($0 * 1000).rounded() / 1000 } } }
        try require(timed(0.6, true, 1.000) == [nil, nil], "A pull already under way when timing starts is not timed")
        try require(timed(0, false, 1.010) == [nil, nil] && timed(0.08, false, 1.020) == [nil, nil] &&
                    ms(timed(0.30, true, 1.028)) == [8, nil] && timed(0.45, true, 1.036) == [nil, nil] &&
                    ms(timed(0.70, true, 1.044)) == [nil, 24] && timed(0.90, true, 1.052) == [nil, nil],
                    "A pull is timed from its first report above 0.05 to the latch and to 0.5")
        try require(timed(0.30, false, 1.060) == [nil, nil] && timed(0.60, true, 1.068) == [nil, nil],
                    "A re-press that never went back to rest is not timed")
        try require(timed(0, false, 1.100) == [nil, nil] && ms(timed(0.8, true, 1.108)) == [0, 0],
                    "A pull inside one report takes 0 ms")
        try require([timed(0, false, 1.2), timed(0.1, false, 1.21), timed(0.15, false, 1.22), timed(0.02, false, 1.23)]
                        .allSatisfy { $0 == [nil, nil] }, "A touch that never reaches the latch is not timed")
        print("PASS trigger pull timing from first report above 0.05 to the latch and to the old 0.5")

        let start: UInt64 = 5_000_000_000
        var session = InputTimingSession(startedAt: start)
        let p1 = UUID(), spare = UUID(), gone = UUID()
        for poll in 0..<1000 {
            let now = Double(poll) * 4, lastReport = (now / 9).rounded(.down) * 9
            session.record(p1, name: "Xbox Wireless Controller", eventTimestamp: 100 + lastReport / 1000,
                           eventLatency: (now - lastReport + 1) / 1000, raw: .neutral, latched: .neutral)
        }
        session.record(p1, name: "ignored", eventTimestamp: 101, eventLatency: 0.002, raw: .neutral, latched: .neutral)
        var latches = TriggerLatchBook()
        for (index, rt) in [Float(0), 0.1, 0.3, 0.6, 0.9, 0].enumerated() {
            let raw = PadSample(buttons: 0, axes: [0, 0, 0, 0, 0, rt])
            session.record(spare, name: "DualSense; Wireless\nController", eventTimestamp: 200 + Double(index) * 0.008,
                           eventLatency: 0.001, raw: raw, latched: latches.latch(spare, raw))
        }
        session.record(gone, name: "Pro Controller", eventTimestamp: 0, eventLatency: 0, raw: .neutral, latched: .neutral)
        session.recordTick(late: 900_000 * us, missed: 225)  // the tick blocked by launch() itself
        try require(session.ticks == 0 && session.missedTicks == 0 && session.lateness.count == 0,
                    "The first tick after the game starts (late by the launch work) must not count")
        for _ in 0..<100 { session.recordTick(late: 100 * us, missed: 0) }
        session.recordTick(late: 12_000 * us, missed: 2)
        session.recordSend(-ENOENT)
        session.recordSend(296)
        session.recordSend(-ENOBUFS)
        let minute = start + InputTimingSession.window
        try require(!session.isDue(at: minute - 1) && session.isDue(at: minute), "Timing must flush once a minute")
        let lines = session.flush(at: minute, players: [p1: 0], connected: [p1, spare], reason: "periodic")
        guard lines.count == 4 else { throw LobbyError("Expected one line per controller plus the poll line: \(lines)") }
        try require(lines[0] == "00:01:00.000 |I| Launcher InputTiming: player=P1;pad=Xbox Wireless Controller;seconds=60.0;" +
                    "polls=1001;reports=446;reportMs=p50:9.00,p95:9.00,max:9.00;longGaps=1;" +
                    "eventAgeMs=p50:2.00,p95:4.00,max:4.00;triggerPulls=0;pullToLatchMs=none;pullToHalfMs=none;reason=periodic",
                    "P1's timing line changed: \(lines[0])")
        try require(lines[1].hasPrefix("00:01:00.000 |I| Launcher InputTiming: player=none;pad=DualSense_ Wireless_Controller;") &&
                    lines[1].contains("polls=6;reports=6;reportMs=p50:8.00,p95:8.00,max:8.00;") &&
                    lines[1].contains("triggerPulls=1;pullToLatchMs=p50:8.00,p95:8.00,max:8.00;pullToHalfMs=p50:16.00,p95:16.00,max:16.00;"),
                    "A controller without a spot must still be timed, trigger pulls included: \(lines[1])")
        try require(lines[2].contains("player=disconnected;pad=Pro Controller;") && lines[2].contains("reports=0;reportMs=none;"),
                    "A controller that left mid-window must be reported once: \(lines[2])")
        try require(lines[3] == "00:01:00.000 |I| Launcher PollTiming: seconds=60.0;ticks=101;missedTicks=2;" +
                    "lateMs=p50:0.10,p95:0.10,max:12.00;lateHistMs=0-0.25:100,0.25-0.5:0,0.5-1:0,1-2:0,2-4:0,4-8:0,8-16:1,16-32:0,32+:0;" +
                    "sends=3;sendErrors=ENOBUFS:1;sendErrorsBeforeFirstDelivery=ENOENT:1;reason=periodic",
                    "Poll timing line changed: \(lines[3])")
        try require(Set(session.controllers.keys) == [p1, spare] && session.controllers[p1]?.polls == 0 &&
                    session.ticks == 0 && session.sends.sends == 0 && session.windowStart == minute && session.lateness.count == 0,
                    "A flush must start a fresh window and drop disconnected controllers")
        let exitLines = session.flush(at: minute + 1_500_000_000, players: [:], connected: [], reason: "exit")
        try require(exitLines.count == 1 && exitLines[0].hasPrefix("00:01:01.500 |I| Launcher PollTiming: seconds=1.5;ticks=0;") &&
                    exitLines[0].hasSuffix("reason=exit") && session.controllers.isEmpty,
                    "The exit flush must report the partial window and skip idle controllers")
        try require(InputTiming.elapsed(3_723_456_000_000) == "01:02:03.456" && InputTiming.elapsed(0) == "00:00:00.000",
                    "Line stamps must match the game log's HH:MM:SS.mmm")
        print("PASS once-a-minute timing lines: per controller, poll and send failures, fresh windows, exit flush")

        let directory = root.appendingPathComponent("Tests/artifacts/session-log-append-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let logURL = directory.appendingPathComponent("session.log")
        FileManager.default.createFile(atPath: logURL.path, contents: nil)
        let handle = try FileHandle(forWritingTo: logURL)
        defer { try? handle.close() }
        let game = dup(handle.fileDescriptor)  // the game's stdout shares the launcher's open file
        defer { close(game) }
        try require(InputTiming.makeAppendOnly(handle.fileDescriptor) && fcntl(game, F_GETFL) & O_APPEND != 0,
                    "The session log must become append-only for the game too")
        func gameWrites(_ text: String) { _ = Array(text.utf8).withUnsafeBytes { write(game, $0.baseAddress, $0.count) } }
        gameWrites("game line 1\n")
        try require(InputTiming.append(["launcher line a", "launcher line b"], to: handle.fileDescriptor),
                    "Timing lines must be written")
        gameWrites("game line 2\n")
        try require(!InputTiming.append([], to: handle.fileDescriptor) && !InputTiming.append(["x"], to: -1),
                    "Nothing to write, or no log, must be a quiet no-op")
        let written = String(decoding: try Data(contentsOf: logURL), as: UTF8.self)
        try require(written == "game line 1\nlauncher line a\nlauncher line b\ngame line 2\n",
                    "Timing lines must land between the game's lines, never over them")
        print("PASS session log: append-only for launcher and game, one write per flush")
    }

    /// One datagram as CouchReceiver.Accept validates it (size, magic, enable flags, button mask, axis ranges).
    static func receiverAccepts(_ packet: Data, token: [UInt8]) -> Bool {
        guard packet.count == SlotBook.packetBytes, read64(packet, 0) == SlotBook.packetMagicV2,
              Array(packet[8..<24]) == token, read64(packet, 32) != 0 else { return false }
        return (0..<8).allSatisfy { index in
            let offset = SlotBook.packetHeaderBytes + index * 32
            let slot = readSlot(packet, index)
            return read32(packet, offset) <= 1 && slot.buttons & ~UInt32(0x1fffe) == 0 &&
                slot.axes.enumerated().allSatisfy { $1.isFinite && abs($1) <= 1 && ($0 < 4 || $1 >= 0) }
        }
    }

    /// Every slot of a packet as numbers (enabled, buttons, six axes), for exact comparisons.
    static func slotValues(_ packet: Data) -> [[Float]] {
        (0..<8).map { index in
            let slot = readSlot(packet, index)
            return [slot.enabled ? 1 : 0, Float(slot.buttons)] + slot.axes
        }
    }

    /// Events mode's pipe (InputSampling.swift) driven by a fake controller-state source and a fake clock, as
    /// the input queue drives it: ordering, sequence numbers, the heartbeat, other pads' state in every packet,
    /// neutral slots, the shield latch and the hand-over applied exactly as the poll applies them.
    static func testEventInput() throws {
        let token = Array(UInt8(0)..<UInt8(16))
        let ms: UInt64 = 1_000_000
        final class Sink { var packets: [Data] = [] }
        struct Sent { let time: UInt64; let packet: Data; let byEvent: Bool }
        struct Change { let time: UInt64; let pad: UUID; let raw: PadSample }
        var consistent = true
        func transport(_ sink: Sink) -> InputTransport {
            InputTransport(token: token) { packet in
                sink.packets.append(packet)
                return Int32(packet.count)
            }
        }
        /// Attached, joined and ready players with a game running.
        func playing(_ pads: [UUID], live: Bool = true) -> (InputPipe, Sink) {
            let pipe = InputPipe(), sink = Sink()
            pipe.modify { input in
                for pad in pads { input.attach(pad, name: "Fake pad", raw: .neutral, eventTimestamp: 1, eventLatency: 0) }
                _ = input.book.joinAll(pads.enumerated().map { SlotJoinRequest(connection: $1, order: UInt64($0)) }, at: Date())
                input.applyLatest()
                input.startTransport(transport(sink))
                input.live = live
            }
            return (pipe, sink)
        }
        func state(_ pipe: InputPipe, _ pad: UUID, _ raw: PadSample, at time: UInt64) -> Bool {
            pipe.modify {
                $0.receive(pad, name: "Fake pad", raw: raw, eventTimestamp: Double(time) / 1e9, eventLatency: 0.001,
                           at: time, captureTime: time + 1)
            }
        }
        /// Replays the source's states and the 4 ms heartbeat in time order (a state first at equal times).
        func replay(_ pipe: InputPipe, _ sink: Sink, _ changes: [Change], until end: UInt64) -> [Sent] {
            var sent: [Sent] = []
            var pending = changes.sorted { $0.time < $1.time }[...]
            func record(_ time: UInt64, byEvent: Bool, _ went: () -> Bool) {
                let before = sink.packets.count
                let didSend = went()
                if didSend != (sink.packets.count == before + 1) || sink.packets.count > before + 1 { consistent = false }
                if didSend, let packet = sink.packets.last { sent.append(Sent(time: time, packet: packet, byEvent: byEvent)) }
            }
            var beat: UInt64 = 0
            while beat <= end {
                while let next = pending.first, next.time <= beat {
                    pending = pending.dropFirst()
                    record(next.time, byEvent: true) { state(pipe, next.pad, next.raw, at: next.time) }
                }
                let now = beat
                record(now, byEvent: false) { pipe.modify { $0.heartbeat(at: now, captureTime: now + 1) } }
                beat += InputSampling.heartbeatPeriod
            }
            return sent
        }
        func sequences(_ sent: [Sent]) -> [UInt64] { sent.map { read64($0.packet, 24) } }

        // Events stay opt-in until real controllers show buffered states reach a background launcher promptly.
        try require(InputSampling.from(nil) == .poll && InputSampling.from("") == .poll &&
                    InputSampling.from("events") == .events && InputSampling.from(" Events\n") == .events &&
                    InputSampling.from("EVENTS") == .events && InputSampling.from(" Poll\n") == .poll &&
                    InputSampling.from("sometimes") == .poll && InputSampling.from("event") == .poll &&
                    InputSampling.default == .poll && InputSampling.defaultsKey == "inputSampling",
                    "UserDefaults inputSampling: \"events\" opts in, anything else (or nothing) is the default poll")
        try require(InputSampling.heartbeatPeriod == 4 * ms && InputSampling.heartbeatSkip == 2 * ms &&
                    InputSampling.heartbeatPeriod + InputSampling.heartbeatSkip <= 8 * ms && InputSampling.drainLimit >= 20,
                    "Heartbeat 4 ms, skipped under 2 ms after a packet: packets at most 6 ms apart")
        try require(InputSampling.sessionLine() ==
                    "00:00:00.000 |I| Launcher InputSampling: mode=events;heartbeatMs=4;heartbeatSkipMs=2;queue=userInteractive",
                    "Session log mode line changed")
        print("PASS input sampling mode: the poll by default, UserDefaults inputSampling=events opts in; heartbeat at most 6 ms apart")

        let p1 = UUID(), p2 = UUID()
        let attack = PadSample(buttons: 1 << 2), flick = PadSample(buttons: 0, axes: [0.9, 0, 0, 0, 0, 0])
        let (ordered, orderedSink) = playing([p1, p2])
        let sent = replay(ordered, orderedSink, [Change(time: 0, pad: p1, raw: attack), Change(time: 1 * ms, pad: p2, raw: flick),
                                                 Change(time: 9 * ms, pad: p1, raw: .neutral),
                                                 Change(time: 18 * ms, pad: p2, raw: .neutral)], until: 20 * ms)
        try require(sent.map { $0.time / ms } == [0, 1, 4, 8, 9, 12, 16, 18, 20] &&
                    sent.map(\.byEvent) == [true, true, false, false, true, false, false, true, false],
                    "Each state change must send at its own time; heartbeats fill the gaps and skip right after a packet")
        try require(sent.map { [readSlot($0.packet, 0).buttons, readSlot($0.packet, 1).axes[0] == 0.9 ? 1 : 0] } ==
                    [[4, 0], [4, 1], [4, 1], [4, 1], [0, 1], [0, 1], [0, 1], [0, 0], [0, 0]],
                    "Every packet must carry both players' newest state, in order")
        try require(sequences(sent) == Array(1...9) && sent.allSatisfy { read64($0.packet, 32) == $0.time + 1 } &&
                    sent.allSatisfy { readSlot($0.packet, 0).enabled && readSlot($0.packet, 1).enabled && receiverAccepts($0.packet, token: token) },
                    "Sequence numbers must rise by one per packet; capture time is the send time; every packet is valid")
        try require(ordered.read { $0.counts } == EventInputCounts(states: 4, eventSends: 4, heartbeatSends: 5, heartbeatsSkipped: 1, polledPads: 0) &&
                    consistent, "Event and heartbeat sends must be counted")
        try require(!state(ordered, p1, .neutral, at: 30 * ms) &&
                    !state(ordered, p1, PadSample(buttons: 0, axes: [0, 0, 0, 0, 0, 0.1]), at: 31 * ms) &&
                    ordered.read { $0.counts.states } == 6 && orderedSink.packets.count == 9,
                    "A state that changes nothing sent (same state, a trigger touch below the latch) sends nothing but is counted")
        let nextSession = Sink()
        ordered.modify { input in
            input.transport = nil
            input.startTransport(transport(nextSession))
        }
        try require(ordered.modify({ $0.heartbeat(at: 100 * ms, captureTime: 1) }) && read64(nextSession.packets[0], 24) == 1 &&
                    ordered.read { $0.counts.heartbeatSends } == 1,
                    "The next game's packets must be numbered from 1 again, its counts from zero")
        print("PASS events: one packet per state change at once, both players' newest state in each, sequence +1, heartbeat fills gaps")

        let (idle, idleSink) = playing([p1])
        let beats = replay(idle, idleSink, [], until: 36 * ms)
        try require(beats.count == 10 && beats.allSatisfy { !$0.byEvent } && sequences(beats) == Array(1...10) &&
                    zip(beats, beats.dropFirst()).allSatisfy { $1.time - $0.time == 4 * ms } &&
                    Set(beats.map { slotValues($0.packet) }).count == 1 && readSlot(beats[0].packet, 0).enabled,
                    "With nobody moving, the heartbeat must still send every 4 ms (the game's 150 ms freshness rule)")
        print("PASS events heartbeat: an idle couch still gets a packet every 4 ms")

        var seed: UInt64 = 0xE7E1_2026
        func random(_ below: UInt64) -> UInt64 {
            seed = seed &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return (seed >> 33) % below
        }
        let three = [UUID(), UUID(), UUID()]
        let (busy, busySink) = playing(three)
        var changes: [Change] = [], last: [UUID: PadSample] = [:], expectedEvents = 0
        var time: UInt64 = 0
        while time < 2000 * ms {
            time += random(9 * ms) + 1
            let pad = three[Int(random(3))]
            let raw = PadSample(buttons: UInt32(random(4)) << 1, axes: [Float(random(3)) / 2 - 0.5, 0, 0, 0, 0, 0])
            if raw != last[pad, default: .neutral] { expectedEvents += 1 }
            last[pad] = raw
            changes.append(Change(time: time, pad: pad, raw: raw))
        }
        let mixed = replay(busy, busySink, changes, until: 2020 * ms)
        let gaps = zip(mixed, mixed.dropFirst()).map { $1.time - $0.time }
        try require(mixed.filter(\.byEvent).count == expectedEvents && (gaps.max() ?? 0) <= InputSampling.heartbeatPeriod + InputSampling.heartbeatSkip &&
                    sequences(mixed) == Array(1...UInt64(mixed.count)) && mixed.allSatisfy { receiverAccepts($0.packet, token: token) } && consistent,
                    "Random states for 2 s: every change sent at once, packets never more than 6 ms apart, sequence strictly rising")
        print("PASS events: \(changes.count) random states over 2 s from three pads, \(expectedEvents) sends at once, largest gap \(Double(gaps.max() ?? 0) / 1e6) ms")

        let (disabled, disabledSink) = playing([p1], live: false)
        try require(state(disabled, p1, attack, at: 0) && slotValues(disabledSink.packets[0]).allSatisfy { $0.allSatisfy { $0 == 0 } },
                    "Before the game takes input (not live), every slot must be neutral")
        let (gated, gatedSink) = playing([p1])
        let late = UUID(), racer = UUID(), stranger = UUID()
        gated.modify { input in
            input.attach(late, name: "Fake pad", raw: attack, eventTimestamp: 1, eventLatency: 0)
            input.attach(racer, name: "Fake pad", raw: attack, eventTimestamp: 1, eventLatency: 0)
            input.attach(stranger, name: "Fake pad", raw: .neutral, eventTimestamp: 1, eventLatency: 0)
        }
        try require(state(gated, stranger, attack, at: 1 * ms) && isNeutral(readSlot(gatedSink.packets[0], 1)) &&
                    (1..<8).allSatisfy { isNeutral(readSlot(gatedSink.packets[0], $0)) },
                    "A controller that has not joined must reach no slot")
        _ = state(gated, racer, .neutral, at: 2 * ms)  // lets go before the lobby's tick takes its join
        gated.modify { input in
            _ = input.book.joinAll([SlotJoinRequest(connection: late, order: 5), SlotJoinRequest(connection: racer, order: 6)], at: Date())
            input.applyLatest()  // the end of the lobby tick
        }
        try require(gated.read { !$0.book.slots[1].ready && $0.book.slots[2].ready && $0.book.slots[2].connection == racer },
                    "A join whose release already arrived must be ready at once; one still held must wait")
        _ = gated.modify { $0.heartbeat(at: 10 * ms, captureTime: 1) }
        try require(isNeutral(readSlot(gatedSink.packets.last!, 1)) && readSlot(gatedSink.packets.last!, 2).enabled,
                    "The join press must never reach the game")
        _ = state(gated, late, .neutral, at: 11 * ms)
        _ = state(gated, late, attack, at: 12 * ms)
        try require(readSlot(gatedSink.packets.last!, 1).enabled && readSlot(gatedSink.packets.last!, 1).buttons == attack.buttons,
                    "After letting go, the new player's presses must pass")
        _ = state(gated, p1, attack, at: 13 * ms)
        gated.modify { $0.book.suspend() }
        _ = gated.modify { $0.heartbeat(at: 20 * ms, captureTime: 1) }
        let stopped = gatedSink.packets.last!
        try require(isNeutral(readSlot(stopped, 0)) && isNeutral(readSlot(stopped, 1)) && readSlot(stopped, 2).enabled &&
                    (0..<8).allSatisfy { readSlot(stopped, $0).buttons == 0 && readSlot(stopped, $0).axes.allSatisfy { $0 == 0 } },
                    "A stop (suspend) must pass no input; players holding a button stay neutral, one at rest is ready (as the poll)")
        _ = state(gated, p1, .neutral, at: 21 * ms)
        try require(readSlot(gatedSink.packets.last!, 0).enabled && isNeutral(readSlot(gatedSink.packets.last!, 1)),
                    "A stopped player who lets go is ready again; one still holding is not")
        gated.modify { $0.book.beginSession(profiles: 2) }
        _ = gated.modify { $0.heartbeat(at: 40 * ms, captureTime: 1) }
        try require(isNeutral(readSlot(gatedSink.packets.last!, 2)), "A slot beyond the session's profiles must be neutral")
        gated.modify { input in
            input.book.disconnect(p1)
            input.latches.reset(p1)
            input.detach(p1)
        }
        let beforeDetached = gatedSink.packets.count
        try require(!state(gated, p1, .neutral, at: 41 * ms) && gatedSink.packets.count == beforeDetached &&
                    gated.read { $0.latest[p1] == nil && $0.latestRaw[p1] == nil },
                    "A state still queued for a disconnected controller must be ignored")
        let lobby = InputPipe()
        lobby.modify { input in
            input.attach(p2, name: "Fake pad", raw: attack, eventTimestamp: 1, eventLatency: 0)
            _ = input.book.joinAll([SlotJoinRequest(connection: p2, order: 0)], at: Date())
            input.applyLatest()
        }
        try require(!state(lobby, p2, .neutral, at: 1) && lobby.read { $0.book.slots[0].ready && $0.sequence == 0 } &&
                    !lobby.modify { $0.heartbeat(at: 10 * ms, captureTime: 1) },
                    "In the lobby (no game) states still reach the spots, but nothing is sent")
        print("PASS events: not live, unjoined, join gate (incl. a release that beats the join), stop, session size and disconnect stay neutral")

        let shield = UUID()
        let (latching, latchingSink) = playing([shield])
        var reference = TriggerLatchBook()
        _ = reference.latch(shield, .neutral)
        var matches = true
        for (index, rt) in [Float(0), 0.1, 0.15, 0.25, 0.35, 0.6, 0.9, 0.5, 0.3, 0.05, 0.3, 0.12].enumerated() {
            let raw = PadSample(buttons: 0, axes: [0, 0, 0, 0, rt / 2, rt])
            _ = state(latching, shield, raw, at: UInt64(index + 1) * ms)
            let expected = reference.latch(shield, raw)
            if latching.read({ $0.latest[shield] }) != expected { matches = false }
            if let packet = latchingSink.packets.last, ![0, 1].contains(readSlot(packet, 0).axes[5]) { matches = false }
        }
        _ = state(latching, shield, PadSample(buttons: 1 << 2, axes: [0, 0, 0, 0, 0, 0.25]), at: 20 * ms)
        let grab = readSlot(latchingSink.packets.last!, 0)
        try require(matches && grab.buttons == 1 << 2 && grab.axes[5] == 1,
                    "Events must latch the triggers exactly as the TriggerLatch does; shield and attack in one packet")
        print("PASS events: shield latch identical to the poll's, a quarter pull and attack in one packet")

        // The poll and events modes, fed the same states and lobby decisions, must leave every spot and every
        // packet the same: the join gate, the hand-over, stops, leaving and the shield latch apply identically.
        enum Step { case state(Int, PadSample), join([Int]), withhold, suspend, live(Bool), leave(Int) }
        let pads = (0..<3).map { _ in UUID() }
        let pollSink = Sink(), eventSink = Sink()
        var poll = InputPipeState(), events = InputPipeState()
        var current: [UUID: PadSample] = [:]
        var pollLive = false
        for pad in pads {
            current[pad] = .neutral
            _ = poll.latches.latch(pad, .neutral)
            events.attach(pad, name: "Fake pad", raw: .neutral, eventTimestamp: 1, eventLatency: 0)
        }
        poll.startTransport(transport(pollSink))
        events.startTransport(transport(eventSink))
        func pollTick(_ op: (inout InputPipeState) -> Void = { _ in }) {
            for pad in pads { poll.book.update(pad, sample: poll.latches.latch(pad, current[pad]!)) }
            op(&poll)
            poll.send(captureTime: 1, enabled: pollLive)
        }
        var clock: UInt64 = 0
        var steps = 0, differences = 0, kinds = Set<String>()
        let buttonChoices: [UInt32] = [0, 1 << 1, 1 << 2, 1 << 2 | 1 << 3, 1 << 16, 1 << 7]
        let stickChoices: [Float] = [0, 0.9, -0.9, 0.3]
        let triggerChoices: [Float] = [0, 0.1, 0.25, 0.6, 1.0, 0.4]
        while steps < 4000 {
            steps += 1
            let roll = random(100)
            let pad = Int(random(3))
            let step: Step
            switch roll {
            case 0..<70:
                step = .state(pad, PadSample(buttons: buttonChoices[Int(random(6))], axes: [stickChoices[Int(random(4))], 0, 0,
                    stickChoices[Int(random(4))], triggerChoices[Int(random(6))], triggerChoices[Int(random(6))]]))
            case 70..<80: step = .join((0..<3).filter { _ in random(2) == 0 })
            case 80..<85: step = .withhold
            case 85..<88: step = .suspend
            case 88..<93: step = .live(random(4) != 0)
            default: step = .leave(pad)
            }
            clock += 3 * ms
            switch step {
            case let .state(index, raw):
                kinds.insert("state")
                current[pads[index]] = raw
                pollTick()
                _ = events.receive(pads[index], name: "Fake pad", raw: raw, eventTimestamp: 0, eventLatency: 0, at: clock, captureTime: 1)
            case let .join(indices):
                kinds.insert("join")
                let requests = indices.map { SlotJoinRequest(connection: pads[$0], order: UInt64($0)) }
                let at = Date(timeIntervalSince1970: TimeInterval(clock) / 1e9)
                pollTick { _ = $0.book.joinAll(requests, at: at) }
                _ = events.book.joinAll(requests, at: at)
            case .withhold:
                kinds.insert("withhold")
                pollTick { $0.book.withholdHeldButtons() }
                events.book.withholdHeldButtons()
            case .suspend:
                kinds.insert("suspend")
                pollTick { $0.book.suspend() }
                events.book.suspend()
            case let .live(on):
                kinds.insert("live")
                pollLive = on
                pollTick()
                events.live = on
            case let .leave(index):
                kinds.insert("leave")
                let id = pads[index]
                if let slot = poll.book.slots.firstIndex(where: { $0.connection == id }) {
                    pollTick { $0.resetLatches(id); $0.book.clear(slot) }
                    events.resetLatches(id)
                    events.book.clear(slot)
                } else {
                    pollTick()
                }
            }
            pollTick()  // the poll applies a lobby decision on its next tick; events mode at the end of the same tick
            events.applyLatest()
            clock += 3 * ms
            events.heartbeat(at: clock, captureTime: 1)
            let same = slotValues(pollSink.packets.last!) == slotValues(eventSink.packets.last!) &&
                zip(poll.book.slots, events.book.slots).allSatisfy {
                    $0.connection == $1.connection && $0.reserved == $1.reserved && $0.ready == $1.ready &&
                        $0.withheld == $1.withheld && $0.sample == $1.sample
                }
            if !same { differences += 1 }
        }
        try require(differences == 0 && kinds.count == 6 && eventSink.packets.allSatisfy { receiverAccepts($0, token: token) },
                    "Poll and events modes must apply the same states and lobby decisions identically (\(differences) differences)")
        print("PASS events = poll: 4000 random states, joins, hand-overs, stops, live changes and leaves leave every spot and packet identical")

        // The stage-2 poll's send, kept inline as the reference: the shared send must produce the same bytes.
        var referenceBook = SlotBook(), referenceSequence: UInt64 = 0
        let refPads = (0..<4).map { _ in UUID() }
        _ = referenceBook.joinAll(refPads.enumerated().map { SlotJoinRequest(connection: $1, order: UInt64($0)) }, at: Date())
        var shared = InputPipeState()
        let sharedSink = Sink()
        try require(shared.send(captureTime: 5, enabled: true) == nil && shared.sequence == 0,
                    "Without a transport (no game), nothing is sent and the sequence does not move")
        shared.startTransport(transport(sharedSink))
        shared.timing = InputTimingSession(startedAt: 1)
        var identical = true
        for round in 0..<200 {
            let id = refPads[Int(random(4))]
            let sample = PadSample(buttons: UInt32(random(16)) << 1, axes: [Float(random(5)) / 4, 0, 0, 0, Float(random(2)), 0])
            referenceBook.update(id, sample: round % 7 == 0 ? .neutral : sample)
            if round % 31 == 0 { referenceBook.withholdHeldButtons() }
            shared.book = referenceBook
            let captured = UInt64(round) * 4_000_000 + 7, enabled = round % 13 != 0
            referenceSequence &+= 1
            let expected = referenceBook.packet(token: token, sequence: referenceSequence, captureTime: captured, enabled: enabled)
            if shared.send(captureTime: captured, enabled: enabled) != Int32(expected.count) || sharedSink.packets.last != expected {
                identical = false
            }
        }
        try require(identical && shared.timing?.sends.sends == 200 && sharedSink.packets.count == 200,
                    "The shared send must produce exactly the stage-2 poll's packet bytes, sequence and send count")
        print("PASS poll fallback: the shared send is byte-for-byte the stage-2 poll's packet, sequence and send timing")

        var watch = BufferedInputWatch()
        let moving = (0..<10).map { _ in watch.beat(bufferedArrived: true, captureDiffers: true) }
        try require(!moving.contains(true) && !watch.polling,
                    "A stick in motion (capture() ahead of the last buffered state while states keep arriving) must stay on events")
        let quiet = [watch.beat(bufferedArrived: true, captureDiffers: true), watch.beat(bufferedArrived: false, captureDiffers: false),
                     watch.beat(bufferedArrived: false, captureDiffers: true), watch.beat(bufferedArrived: false, captureDiffers: true),
                     watch.beat(bufferedArrived: true, captureDiffers: false), watch.beat(bufferedArrived: false, captureDiffers: true),
                     watch.beat(bufferedArrived: false, captureDiffers: true), watch.beat(bufferedArrived: false, captureDiffers: false)]
        try require(!quiet.contains(true) && !watch.polling,
                    "Buffered states arriving, or capture() agreeing, must keep a controller on events")
        let switching = [watch.beat(bufferedArrived: false, captureDiffers: true), watch.beat(bufferedArrived: false, captureDiffers: true),
                         watch.beat(bufferedArrived: false, captureDiffers: true), watch.beat(bufferedArrived: true, captureDiffers: false),
                         watch.beat(bufferedArrived: false, captureDiffers: true)]
        try require(BufferedInputWatch.quietBeats == 3 && switching == [false, false, true, false, false] && watch.polling,
                    "Three heartbeats of a changed controller with no buffered state must switch it to polling, once and for good")
        print("PASS events safety net: a controller whose buffered states stop coming is polled on the input queue after three heartbeats")

        var counted = InputPipeState(), untimed = InputPipeState()
        counted.timing = InputTimingSession(startedAt: 5_000_000_000)
        counted.counts = EventInputCounts(states: 1200, eventSends: 800, heartbeatSends: 9000, heartbeatsSkipped: 3000, polledPads: 1)
        try require(counted.eventLine(at: 5_000_000_000 + InputTimingSession.window, reason: "periodic") ==
                    "00:01:00.000 |I| Launcher EventInput: seconds=60.0;states=1200;eventSends=800;heartbeatSends=9000;" +
                    "heartbeatsSkipped=3000;polledPads=1;reason=periodic" &&
                    counted.counts == EventInputCounts(states: 0, eventSends: 0, heartbeatSends: 0, heartbeatsSkipped: 0, polledPads: 1) &&
                    untimed.eventLine(at: 1, reason: "exit") == nil,
                    "Events mode's once-a-minute line changed, or its window did not restart")
        print("PASS events timing line: states, event and heartbeat sends, skipped heartbeats, polled pads")

        // Main thread and input queue at once: the lock keeps sequence numbers strictly rising and every packet whole.
        let stress = InputPipe(), stressSink = Sink()
        let stressPads = (0..<4).map { _ in UUID() }
        stress.modify { input in
            for pad in stressPads { input.attach(pad, name: "Fake pad", raw: .neutral, eventTimestamp: 1, eventLatency: 0) }
            input.startTransport(transport(stressSink))
            input.live = true
        }
        let group = DispatchGroup()
        DispatchQueue(label: "test.controller-input", qos: .userInteractive).async(group: group) {
            var now: UInt64 = 0
            for i in 0..<30_000 {
                now += 100_000
                let raw = PadSample(buttons: UInt32((i / 4) % 4) << 1, axes: [Float(i % 7) / 10, 0, 0, 0, Float(i % 5) / 4, 0])
                _ = stress.modify {
                    $0.receive(stressPads[i % 4], name: "Fake pad", raw: raw, eventTimestamp: 0, eventLatency: 0, at: now, captureTime: now)
                }
                if i % 40 == 0 { _ = stress.modify { $0.heartbeat(at: now, captureTime: now) } }
            }
        }
        DispatchQueue(label: "test.lobby").async(group: group) {
            for i in 0..<6_000 {
                let pad = stressPads[i % 4]
                switch i % 6 {
                case 0: stress.modify { input in
                    _ = input.book.joinAll([SlotJoinRequest(connection: pad, order: UInt64(i % 4))], at: Date())
                    input.applyLatest()
                }
                case 1: stress.modify { $0.book.withholdHeldButtons() }
                case 2: _ = stress.read { $0.latest.count + $0.book.slots.count }
                case 3: stress.modify { $0.book.suspend() }
                case 4: stress.modify { input in
                    if let slot = input.book.slots.firstIndex(where: { $0.connection == pad }) {
                        input.resetLatches(pad)
                        input.book.clear(slot)
                    }
                }
                default: stress.modify { $0.live = i % 12 != 5 }
                }
            }
        }
        group.wait()
        let stressSequences = stressSink.packets.map { read64($0, 24) }
        try require(stressSink.packets.count > 1000 && stressSequences == Array(1...UInt64(stressSink.packets.count)) &&
                    stressSink.packets.allSatisfy { receiverAccepts($0, token: token) } && stress.read { $0.sequence } == UInt64(stressSink.packets.count),
                    "Under concurrent lobby and input-queue use, sequence numbers must rise by one per packet and packets stay valid")
        print("PASS events under concurrency: \(stressSink.packets.count) packets from the input queue while the lobby changed spots, sequence strictly +1")
    }

    /// Events-mode wiring the CPU tests cannot reach without GameController: checks NativeLobbyApp.swift's source.
    static func testEventInputWiring(root: URL) throws {
        let source = try String(contentsOf: root.appendingPathComponent("Sources/NativeLobbyApp.swift"), encoding: .utf8)
        func body(_ signature: String) throws -> String {
            guard let start = source.range(of: signature), let open = source[start.upperBound...].firstIndex(of: "{") else {
                throw LobbyError("NativeLobbyApp.swift no longer has \(signature)")
            }
            var depth = 0
            for index in source[open...].indices {
                if source[index] == "{" { depth += 1 }
                if source[index] == "}" { depth -= 1 }
                if depth == 0 { return String(source[open...index]) }
            }
            throw LobbyError("Unbalanced braces in \(signature)")
        }
        func ordered(_ text: String, _ parts: [String]) -> Bool {
            var cursor = text.startIndex
            for part in parts {
                guard let found = text.range(of: part, range: cursor..<text.endIndex) else { return false }
                cursor = found.upperBound
            }
            return true
        }
        let eventInput = try body("final class EventInput: @unchecked Sendable")
        let attach = try body("func attach(_ pad: GCController, id: UUID, name: String, labelOrdered: Bool)")
        try require(!source.contains("@MainActor final class EventInput") &&
                    eventInput.contains("DispatchQueue(label: \"local.smash.couch.controller-input\", qos: .userInteractive)") &&
                    eventInput.contains("DispatchSource.makeTimerSource(flags: .strict, queue: queue)"),
                    "Events mode needs its own user-interactive serial queue and a strict heartbeat on it, off the main actor")
        // The heartbeat keeps the lobby poll's pace: 4 ms while a game runs or the lobby is in front, else 100 ms.
        let eventInit = try body("init(pipe: InputPipe)"), setPace = try body("func setPace(fast: Bool)")
        let schedule = try body("private func schedule(fast: Bool)")
        let paceChange = try body("if focus.fastPoll != fastPolling")
        try require(ordered(eventInit, ["heartbeat.setEventHandler", "schedule(fast: false)", "heartbeat.resume()"]) &&
                    eventInput.contains("private var fast = false") &&
                    ordered(setPace, ["queue.async", "guard fast != self.fast else { return }", "self.fast = fast", "schedule(fast: fast)"]) &&
                    ordered(schedule, ["InputSampling.heartbeatPeriod(fast: fast)", "heartbeat.schedule(deadline: first, repeating: .nanoseconds(Int(period)),",
                                       "leeway: fast ? .microseconds(250) : .milliseconds(10))",
                                       "$0.heartbeatGrid.start(at: first.uptimeNanoseconds, period: period)"]) &&
                    ordered(paceChange, ["fastPolling = focus.fastPoll", "pollGrid.start(", "eventInput?.setPace(fast: fastPolling)"]) &&
                    source.components(separatedBy: "setPace(fast:").count == 3 &&
                    source.components(separatedBy: "heartbeat.schedule(").count == 2,
                    "The heartbeat must start slow like the poll, change pace only on the input queue with its grid, and follow tick()'s fast poll")
        try require(InputSampling.heartbeatPeriod(fast: true) == 4_000_000 && InputSampling.heartbeatPeriod(fast: false) == 100_000_000 &&
                    InputSampling.idleHeartbeatPeriod + 10_000_000 < 150_000_000,
                    "Heartbeat 4 ms at the fast pace, 100 ms idle (still inside the game's 150 ms freshness rule)")
        try require(ordered(attach, ["input.capture()", "$0.attach(id,", "queue.async { self.pads[id] = entry }",
                                     "input.queue = queue", "input.inputStateQueueDepth = 20",
                                     "input.inputStateAvailableHandler = { [weak self] _ in self?.onQueue { $0.drain(id) } }"]) &&
                    eventInput.contains("queue.setSpecific(key: Self.queueKey, value: true)") &&
                    eventInput.contains("if DispatchQueue.getSpecific(key: Self.queueKey) == true { work(self) } else { queue.async { work(self) } }"),
                    "attach must seed from capture(), register the controller, then set the queue before the handler")
        let drain = try body("private func drain(_ id: UUID)"), beat = try body("private func beat()")
        let detach = try body("func detach(_ pad: GCController, id: UUID)")
        try require(drain.contains("for _ in 0..<InputSampling.drainLimit") && drain.contains("pad.input.nextInputState()") &&
                    drain.contains("!pad.watch.polling") && detach.contains("inputStateAvailableHandler = nil"),
                    "Buffered states must be drained oldest first with a bound; detach must stop the callbacks")
        try require(ordered(beat, ["drain(id)", "pad.input.capture()", "pad.watch.beat(", "if pad.watch.polling",
                                   "input.heartbeat(at: now, captureTime: captured)"]),
                    "The heartbeat must drain, watch, poll a controller whose buffered states stopped, then send")
        let initBody = try body("init(gate: LobbyGate) throws")
        try require(initBody.contains("UserDefaults.standard.string(forKey: InputSampling.defaultsKey)") &&
                    ordered(initBody, ["if sampling == .events { eventInput = EventInput(pipe: pipe) }",
                                       "for pad in GCController.controllers() { connected(pad) }"]),
                    "The mode must be read once at launch, and events mode must exist before the first controller connects")
        let tick = try body("func tick()"), connected = try body("func connected(_ pad: GCController)")
        try require(tick.contains("sampling == .events ? latest[id] ?? prior[id] ?? .neutral : sample(pad, id: id)") &&
                    tick.contains("if sampling == .poll { pipe.modify { $0.book.update(id, sample: now) } }") &&
                    ordered(tick, ["if sampling == .poll {", "pipe.modify { $0.send(captureTime: captured, enabled: focus.inputLive) }",
                                   "} else {", "input.live = focus.inputLive", "input.applyLatest()"]) &&
                    tick.contains("fastPolling, sampling == .poll"),
                    "In events mode the lobby tick must not sample, update spots per sample or send; the poll must do all three")
        try require(connected.contains("eventInput?.attach(pad, id: id,") && connected.contains("?? sample(pad, id: id)"),
                    "A new controller goes to the input queue in events mode, to the poll otherwise")
        let cleanup = try body("func cleanupTransport()")
        try require(ordered(cleanup, ["input.transport = nil", "Darwin.close(socketFD)"]) &&
                    source.components(separatedBy: "couchSend(").count == 3,
                    "Packets go out only through the pipe's transport, which is gone before the socket closes")
        let launch = try body("func launch()")
        try require(ordered(launch, ["try process.run()", "input.startTransport(transport)",
                                     "if sampling == .events { InputTiming.append([InputSampling.sessionLine()]"]),
                    "The transport starts only after the game process runs; events mode notes itself in the session log")
        let flush = try body("func flushTiming(reason: String)")
        try require(ordered(flush, ["input.eventLine(at: now, reason: reason)", "input.timing?.flush("]),
                    "Events mode's line must be taken before the timing window restarts")
        print("PASS wiring: events mode on its own queue (queue set before the handler), heartbeat at the poll's pace, safety net, poll path intact")
    }

    /// Wiring the CPU tests cannot reach without GameController: checks NativeLobbyApp.swift's source.
    static func testControllerWiring(root: URL) throws {
        let source = try String(contentsOf: root.appendingPathComponent("Sources/NativeLobbyApp.swift"), encoding: .utf8)
        func body(_ signature: String) throws -> String {
            guard let start = source.range(of: signature), let open = source[start.upperBound...].firstIndex(of: "{") else {
                throw LobbyError("NativeLobbyApp.swift no longer has \(signature)")
            }
            var depth = 0
            for index in source[open...].indices {
                if source[index] == "{" { depth += 1 }
                if source[index] == "}" { depth -= 1 }
                if depth == 0 { return String(source[open...index]) }
            }
            throw LobbyError("Unbalanced braces in \(signature)")
        }
        let sample = try body("func sample(_ pad: GCController, id: UUID)")
        let tick = try body("func tick()")
        let finished = try body("func finished(_ process: Process)")
        try require(sample.contains("input.latches.latch(id, raw)") && sample.contains("timing?.record(id,") &&
                    sample.contains("lastEventTimestamp") && sample.contains("lastEventLatency") &&
                    sample.contains("PadSample(state, labelOrdered: labelOrderedPads.contains(ObjectIdentifier(pad)))"),
                    "Every sample must latch the triggers and feed the timing")
        let disconnected = try body("func disconnected("), leave = try body("func removePlayer(")
        try require(disconnected.contains("input.latches.reset(id)") && leave.contains("input.resetLatches(id)"),
                    "Trigger latches must reset when a controller disconnects or leaves its spot")
        try require(tick.contains("book.withholdHeldButtons()") && !tick.contains("book.suspend()"),
                    "Starting the game must withhold held buttons, not make whole pads wait")
        let pipeSource = try String(contentsOf: root.appendingPathComponent("Sources/InputSampling.swift"), encoding: .utf8)
        try require(tick.contains("pipe.modify { $0.send(captureTime: captured, enabled: focus.inputLive) }") &&
                    pipeSource.contains("let sent = transport.send(packet)\n        timing?.recordSend(sent)") &&
                    !tick.contains("_ = socketPath.withCString") &&
                    tick.contains("pollGrid.tick(") && tick.contains("pollGrid.start(") && tick.contains("flushTiming(reason: \"periodic\")"),
                    "Each tick must time the poll, count couch_send results and flush once a minute")
        guard let flush = finished.range(of: "flushTiming(reason: \"exit\")"), let cleanup = finished.range(of: "cleanupTransport()") else {
            throw LobbyError("finished() must flush timing and clean up")
        }
        try require(flush.lowerBound < cleanup.lowerBound && finished.contains("book.withholdHeldButtons()") &&
                    !finished.contains("book.suspend()"),
                    "Game exit must flush timing before closing the log and withhold held buttons")
        let launch = try body("func launch()"), cleanupTransport = try body("func cleanupTransport()")
        try require(launch.contains("InputTiming.makeAppendOnly(logHandle!.fileDescriptor)") &&
                    cleanupTransport.contains("timing = nil"),
                    "The session log must be append-only and timing must stop with the transport")
        print("PASS wiring: latch and timing on every sample, reset on leave/disconnect, hand-over on start and exit")

        // Pairing: System Settings covers the lobby, and presses only count with the lobby (or the game) in front.
        func ordered(_ text: String, _ parts: [String]) -> Bool {
            var cursor = text.startIndex
            for part in parts {
                guard let found = text.range(of: part, range: cursor..<text.endIndex) else { return false }
                cursor = found.upperBound
            }
            return true
        }
        try require(SlotBook.connectedMessage("Xbox", actionsAllowed: true) == "Xbox connected. Press A to join." &&
                    SlotBook.connectedMessage("Xbox", actionsAllowed: false) == "Xbox connected. Click this window, then press A to join.",
                    "A controller that connects while the lobby is not in front must be told to click back on it first")
        let connected = try body("func connected(_ pad: GCController)"), bluetooth = try body("func openBluetoothSettings()")
        let design = try String(contentsOf: root.appendingPathComponent("Sources/CouchDesign.swift"), encoding: .utf8)
        try require(connected.contains("let focus = LobbyFocus(frontmostPID: frontmostPID, launcherPID: ProcessInfo.processInfo.processIdentifier,") &&
                    connected.contains("message = SlotBook.connectedMessage(shortName(names[id]!), actionsAllowed: focus.allowsActions)") &&
                    connected.contains("clickBackName = focus.allowsActions ? nil : shortName(names[id]!)") &&
                    ordered(tick, ["actionsWereAllowed = focus.allowsActions", "if actionsJustAllowed, let name = clickBackName {",
                                   "clickBackName = nil", "message = SlotBook.connectedMessage(name, actionsAllowed: true)"]) &&
                    ordered(bluetooth, ["NSWorkspace.shared.open(url) {", "then click back on this window and press A to join.", "return"]) &&
                    design.contains("then click back on this window and press A.\")"),
                    "Pairing guidance must say to click back on the lobby, on the panel, after the button and on connect")
        print("PASS pairing: the panel, the Bluetooth button and a connect behind System Settings all say to click back on the lobby")

        // Mid-game the game only has the spots it launched with: the lobby must not offer the others.
        let scene = try body("func lobbyScene() -> LobbyScene")
        try require(scene.contains("nextGameOnly: running && index >= book.capacity)") &&
                    scene.contains("joinsOpen: !running || book.nextOpenSlot() != nil)") &&
                    design.contains("case .open: return slot.nextGameOnly ? \"Next game\" : \"Open\"") &&
                    design.contains("case .open: return slot.nextGameOnly ? \"Opens when this game ends\" : \"Press A to join\"") &&
                    design.contains("Text(joinsOpen ? \"Press \\(family.joinLabel) to join\" : \"Joins next game\")") &&
                    design.contains("WaitingStrip(waiting: scene.waiting, joinsOpen: scene.joinsOpen,"),
                    "While a game runs, spots past its profile count and waiting controllers with no spot left must say 'next game'")
        print("PASS mid-game lobby: spots past the launched profile count and controllers with no spot left say next game")
    }

    /// The 58.2.0 preferences reader, copied so a rollback stays proven: it requires capacity 1...8.
    struct Launcher582Preferences: Codable {
        var capacity: Int
        var labels: [String]
        var layouts: [String]?
        var isValid: Bool { (1...8).contains(capacity) && labels.count == 8 && labels.allSatisfy(LobbyPreferences.validLabel) }
    }

    static func testPreferencesMigration() throws {
        // The user's file as 58.2.0 left it on 2026-10-11 (P2 on "A to jump").
        let installed = Data("""
        {
          "capacity" : 4,
          "labels" : [
            "Controller 1",
            "Controller 2",
            "Controller 3",
            "Controller 4",
            "Controller 5",
            "Controller 6",
            "Controller 7",
            "Controller 8"
          ],
          "layouts" : [
            "positions",
            "xbox_a_jump",
            "positions_x_attack",
            "positions_x_attack",
            "positions_x_attack",
            "positions_x_attack",
            "positions_x_attack",
            "positions_x_attack"
          ]
        }
        """.utf8)
        var decoded = try JSONDecoder().decode(LobbyPreferences.self, from: installed)
        try require(decoded.isValid && decoded.labels == (1...8).map { "Controller \($0)" },
                    "The installed preferences file must load with its names")
        decoded.labels[1] = "Couch Kid"
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let written = try encoder.encode(decoded)
        let object = try jsonObject(written)
        try require(Set(object.keys) == ["capacity", "labels"] && object["capacity"] as? Int == 4 &&
                    (object["labels"] as? [String])?[1] == "Couch Kid",
                    "Saved preferences must drop layouts and write back the count 58.2.0 saved")
        let rolledBack = try JSONDecoder().decode(Launcher582Preferences.self, from: written)
        try require(rolledBack.isValid && rolledBack.capacity == 4 && rolledBack.layouts == nil && rolledBack.labels[1] == "Couch Kid",
                    "A rollback to 58.2.0 must still load the new file, at the count it had before")
        for (legacy, count) in [(#"{"capacity":3,"labels":["a","b","c","d","e","f","g","h"]}"#, 3),
                                (#"{"labels":["a","b","c","d","e","f","g","h"]}"#, 1),
                                (#"{"capacity":0,"labels":["a","b","c","d","e","f","g","h"],"layouts":["turbo"]}"#, 1),
                                (#"{"capacity":"four","labels":["a","b","c","d","e","f","g","h"]}"#, 1),
                                (#"{"capacity":9,"labels":["a","b","c","d","e","f","g","h"]}"#, 1)] {
            let loaded = try JSONDecoder().decode(LobbyPreferences.self, from: Data(legacy.utf8))
            let back = try JSONDecoder().decode(Launcher582Preferences.self, from: try encoder.encode(loaded))
            try require(loaded.isValid && loaded.labels.first == "a" && back.isValid && back.capacity == count,
                        "Old player counts and layouts must be ignored without losing names; a valid old count goes back unchanged, else 1")
        }
        let fresh = try JSONDecoder().decode(Launcher582Preferences.self, from: try encoder.encode(LobbyPreferences.defaults))
        try require(fresh.isValid && fresh.capacity == 1, "A new file must give 58.2.0 its own default count, 1, not eight")
        let short = try JSONDecoder().decode(LobbyPreferences.self, from: Data(#"{"capacity":4,"labels":["a"]}"#.utf8))
        try require(!short.isValid && !LobbyPreferences(labels: Array(repeating: " ", count: 8)).isValid,
                    "Malformed names must still fall back to safe defaults")
        try require(LobbyPreferences.defaults.isValid && LobbyPreferences.defaults.labels.count == 8,
                    "Default preferences must be valid")
        print("PASS preferences migrate: names kept, layouts and player count ignored, 58.2.0's own count written back for rollback")
    }

    static func testWindowChrome() throws {
        for start in [[], [.fullScreenAuxiliary], [.fullScreenNone], [.managed]] as [NSWindow.CollectionBehavior] {
            let behavior = LobbyWindowChrome.collectionBehavior(start)
            try require(behavior.contains(.fullScreenPrimary) && !behavior.contains(.fullScreenAuxiliary) &&
                        !behavior.contains(.fullScreenNone), "The lobby window must be full-screen capable")
        }
        let menus = LobbyWindowChrome.mainMenu()
        try require(menus.main.items.map(\.title) == ["Smash Couch", "View", "Window"] &&
                    menus.main.items[2].submenu === menus.windows, "Menu bar must hold the app, View and Window menus")
        let quit = menus.main.items[0].submenu?.items.first
        try require(quit?.action == #selector(NSApplication.terminate(_:)) && quit?.keyEquivalent == "q" &&
                    quit?.keyEquivalentModifierMask == [.command], "⌘Q must still quit")
        let fullScreen = menus.main.items[1].submenu?.items.first
        try require(fullScreen?.title == "Enter Full Screen" && fullScreen?.action == #selector(NSWindow.toggleFullScreen(_:)) &&
                    fullScreen?.keyEquivalent == "f" && fullScreen?.keyEquivalentModifierMask == [.control, .command] &&
                    fullScreen?.target == nil, "View > Enter Full Screen must be ⌃⌘F to the key window")
        let minimize = menus.windows.items.first
        try require(minimize?.action == #selector(NSWindow.performMiniaturize(_:)) && minimize?.keyEquivalent == "m" &&
                    minimize?.keyEquivalentModifierMask == [.command], "Window > Minimize must be ⌘M")
        func key(_ flags: NSEvent.ModifierFlags, _ characters: String) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: flags, timestamp: 0, windowNumber: 0,
                             context: nil, characters: characters, charactersIgnoringModifiers: characters,
                             isARepeat: false, keyCode: characters == "f" ? 3 : 5)!
        }
        try require(LobbyWindowChrome.isGlobeF(key([.function], "f")) && LobbyWindowChrome.isGlobeF(key([.function], "F")),
                    "Fn/Globe-F must toggle full screen")
        try require(!LobbyWindowChrome.isGlobeF(key([], "f")) && !LobbyWindowChrome.isGlobeF(key([.function, .command], "f")) &&
                    !LobbyWindowChrome.isGlobeF(key([.control, .command], "f")) && !LobbyWindowChrome.isGlobeF(key([.function], "g")) &&
                    !LobbyWindowChrome.isGlobeF(key([.function, .shift], "f")),
                    "Only Fn/Globe-F alone may be taken by the key monitor (⌃⌘F belongs to the menu)")
        print("PASS lobby window: full-screen capable, View ⌃⌘F, Fn/Globe-F, Window ⌘M, ⌘Q")
    }

    struct DecodedSlot {
        let enabled: Bool
        let buttons: UInt32
        let axes: [Float]
    }

    static func read32(_ data: Data, _ offset: Int) -> UInt32 {
        data[offset..<(offset + 4)].enumerated().reduce(UInt32(0)) { $0 | (UInt32($1.element) << UInt32($1.offset * 8)) }
    }

    static func read64(_ data: Data, _ offset: Int) -> UInt64 {
        data[offset..<(offset + 8)].enumerated().reduce(UInt64(0)) { $0 | (UInt64($1.element) << UInt64($1.offset * 8)) }
    }

    static func readSlot(_ data: Data, _ index: Int) -> DecodedSlot {
        let offset = SlotBook.packetHeaderBytes + index * 32
        return DecodedSlot(enabled: read32(data, offset) == 1,
            buttons: read32(data, offset + 4),
            axes: (0..<6).map { Float(bitPattern: read32(data, offset + 8 + $0 * 4)) })
    }

    static func isNeutral(_ slot: DecodedSlot) -> Bool {
        !slot.enabled && slot.buttons == 0 && slot.axes.allSatisfy { $0 == 0 }
    }

    static func testGate(seed: Data, root artifacts: URL) throws {
        let base = artifacts.appendingPathComponent("gate-fixture-\(UUID().uuidString)", isDirectory: true)
        let lab = base.appendingPathComponent("Lab", isDirectory: true)
        let privateRoot = lab.appendingPathComponent(LobbyGate.privateRootName, isDirectory: true)
        let data = privateRoot.appendingPathComponent("Data", isDirectory: true)
        let games = data.appendingPathComponent("games", isDirectory: true)
        let game = games.appendingPathComponent("Smash-[01006a800016e000].xci")
        let prototype = base.appendingPathComponent("prototype", isDirectory: true)
        let app = prototype.appendingPathComponent(LobbyGate.approvedNativeAppName, isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        let macOS = contents.appendingPathComponent("MacOS", isDirectory: true)
        let executable = macOS.appendingPathComponent("Ryujinx")
        let plist = contents.appendingPathComponent("Info.plist")
        let packageRoot = prototype.appendingPathComponent(LobbyGate.approvedPackageOutputDirectory, isDirectory: true)
        let manifestURL = packageRoot.appendingPathComponent("app-manifest.json")
        let configURL = data.appendingPathComponent("Config.json")
        let gateURL = privateRoot.appendingPathComponent("preview-gate.json")
        try FileManager.default.createDirectory(at: games, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: macOS, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: packageRoot, withIntermediateDirectories: true)

        var fixtureConfig = try jsonObject(seed)
        fixtureConfig["game_dirs"] = [games.path]
        let fixtureSeed = try jsonData(fixtureConfig)
        try fixtureSeed.write(to: configURL)
        try Data("synthetic game fixture".utf8).write(to: game)
        try Data("native package test executable".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        let fixtureInfoPlist: [String: Any] = [
            "CFBundleExecutable": "Ryujinx",
            "CFBundleIdentifier": LobbyGate.approvedNativeBundleIdentifier,
            "CFBundleName": LobbyGate.approvedNativeBundleName,
            "CFBundleShortVersionString": LobbyGate.approvedNativeVersion,
            "CFBundleVersion": LobbyGate.approvedNativeVersion
        ]
        let plistData = try PropertyListSerialization.data(fromPropertyList: fixtureInfoPlist,
                                                            format: .xml, options: 0)
        try plistData.write(to: plist)
        let entries: [[String: Any]] = [
            ["path": "Contents/MacOS/Ryujinx", "sha256": try LobbyGate.digest(executable)],
            ["path": "Contents/Info.plist", "sha256": try LobbyGate.digest(plist)]
        ]
        func manifestApp(files: [[String: Any]], infoPlist: [String: Any]) -> [String: Any] {
            ["name": LobbyGate.approvedNativeAppName, "path": app.path,
             "info_plist": infoPlist, "files": files]
        }
        var manifest: [String: Any] = ["app": manifestApp(files: entries, infoPlist: fixtureInfoPlist)]
        try jsonData(manifest).write(to: manifestURL)
        var gate: [String: Any] = [
            "gateVersion": 1, "approved": true, "nativeApp": app.path,
            "packageManifest": manifestURL.path,
            "packageManifestSHA256": try LobbyGate.digest(manifestURL),
            "privateData": data.path,
            "configSHA256": LobbyGate.digest(data: fixtureSeed),
            "gamePath": game.path
        ]

        func writeGate() throws { try jsonData(gate).write(to: gateURL) }
        func validate() throws -> LobbyGate {
            try LobbyGate.validate(gateURL: gateURL, root: privateRoot, labRoot: lab, prototypeRoot: prototype,
                expectedManifestSHA256: try LobbyGate.digest(manifestURL),
                expectedSeedSHA256: try LobbyGate.digest(configURL))
        }
        func accepted(_ name: String) throws {
            try writeGate()
            _ = try validate()
            print("PASS gate accepted \(name)")
        }
        func refused(_ name: String) throws {
            try writeGate()
            try expectRefusal(name) { _ = try validate() }
        }
        try accepted("fixed-path synthetic fixture")
        gate["approved"] = false
        try refused("unapproved gate")
        gate["approved"] = true

        let normalData = base.appendingPathComponent("normal/Data", isDirectory: true)
        try FileManager.default.createDirectory(at: normalData, withIntermediateDirectories: true)
        gate["privateData"] = normalData.path
        try refused("normal data path")
        gate["privateData"] = data.path

        var changedConfig = try jsonObject(fixtureSeed)
        changedConfig["version"] = 70
        try replaceConfig(changedConfig, configURL: configURL, gate: &gate)
        try refused("schema-70 seed with matching hash")
        try fixtureSeed.write(to: configURL)
        gate["configSHA256"] = LobbyGate.digest(data: fixtureSeed)

        changedConfig = try jsonObject(fixtureSeed)
        var duplicate = changedConfig["input_config"] as! [[String: Any]]
        duplicate.append(duplicate[0])
        changedConfig["input_config"] = duplicate
        try replaceConfig(changedConfig, configURL: configURL, gate: &gate)
        try refused("duplicate seed profiles with matching hash")
        try fixtureSeed.write(to: configURL)
        gate["configSHA256"] = LobbyGate.digest(data: fixtureSeed)

        changedConfig = try jsonObject(fixtureSeed)
        var wrongSlot = (changedConfig["input_config"] as! [[String: Any]])[0]
        wrongSlot["id"] = "couch-2"
        changedConfig["input_config"] = [wrongSlot]
        try replaceConfig(changedConfig, configURL: configURL, gate: &gate)
        try refused("wrong seed slot with matching hash")
        try fixtureSeed.write(to: configURL)
        gate["configSHA256"] = LobbyGate.digest(data: fixtureSeed)

        changedConfig = try jsonObject(fixtureSeed)
        var missing = (changedConfig["input_config"] as! [[String: Any]])[0]
        var buttons = missing["right_joycon"] as! [String: Any]
        buttons.removeValue(forKey: "button_x")
        missing["right_joycon"] = buttons
        changedConfig["input_config"] = [missing]
        try replaceConfig(changedConfig, configURL: configURL, gate: &gate)
        try refused("missing positional mapping with matching hash")
        try fixtureSeed.write(to: configURL)
        gate["configSHA256"] = LobbyGate.digest(data: fixtureSeed)
        try accepted("restored exact one-profile seed")

        gate["packageManifestSHA256"] = String(repeating: "0", count: 64)
        try refused("wrong package manifest hash")
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)

        var wrongInfoPlist = fixtureInfoPlist
        wrongInfoPlist["CFBundleIdentifier"] = "local.invalid.fixture"
        manifest["app"] = manifestApp(files: entries, infoPlist: wrongInfoPlist)
        try jsonData(manifest).write(to: manifestURL)
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)
        try refused("wrong native bundle identifier")

        wrongInfoPlist = fixtureInfoPlist
        wrongInfoPlist["CFBundleVersion"] = "1.3.3-metal.31"
        manifest["app"] = manifestApp(files: entries, infoPlist: wrongInfoPlist)
        try jsonData(manifest).write(to: manifestURL)
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)
        try refused("wrong native bundle version")

        manifest["app"] = manifestApp(files: entries, infoPlist: fixtureInfoPlist)
        try jsonData(manifest).write(to: manifestURL)
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)

        let extra = app.appendingPathComponent("Contents/extra.fixture")
        try Data("unlisted".utf8).write(to: extra)
        try refused("unlisted package file")
        try FileManager.default.removeItem(at: extra)

        let localConfig = macOS.appendingPathComponent("Config.json")
        try Data("{}".utf8).write(to: localConfig)
        manifest["app"] = manifestApp(files: entries + [[
            "path": "Contents/MacOS/Config.json", "sha256": try LobbyGate.digest(localConfig)
        ]], infoPlist: fixtureInfoPlist)
        try jsonData(manifest).write(to: manifestURL)
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)
        try refused("app-local Config.json")
        try FileManager.default.removeItem(at: localConfig)
        manifest["app"] = manifestApp(files: entries, infoPlist: fixtureInfoPlist)
        try jsonData(manifest).write(to: manifestURL)
        gate["packageManifestSHA256"] = try LobbyGate.digest(manifestURL)

        let linked = data.appendingPathComponent("linked-game")
        try FileManager.default.createSymbolicLink(at: linked, withDestinationURL: game)
        try refused("symlink inside private Data")
        try FileManager.default.removeItem(at: linked)
        try accepted("restored package and symlink-free Data")
        print("PASS gate fixed root, native seed, package exact-set, and symlink rules")
    }

    static func replaceConfig(_ value: [String: Any], configURL: URL, gate: inout [String: Any]) throws {
        let data = try jsonData(value)
        try data.write(to: configURL)
        gate["configSHA256"] = LobbyGate.digest(data: data)
    }

    static func testConfigTransaction(seed: Data, labels: [String], root artifacts: URL) throws {
        let directory = artifacts.appendingPathComponent("config-transaction-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let config = directory.appendingPathComponent("Config.json")
        let backup = directory.appendingPathComponent("seed-config-backup.json")
        try seed.write(to: config)
        let preparedReceipt = try NativeConfigTransaction.apply(configURL: config, backupURL: backup, seed: seed,
            expectedSeedSHA256: LobbyGate.digest(data: seed), capacity: 4, labels: labels)
        let savedBackup = try Data(contentsOf: backup)
        try require(savedBackup == seed, "Private seed backup was not byte-exact")
        let prepared = try Data(contentsOf: config)
        try require(LobbyGate.digest(data: prepared) == preparedReceipt.preparedSHA256,
                    "Atomic preparation receipt did not match the private config")
        let preparedCheck = try NativeConfigTransaction.inspectPrepared(configURL: config, seed: seed,
                                                                         receipt: preparedReceipt)
        try NativeConfigTransaction.requireLaunchable(preparedCheck)
        try require(preparedCheck.expectedSHA256 == preparedCheck.observedSHA256 && preparedCheck.exactBytesMatch,
                    "Final prepared config check did not verify exact expected bytes")

        let failedStartReceiptURL = directory.appendingPathComponent("failed-start-receipt.json")
        var failedStart = makeLaunchReceipt(directory: directory, config: config,
            prepared: preparedReceipt, verification: preparedCheck, state: "prepared", childPID: nil)
        try NativeLobbyLaunchReceiptStore.persist(failedStart, to: failedStartReceiptURL)
        try NativeConfigTransaction.restoreSeedIfUnchanged(configURL: config, seed: seed,
                                                            preparedSHA256: preparedReceipt.preparedSHA256)
        let restored = try Data(contentsOf: config)
        try require(restored == seed, "Conditional rollback failed to restore the exact seed")
        failedStart.state = "failed_before_child"
        failedStart.rollbackOutcome = "seed_restored_exactly"
        failedStart.observedConfigSHA256AtFailure = LobbyGate.digest(data: restored)
        try NativeLobbyLaunchReceiptStore.persist(failedStart, to: failedStartReceiptURL)
        let failedStartReadback = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
            from: Data(contentsOf: failedStartReceiptURL))
        try require(failedStartReadback.rollbackOutcome == "seed_restored_exactly" &&
                    failedStartReadback.childPID == nil &&
                    failedStartReadback.observedConfigSHA256AtFailure == LobbyGate.digest(data: seed),
                    "Failed-start receipt did not retain exact conditional rollback evidence")
        print("PASS exact private seed backup and failed-start conditional rollback receipt")

        let second = try NativeConfigTransaction.apply(configURL: config, backupURL: backup, seed: seed,
            expectedSeedSHA256: LobbyGate.digest(data: seed), capacity: 8, labels: labels)
        let preflight = try NativeConfigTransaction.inspectPrepared(configURL: config, seed: seed, receipt: second)
        try NativeConfigTransaction.requireLaunchable(preflight)
        let sessionReceiptURL = directory.appendingPathComponent("native-session-receipt.json")
        var sessionReceipt = makeLaunchReceipt(directory: directory, config: config,
            prepared: second, verification: preflight, state: "started", childPID: 34567)
        try NativeLobbyLaunchReceiptStore.persist(sessionReceipt, to: sessionReceiptURL)
        let startedReadback = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
            from: Data(contentsOf: sessionReceiptURL))
        let startedJSON = String(data: try Data(contentsOf: sessionReceiptURL), encoding: .utf8) ?? ""
        try require(startedReadback.expectedPreparedConfigSHA256 == second.preparedSHA256 &&
                    startedReadback.observedPreparedConfigSHA256 == second.preparedSHA256 &&
                    startedReadback.expectedNativeRoundTripConfigSHA256 == second.expectedNativeRoundTripSHA256 &&
                    startedReadback.seedConfigSHA256 == LobbyGate.digest(data: seed) &&
                    startedReadback.capacity == 8 && startedReadback.labels == labels &&
                    startedReadback.nativeAppPath.hasSuffix("Native.app") &&
                    startedReadback.packageManifestSHA256 == String(repeating: "a", count: 64) &&
                    startedReadback.privateDataPath == directory.path && startedReadback.childPID == 34567,
                    "Started session receipt omitted exact hashes, package/data identity, capacity, labels, or PID")
        try require(!startedJSON.lowercased().contains("token"), "Session receipt serialized the controller token")

        // Only a post-exit exact prepared form or the captured canonical .53
        // serializer projection may restore the seed. Pre-child rollback
        // remains exact-hash-only.
        try second.expectedNativeRoundTripBytes.write(to: config, options: .atomic)
        let exitVerification = try NativeConfigTransaction.inspectAfterChildExit(configURL: config, receipt: second)
        try require(exitVerification.isExpected &&
                    exitVerification.matchedRepresentation == "native_schema_serialized" &&
                    exitVerification.expectedNativeRoundTripSHA256 == second.expectedNativeRoundTripSHA256,
                    "Captured canonical .53 serializer output was not recognized after child exit")
        try expectRefusal("pre-child rollback rejects canonical .53 serializer output") {
            try NativeConfigTransaction.restoreSeedIfUnchanged(configURL: config, seed: seed,
                preparedSHA256: second.preparedSHA256)
        }
        let stillSerialized = try Data(contentsOf: config)
        try require(stillSerialized == second.expectedNativeRoundTripBytes,
                    "Failed-start rollback overwrote the normalized config")
        try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: config, seed: seed,
            receipt: second, verification: exitVerification)
        let afterExitSeedRestore = try Data(contentsOf: config)
        try require(afterExitSeedRestore == seed,
                    "Safe post-exit rollback did not restore the exact seed")
        sessionReceipt.state = "exited"
        sessionReceipt.observedConfigSHA256AtExit = exitVerification.observedConfigSHA256
        sessionReceipt.nativeRoundTripStatus = exitVerification.matchedRepresentation
        sessionReceipt.rollbackOutcome = "seed_restored_after_expected_native_roundtrip"
        sessionReceipt.childExitStatus = 0
        try NativeLobbyLaunchReceiptStore.persist(sessionReceipt, to: sessionReceiptURL)
        let exitReadback = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
            from: Data(contentsOf: sessionReceiptURL))
        try require(exitReadback.state == "exited" &&
                    exitReadback.expectedNativeRoundTripConfigSHA256 == second.expectedNativeRoundTripSHA256 &&
                    exitReadback.observedConfigSHA256AtExit == exitVerification.observedConfigSHA256 &&
                    exitReadback.nativeRoundTripStatus == "native_schema_serialized" &&
                    exitReadback.rollbackOutcome == "seed_restored_after_expected_native_roundtrip" &&
                    exitReadback.labels == labels,
                    "Exit receipt omitted the expected/observed native config round-trip or private labels")
        print("PASS canonical .53 config serialization is receipt-bound and safely rolled back after exact child exit")

        let third = try NativeConfigTransaction.apply(configURL: config, backupURL: backup, seed: seed,
            expectedSeedSHA256: LobbyGate.digest(data: seed), capacity: 8, labels: labels)
        let refusalReceiptURL = directory.appendingPathComponent("unexpected-exit-receipt.json")
        var refusalReceipt = makeLaunchReceipt(directory: directory, config: config,
            prepared: third, verification: try NativeConfigTransaction.inspectPrepared(
                configURL: config, seed: seed, receipt: third), state: "started", childPID: 34568)
        try NativeLobbyLaunchReceiptStore.persist(refusalReceipt, to: refusalReceiptURL)

        var supportedEdit = try jsonObject(third.expectedNativeRoundTripBytes)
        supportedEdit["audio_volume"] = 0.25
        let supportedEditBytes = try jsonData(supportedEdit)
        try supportedEditBytes.write(to: config, options: .atomic)
        let supportedMismatch = try NativeConfigTransaction.inspectAfterChildExit(configURL: config, receipt: third)
        try require(!supportedMismatch.isExpected,
                    "Unexpected supported option edit was mistaken for serializer formatting")
        try expectRefusal("post-exit supported setting change") {
            try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: config, seed: seed,
                receipt: third, verification: supportedMismatch)
        }
        let preservedSupportedEdit = try Data(contentsOf: config)
        try require(preservedSupportedEdit == supportedEditBytes,
                    "Post-exit rollback overwrote an unexpected supported setting change")
        refusalReceipt.state = "exited"
        refusalReceipt.expectedNativeRoundTripConfigSHA256 = third.expectedNativeRoundTripSHA256
        refusalReceipt.observedConfigSHA256AtExit = supportedMismatch.observedConfigSHA256
        refusalReceipt.nativeRoundTripStatus = "unexpected_config_changes"
        refusalReceipt.rollbackOutcome = "preserved_post_exit_config_discrepancy"
        try NativeLobbyLaunchReceiptStore.persist(refusalReceipt, to: refusalReceiptURL)
        let refusalReadback = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
            from: Data(contentsOf: refusalReceiptURL))
        try require(refusalReadback.observedConfigSHA256AtExit == LobbyGate.digest(data: supportedEditBytes) &&
                    refusalReadback.rollbackOutcome == "preserved_post_exit_config_discrepancy",
                    "Unexpected exit change was not recorded and preserved")
        print("PASS supported option edits after child exit are preserved")

        try third.preparedBytes.write(to: config, options: .atomic)
        var unknownEdit = try jsonObject(third.preparedBytes)
        unknownEdit["external_unknown_field"] = "preserve-me"
        let unknownEditBytes = try jsonData(unknownEdit)
        try unknownEditBytes.write(to: config, options: .atomic)
        let unknownMismatch = try NativeConfigTransaction.inspectAfterChildExit(configURL: config, receipt: third)
        try expectRefusal("post-exit unknown field addition") {
            try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: config, seed: seed,
                receipt: third, verification: unknownMismatch)
        }
        let preservedUnknownEdit = try Data(contentsOf: config)
        try require(preservedUnknownEdit == unknownEditBytes,
                    "Post-exit rollback erased an unknown field addition")
        print("PASS unknown post-exit fields are not mistaken for a canonical .53 serializer form")

        let discrepancy = Data("preserve this unexpected config".utf8)
        try discrepancy.write(to: config, options: .atomic)
        let finalCheck = try NativeConfigTransaction.inspectPrepared(configURL: config, seed: seed, receipt: third)
        var childLaunchAttempted = false
        try require(finalCheck.expectedSHA256 == third.preparedSHA256 &&
                    finalCheck.observedSHA256 == LobbyGate.digest(data: discrepancy) &&
                    !finalCheck.exactBytesMatch,
                    "Changed bytes were not detected by the immediate prepared-config check")
        try expectRefusal("final config change before child launch") {
            try NativeConfigTransaction.requireLaunchable(finalCheck)
            childLaunchAttempted = true
        }
        try require(!childLaunchAttempted, "Child launch was admitted after the final config check failed")
        refusalReceipt.state = "refused_before_child"
        refusalReceipt.childPID = nil
        refusalReceipt.expectedPreparedConfigSHA256 = finalCheck.expectedSHA256
        refusalReceipt.observedPreparedConfigSHA256 = finalCheck.observedSHA256
        refusalReceipt.observedConfigSHA256AtFailure = finalCheck.observedSHA256
        refusalReceipt.failure = "Private config changed between preparation and final admission"
        refusalReceipt.rollbackOutcome = "preserved_private_config_discrepancy"
        refusalReceipt.updatedAtUTC = ISO8601DateFormatter().string(from: Date())
        try NativeLobbyLaunchReceiptStore.persist(refusalReceipt, to: refusalReceiptURL)
        let refusedReadback = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
            from: Data(contentsOf: refusalReceiptURL))
        try require(refusedReadback.state == "refused_before_child" && refusedReadback.childPID == nil &&
                    refusedReadback.observedPreparedConfigSHA256 == LobbyGate.digest(data: discrepancy) &&
                    refusedReadback.rollbackOutcome == "preserved_private_config_discrepancy",
                    "Final-check refusal receipt did not retain the discrepancy and no-child state")
        try expectRefusal("rollback after config discrepancy") {
            try NativeConfigTransaction.restoreSeedIfUnchanged(configURL: config, seed: seed,
                                                                 preparedSHA256: third.preparedSHA256)
        }
        let preserved = try Data(contentsOf: config)
        try require(preserved == discrepancy,
                    "Conditional rollback overwrote a post-preparation discrepancy")
        print("PASS final prepared-byte gate refuses launch and preserves refusal receipt")
    }

    static func testTwoReusableConfigSessions(seed: Data, serializedByCapacity: [Int: Data],
                                               labels: [String], root artifacts: URL) throws {
        let privateRoot = artifacts.appendingPathComponent("two-session-transaction-\(UUID().uuidString)",
                                                           isDirectory: true)
        let dataRoot = privateRoot.appendingPathComponent("Data", isDirectory: true)
        try FileManager.default.createDirectory(at: dataRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let config = dataRoot.appendingPathComponent("Config.json")
        let backup = privateRoot.appendingPathComponent("seed-config-backup.json")
        try seed.write(to: config, options: .atomic)
        var telemetryPaths = Set<String>()

        for (index, capacity) in [1, 4].enumerated() {
            guard let serializer = serializedByCapacity[capacity] else {
                throw LobbyError("Missing .53 serializer fixture for repeat-session capacity \(capacity)")
            }
            let sessionID = index == 0 ? "f68c8534-f8f0-48a1-a45e-e0450f8a1d01" :
                "f68c8534-f8f0-48a1-a45e-e0450f8a1d02"
            let pid = Int32(64001 + index)
            let telemetry = try NativeSelfTelemetryFile.targetURL(sessionID: sessionID, under: dataRoot)
            try NativeSelfTelemetryFile.validateAbsentTarget(telemetry, under: dataRoot)
            try require(telemetryPaths.insert(telemetry.path).inserted,
                        "Repeated session reused a prior telemetry path")
            let lease = try NativePreviewSessionClaim.acquire(in: privateRoot, sessionID: sessionID)
            let prepared = try NativeConfigTransaction.apply(configURL: config, backupURL: backup,
                seed: seed, expectedSeedSHA256: LobbyGate.digest(data: seed), capacity: capacity, labels: labels)
            let preflight = try NativeConfigTransaction.inspectPrepared(configURL: config, seed: seed,
                receipt: prepared)
            try NativeConfigTransaction.requireLaunchable(preflight)
            let preparedObject = try jsonObject(prepared.preparedBytes)
            try require(preparedObject["remember_window_state"] as? Bool == false &&
                        preparedObject["start_fullscreen"] as? Bool == false,
                        "Each private session must disable saved geometry and config fullscreen before launch")

            let startedLease = try NativePreviewSessionClaim.recordChildStarted(in: privateRoot,
                lease: lease, childPID: pid)
            var receipt = makeLaunchReceipt(directory: privateRoot, config: config, prepared: prepared,
                verification: preflight, state: "started", childPID: pid, sessionID: sessionID)
            receipt.selfTelemetryPath = telemetry.path
            receipt.selfTelemetryChildPID = pid
            let receiptURL = privateRoot.appendingPathComponent("session-\(sessionID).launch-receipt.json")
            try NativeLobbyLaunchReceiptStore.persist(receipt, to: receiptURL)
            try expectRefusal("concurrent session during repeat-session child") {
                _ = try NativePreviewSessionClaim.acquire(in: privateRoot,
                    sessionID: "f68c8534-f8f0-48a1-a45e-e0450f8a1d03")
            }

            try serializer.write(to: config, options: .atomic)
            let serializedObject = try jsonObject(serializer)
            try require(serializedObject["remember_window_state"] as? Bool == false &&
                        serializedObject["start_fullscreen"] as? Bool == false,
                        "Actual .53 serializer re-enabled saved geometry or config fullscreen in repeat session")
            let exit = try NativeConfigTransaction.inspectAfterChildExit(configURL: config, receipt: prepared)
            try require(exit.isExpected, "Captured .53 serializer output failed exact round-trip checks")
            try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: config, seed: seed,
                receipt: prepared, verification: exit)
            let restoredSeed = try Data(contentsOf: config)
            try require(restoredSeed == seed,
                        "Repeat session did not restore the exact approved seed")

            receipt.state = "exited"
            receipt.childExitStatus = 0
            receipt.observedConfigSHA256AtExit = exit.observedConfigSHA256
            receipt.nativeRoundTripStatus = exit.matchedRepresentation
            receipt.rollbackOutcome = "seed_restored_after_expected_native_roundtrip"
            try NativeLobbyLaunchReceiptStore.persist(receipt, to: receiptURL)
            let savedReceipt = try JSONDecoder().decode(NativeLobbyLaunchReceipt.self,
                from: Data(contentsOf: receiptURL))
            try require(savedReceipt.sessionID == sessionID && savedReceipt.childPID == pid &&
                        savedReceipt.state == "exited" && savedReceipt.rollbackOutcome ==
                            "seed_restored_after_expected_native_roundtrip",
                        "Repeat-session final receipt was incomplete or detached from its lease identity")
            try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: privateRoot,
                lease: startedLease, exitedChildPID: pid)
            try require(!FileManager.default.fileExists(atPath: privateRoot
                .appendingPathComponent(NativePreviewSessionClaim.fileName).path),
                        "Completed repeat session retained its active lease")
            try NativeSelfTelemetrySelfTest.frame(pid: pid, sequence: 1, footprint: 1024,
                nonce: NativeSelfTelemetrySelfTest.nonce).write(to: telemetry, options: .atomic)
        }

        let finalSeed = try Data(contentsOf: config)
        try require(finalSeed == seed && telemetryPaths.count == 2,
                    "Two completed private sessions did not preserve seed bytes and distinct telemetry")
        print("PASS two serial private sessions with exact seed restore, final receipts, child-bound leases, and unique telemetry")
    }

    static func testStopOnlySupervisorFallback(root artifacts: URL) throws {
        let dataRoot = artifacts.appendingPathComponent("stop-only-fallback-\(UUID().uuidString)/Data",
                                                        isDirectory: true)
        try FileManager.default.createDirectory(at: dataRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let (child, output) = try launchTermIgnoringPython()
        let pid = child.processIdentifier
        let supervisor = NativeSelfTelemetrySupervisor()
        defer {
            if child.isRunning { _ = Darwin.kill(pid, SIGKILL) }
            if child.isRunning { child.waitUntilExit() }
            _ = supervisor.childExited(pid: pid)
            try? output.fileHandleForReading.close()
        }

        let inMemoryReceiptChildPID = child.processIdentifier
        let inMemoryRunning = child.isRunning
        try require(inMemoryReceiptChildPID == pid && inMemoryRunning,
                    "Post-Process.run child identity/running state was not available to the failure path")
        let invalidTelemetry = dataRoot.deletingLastPathComponent().appendingPathComponent("outside.json")
        try require(!supervisor.start(process: child, telemetryURL: invalidTelemetry,
            privateData: dataRoot, nonce: NativeSelfTelemetrySelfTest.nonce, onIncident: { _ in }),
            "Regular telemetry startup accepted a path outside private Data")

        let incident = DispatchSemaphore(value: 0)
        var incidentSummary: NativeSelfTelemetrySummary?
        try require(supervisor.startStopping(process: child, onIncident: { summary in
            incidentSummary = summary
            incident.signal()
        }), "Stop-only adoption failed without requiring a telemetry file or nonce")
        try require(incident.wait(timeout: .now() + 1) == .success &&
                    incidentSummary?.childPID == pid &&
                    incidentSummary?.stopReason == NativeSelfTelemetryStopReason.launcherFailure.rawValue,
                    "Stop-only adoption did not report the exact child and launcher failure")
        let deadline = Date().addingTimeInterval(6)
        while child.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        try require(!child.isRunning, "TERM-ignoring direct test child survived bounded TERM/KILL supervision")
        child.waitUntilExit()
        try require(child.terminationReason == .uncaughtSignal && child.terminationStatus == SIGKILL,
                    "Stop-only supervision did not escalate the exact direct child to SIGKILL")
        let summary = supervisor.childExited(pid: pid)
        try require(summary?.status == "child_exited_after_stop" && summary?.stopReason ==
                    NativeSelfTelemetryStopReason.launcherFailure.rawValue && summary?.survivorObserved == false,
                    "Stop-only child exit summary did not close its retained exact-PID lifecycle")
        print("PASS invalid regular telemetry setup adopts and KILLs one TERM-ignoring direct child without shell helpers")
    }

    static func testLeaseMismatchAfterProcessRun(seed: Data, labels: [String], root artifacts: URL) throws {
        let privateRoot = artifacts.appendingPathComponent("post-run-lease-mismatch-\(UUID().uuidString)",
                                                           isDirectory: true)
        let dataRoot = privateRoot.appendingPathComponent("Data", isDirectory: true)
        try FileManager.default.createDirectory(at: dataRoot, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let config = dataRoot.appendingPathComponent("Config.json")
        let backup = privateRoot.appendingPathComponent("seed-config-backup.json")
        try seed.write(to: config, options: .atomic)
        let sessionID = UUID().uuidString.lowercased()
        let lease = try NativePreviewSessionClaim.acquire(in: privateRoot, sessionID: sessionID)
        let prepared = try NativeConfigTransaction.apply(configURL: config, backupURL: backup, seed: seed,
            expectedSeedSHA256: LobbyGate.digest(data: seed), capacity: 1, labels: labels)
        let preflight = try NativeConfigTransaction.inspectPrepared(configURL: config, seed: seed,
            receipt: prepared)
        try NativeConfigTransaction.requireLaunchable(preflight)
        let telemetry = try NativeSelfTelemetryFile.targetURL(sessionID: sessionID, under: dataRoot)
        let nonce = NativeSelfTelemetrySelfTest.nonce
        let (child, output) = try launchTermIgnoringPython()
        let pid = child.processIdentifier
        let supervisor = NativeSelfTelemetrySupervisor()
        defer {
            if child.isRunning { _ = Darwin.kill(pid, SIGKILL) }
            if child.isRunning { child.waitUntilExit() }
            _ = supervisor.childExited(pid: pid)
            try? output.fileHandleForReading.close()
        }

        // Mirror the launcher's post-Process.run ordering: retain the child PID
        // and running state, attach regular monitoring, then attempt lease I/O.
        let receiptPIDBeforeLeaseWrite = child.processIdentifier
        let runningBeforeLeaseWrite = child.isRunning
        try require(receiptPIDBeforeLeaseWrite == pid && runningBeforeLeaseWrite,
                    "Direct child identity was not retained before the simulated lease write")
        try require(supervisor.start(process: child, telemetryURL: telemetry, privateData: dataRoot,
            nonce: nonce, onIncident: { _ in }),
            "Regular self-telemetry did not attach before the simulated lease update")

        let wrongLease = NativePreviewSessionLease(schemaVersion: lease.schemaVersion,
            sessionID: UUID().uuidString.lowercased(), launcherPID: lease.launcherPID,
            claimedAtUTC: lease.claimedAtUTC, childPID: nil)
        try expectRefusal("active-session child identity write mismatch") {
            _ = try NativePreviewSessionClaim.recordChildStarted(in: privateRoot, lease: wrongLease,
                childPID: pid)
        }
        let activeLeaseURL = privateRoot.appendingPathComponent(NativePreviewSessionClaim.fileName)
        let persistedLeaseBeforeStop = try JSONDecoder().decode(NativePreviewSessionLease.self,
            from: Data(contentsOf: activeLeaseURL))
        try require(persistedLeaseBeforeStop == lease && persistedLeaseBeforeStop.childPID == nil,
                    "Failed lease-child update overwrote or dropped the original active lease")

        var receipt = makeLaunchReceipt(directory: privateRoot, config: config, prepared: prepared,
            verification: preflight, state: "failed_after_child_start", childPID: pid, sessionID: sessionID)
        receipt.failure = "Synthetic lease identity mismatch after Process.run"
        receipt.selfTelemetryPath = telemetry.path
        receipt.selfTelemetryStatus = "monitoring"
        receipt.selfTelemetryChildPID = pid
        let receiptURL = privateRoot.appendingPathComponent("session-\(sessionID).launch-receipt.json")
        try NativeLobbyLaunchReceiptStore.persist(receipt, to: receiptURL)

        supervisor.requestStop(reason: .launcherFailure)
        let stillPrepared = try Data(contentsOf: config)
        try require(child.isRunning && stillPrepared == prepared.preparedBytes,
                    "Config was restored or child ownership lost before confirmed direct-child exit")
        let deadline = Date().addingTimeInterval(6)
        while child.isRunning && Date() < deadline { Thread.sleep(forTimeInterval: 0.05) }
        try require(!child.isRunning, "Lease-mismatch child survived bounded supervision")
        child.waitUntilExit()
        try require(child.terminationReason == .uncaughtSignal && child.terminationStatus == SIGKILL,
                    "Lease-mismatch stop did not kill the exact TERM-ignoring direct child")
        guard let summary = supervisor.childExited(pid: pid) else {
            throw LobbyError("The direct child exit was not joined to its supervisor summary")
        }
        try require(summary.status == "child_exited_after_stop" && summary.childPID == pid,
                    "The post-run failure summary did not retain the exact child identity")

        let exit = try NativeConfigTransaction.inspectAfterChildExit(configURL: config, receipt: prepared)
        try require(exit.isExpected && exit.matchedRepresentation == "exact_prepared_bytes",
                    "Config changed unexpectedly while the retained child was being stopped")
        try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: config, seed: seed,
            receipt: prepared, verification: exit)
        let restoredConfig = try Data(contentsOf: config)
        try require(restoredConfig == seed,
            "Exact seed was not restored after confirmed child exit")

        receipt.state = "exited"
        receipt.childExitStatus = child.terminationStatus
        receipt.observedConfigSHA256AtExit = exit.observedConfigSHA256
        receipt.nativeRoundTripStatus = exit.matchedRepresentation
        receipt.rollbackOutcome = "seed_restored_after_expected_native_roundtrip"
        receipt.selfTelemetryStatus = summary.status
        receipt.selfTelemetryStopReason = summary.stopReason
        try NativeLobbyLaunchReceiptStore.persist(receipt, to: receiptURL)
        try expectRefusal("lease release after failed child-identity persistence") {
            try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: privateRoot, lease: lease,
                exitedChildPID: pid)
        }
        let retainedLease = try JSONDecoder().decode(NativePreviewSessionLease.self,
            from: Data(contentsOf: activeLeaseURL))
        try require(retainedLease == lease && FileManager.default.fileExists(atPath: activeLeaseURL.path),
                    "Failed child-identity persistence did not leave the original lease fail-closed")
        try expectRefusal("new session after failed child-identity persistence") {
            _ = try NativePreviewSessionClaim.acquire(in: privateRoot,
                sessionID: UUID().uuidString.lowercased())
        }
        print("PASS post-Process.run lease mismatch uses attached supervision, preserves config until exit, and retains the unrecorded-child lease")
    }

    static func launchTermIgnoringPython() throws -> (Process, Pipe) {
        let python = URL(fileURLWithPath: "/usr/bin/python3")
        guard FileManager.default.isExecutableFile(atPath: python.path) else {
            throw LobbyError("Direct-child stop test requires /usr/bin/python3")
        }
        let output = Pipe()
        let child = Process()
        child.executableURL = python
        child.arguments = ["-c", "import os,signal; signal.signal(signal.SIGTERM, signal.SIG_IGN); os.write(1,b'READY\\n'); signal.pause()"]
        child.standardOutput = output
        child.standardError = FileHandle.nullDevice
        try child.run()
        let pid = child.processIdentifier
        try? output.fileHandleForWriting.close()
        let readySignal = DispatchSemaphore(value: 0)
        let readyLock = NSLock()
        var readyData = Data()
        DispatchQueue.global(qos: .utility).async {
            let data = output.fileHandleForReading.availableData
            readyLock.lock()
            readyData = data
            readyLock.unlock()
            readySignal.signal()
        }
        guard readySignal.wait(timeout: .now() + 5) == .success else {
            if child.isRunning { _ = Darwin.kill(pid, SIGKILL) }
            child.waitUntilExit()
            _ = readySignal.wait(timeout: .now() + 1)
            throw LobbyError("Direct Python child did not report readiness within five seconds")
        }
        readyLock.lock()
        let ready = readyData
        readyLock.unlock()
        guard String(data: ready, encoding: .utf8)?.contains("READY\n") == true else {
            if child.isRunning { _ = Darwin.kill(pid, SIGKILL) }
            child.waitUntilExit()
            throw LobbyError("Direct Python child did not install its TERM-ignore handler")
        }
        return (child, output)
    }

    static func makeLaunchReceipt(directory: URL, config: URL, prepared: PreparedConfigReceipt,
                                  verification: PreparedConfigVerification, state: String,
                                  childPID: Int32?, sessionID: String? = nil) -> NativeLobbyLaunchReceipt {
        let now = ISO8601DateFormatter().string(from: Date())
        return NativeLobbyLaunchReceipt(receiptVersion: 1,
            sessionID: sessionID ?? UUID().uuidString.lowercased(),
            createdAtUTC: now, updatedAtUTC: now, state: state,
            seedConfigSHA256: prepared.seedSHA256,
            expectedPreparedConfigSHA256: verification.expectedSHA256,
            observedPreparedConfigSHA256: verification.observedSHA256,
            observedConfigSHA256AtFailure: nil,
            expectedNativeRoundTripConfigSHA256: prepared.expectedNativeRoundTripSHA256,
            observedConfigSHA256AtExit: nil, nativeRoundTripStatus: nil,
            selfTelemetryPath: nil, selfTelemetryStatus: nil, selfTelemetryStopReason: nil,
            selfTelemetrySampleCount: nil, selfTelemetryLastSequence: nil,
            selfTelemetryLastFootprintBytes: nil, selfTelemetryPeakFootprintBytes: nil,
            selfTelemetryChildPID: nil, selfTelemetrySurvivorObserved: nil,
            capacity: prepared.capacity,
            labels: prepared.labels, nativeAppPath: directory.appendingPathComponent("Native.app").path,
            nativeExecutablePath: directory.appendingPathComponent("Native.app/Contents/MacOS/Ryujinx").path,
            packageManifestPath: directory.appendingPathComponent("app-manifest.json").path,
            packageManifestSHA256: String(repeating: "a", count: 64),
            privateDataPath: directory.path, configPath: config.path,
            gamePath: directory.appendingPathComponent("game.xci").path,
            appSignatureVerified: true, childPID: childPID, childExitStatus: nil,
            failure: nil, rollbackOutcome: nil)
    }
}
