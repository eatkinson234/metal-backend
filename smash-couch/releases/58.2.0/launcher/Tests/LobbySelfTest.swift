import Foundation
import Darwin

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
        let preferences = LobbyPreferences(capacity: 4, labels: labels)
        let preferenceRoundTrip = try JSONDecoder().decode(LobbyPreferences.self,
            from: JSONEncoder().encode(preferences))
        try require(preferenceRoundTrip.capacity == 4 && preferenceRoundTrip.labels == labels,
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
            .appendingPathComponent("Library/Application Support/Smash Couch/Runtime/native-metal-58-hostaccess-r1", isDirectory: true)
        try require(LobbyGate.privateRootName == "native-metal-controller-lobby53-preview-r1" &&
                    LobbyGate.approvedSeedConfigSHA256 == "968bee7beba874d89c57ee0f535a6a6d70724f97604a991d845d10d47ef0b21a" &&
                    LobbyGate.approvedPackageManifestSHA256 == "8836c67891e3a380a8a7fe30f8c99ee707714635ec19f94084fce4a04af014ed" &&
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
        var book = SlotBook()
        try require(book.setCapacity(8), "Could not select eight logical slots before joins")
        let ids = (0..<8).map { _ in UUID() }
        for (index, id) in ids.enumerated() {
            try require(book.join(id, at: index) == .joined(index), "Eight distinct slots did not join explicitly")
        }
        try require(book.slots.count == 8 && book.slots.allSatisfy({ $0.reserved }), "Eight explicit joins were not held in fixed slots")
        print("PASS eight distinct explicit Menu claims")

        var identicalLabelsBook = SlotBook()
        try require(identicalLabelsBook.setCapacity(2), "Could not select two slots")
        let first = UUID(), second = UUID()
        try require(identicalLabelsBook.join(first, at: 0) == .joined(0) &&
                    identicalLabelsBook.join(second, at: 1) == .joined(1),
                    "Identical labels blocked explicit independent claims")
        print("PASS duplicate labels have no role in controller ownership")

        var ambiguous = SlotBook()
        try require(ambiguous.setCapacity(2), "Could not prepare ambiguous-join case")
        let a = UUID(), b = UUID()
        let result = ambiguous.applyJoinRequests([SlotJoinRequest(connection: a, slot: 0),
                                                  SlotJoinRequest(connection: b, slot: 1)])
        try require(result == .ambiguous && ambiguous.slots.allSatisfy({ !$0.reserved }),
                    "Simultaneous join requests were partly assigned")
        print("PASS simultaneous Menu joins rejected without partial assignment")

        var collision = SlotBook()
        try require(collision.setCapacity(3), "Could not prepare slot collision case")
        let p1 = UUID(), p2 = UUID(), p3 = UUID(), replacement = UUID()
        try require(collision.join(p1, at: 0) == .joined(0), "P1 join failed")
        try require(collision.join(p2, at: 1) == .joined(1), "P2 join failed")
        try require(collision.join(p3, at: 2) == .joined(2), "P3 join failed")
        collision.disconnect(p1)
        try require(collision.join(replacement, at: 1) == .occupied(1), "Occupied P2 slot accepted a reclaim collision")
        try require(collision.join(replacement, at: 0) == .joined(0), "Explicit reserved P1 reclaim failed")
        try require(collision.slots[1].connection == p2 && collision.slots[2].connection == p3,
                    "Reclaim changed P2 or P3 ownership")
        print("PASS occupied-slot collision and explicit reserved-slot reclaim")

        var release = SlotBook()
        let releaser = UUID()
        try require(release.join(releaser, at: 0) == .joined(0), "Release-gate join failed")
        release.update(releaser, sample: PadSample(buttons: 1 << 3))
        try require(!release.slots[0].ready && release.slots[0].sample.isNeutral,
                    "Joined controller was not neutral until release")
        release.update(releaser, sample: .neutral)
        try require(release.slots[0].ready && release.slots[0].sample.isNeutral,
                    "Neutral release did not arm the joined controller")
        print("PASS join/reclaim neutral-until-release gate")

        var fixed = SlotBook()
        try require(fixed.setCapacity(8), "Could not prepare fixed-slot packet case")
        let packetIDs = (0..<8).map { _ in UUID() }
        for (index, id) in packetIDs.enumerated() {
            try require(fixed.join(id, at: index) == .joined(index), "Packet slot join failed")
            fixed.update(id, sample: .neutral)
            fixed.update(id, sample: PadSample(buttons: (1 << 3) | (1 << 1) | (1 << 4),
                                               axes: [Float(index + 1) / 10, 0, 0, 0, 0, 0]).mappedForNativeLobby())
        }
        let token = Array(UInt8(0)..<UInt8(16))
        let allPacket = fixed.packet(token: token, sequence: 7, captureTime: 1, enabled: true)
        try require(allPacket.count == SlotBook.packetBytes && read64(allPacket, 0) == SlotBook.packetMagicV2,
                    "SCINPUT2 packet header or size changed")
        for slot in 0..<8 {
            let record = readSlot(allPacket, slot)
            try require(record.enabled && record.buttons == ((1 << 1) | (1 << 3) | (1 << 4)),
                        "Default Smash standard positional mapping failed in P\(slot + 1)")
            try require(abs(record.axes[0] - Float(slot + 1) / 10) < 0.0001,
                        "P\(slot + 1) axes were serialized into the wrong slot")
        }
        print("PASS all-eight packet slot order and default positional mapping")

        fixed.disconnect(packetIDs[1])
        let gapPacket = fixed.packet(token: token, sequence: 8, captureTime: 2, enabled: true)
        try require(!readSlot(gapPacket, 1).enabled && readSlot(gapPacket, 2).enabled &&
                    fixed.slots[2].connection == packetIDs[2], "Missing P2 shifted P3 or stayed active")
        let disabledPacket = fixed.packet(token: token, sequence: 9, captureTime: 3, enabled: false)
        try require((0..<8).allSatisfy { isNeutral(readSlot(disabledPacket, $0)) },
                    "Focus-loss packet was not neutral across all eight slots")
        fixed.suspend()
        let suspendedPacket = fixed.packet(token: token, sequence: 10, captureTime: 4, enabled: true)
        try require((0..<8).allSatisfy { isNeutral(readSlot(suspendedPacket, $0)) },
                    "Focus suspension retained input or readiness")
        try require(fixed.slots[2].connection == packetIDs[2] && fixed.slots[2].reserved,
                    "Focus loss changed P3 reservation")
        print("PASS P2 disconnect preserves P3; focus loss emits neutral packets")

        var capacity = SlotBook()
        try require(capacity.setCapacity(8), "Could not prepare capacity test")
        let reserved = UUID()
        try require(capacity.join(reserved, at: 5) == .joined(5), "Could not reserve P6")
        try require(!capacity.setCapacity(4) && capacity.capacity == 8,
                    "Capacity reduction crossed an occupied slot")
        capacity.disconnect(reserved)
        try require(!capacity.setCapacity(4) && capacity.capacity == 8,
                    "Capacity reduction dropped a disconnected reservation")
        var empty = SlotBook()
        try require(empty.setCapacity(8) && empty.setCapacity(4) && empty.capacity == 4,
                    "Capacity reduction failed when no higher slots were reserved")
        try require(SlotBook.movedSelection(0, direction: -1, capacity: 4) == 3 &&
                    SlotBook.movedSelection(3, direction: 1, capacity: 4) == 0,
                    "Unassigned D-pad slot choice did not wrap within selected capacity")
        print("PASS capacity reduction preserves occupied/reserved slots; D-pad selection wraps")

        var evicting = SlotBook()
        let evictP2 = UUID(), evictP6 = UUID()
        try require(evicting.setCapacity(8) && evicting.join(evictP2, at: 1) == .joined(1) &&
                    evicting.join(evictP6, at: 5) == .joined(5), "Could not prepare eviction case")
        evicting.disconnect(evictP6)
        try require(evicting.setCapacity(4, evicting: true) == [5] && evicting.capacity == 4 &&
                    evicting.slots[5].connection == nil && !evicting.slots[5].reserved &&
                    evicting.slots[1].connection == evictP2 && evicting.slots[1].reserved,
                    "Lowering the player count must free only the slots above it")
        try require(evicting.setCapacity(1, evicting: true) == [1] && evicting.capacity == 1 &&
                    evicting.slots[1].connection == nil, "Lowering to one player must free P2")
        try require(evicting.setCapacity(0, evicting: true) == nil && evicting.capacity == 1,
                    "Zero players must be refused")
        try require(evicting.setCapacity(2, evicting: true) == [] && evicting.join(evictP2, at: 1) == .joined(1),
                    "A released controller must be able to join again")
        evicting.clear(1)
        try require(evicting.slots[1].connection == nil && !evicting.slots[1].reserved &&
                    evicting.join(evictP2, at: 1) == .joined(1), "Remove player must free the slot for a fresh join")
        print("PASS minus frees slots above the new count; remove player frees one slot; released pads rejoin")

        let axes: [Float] = [0.25, -0.5, 0.1, 0, 0.7, 0.2]
        for combo in 0..<16 {
            var buttons: UInt32 = (1 << 7) | (1 << 12) | (1 << 16)
            for (index, bit) in [1, 2, 3, 4].enumerated() where combo & (1 << index) != 0 { buttons |= 1 << UInt32(bit) }
            let sample = PadSample(buttons: buttons, axes: axes)
            try require(sample.mapped(for: .default) == sample && sample.mappedForNativeLobby() == sample,
                        "Default layout must pass every face button through (Smash standard)")
            for layout in ControllerLayout.allCases {
                let mapped = sample.mapped(for: layout)
                try require(mapped.axes == axes && mapped.buttons & ~UInt32(0b11110) == buttons & ~UInt32(0b11110),
                            "\(layout) changed a non-face control")
            }
        }
        func output(_ layout: ControllerLayout, _ face: Int) -> UInt32 {
            PadSample(buttons: 1 << UInt32(face)).mapped(for: layout).buttons
        }
        try require(output(.standard, 1) == 1 << 1 && output(.standard, 2) == 1 << 2 &&
                    output(.standard, 3) == 1 << 3 && output(.standard, 4) == 1 << 4, "Smash standard must be identity")
        try require(output(.swapped, 1) == 1 << 2 && output(.swapped, 2) == 1 << 1,
                    "Swap must put Switch A (attack/confirm) on the bottom button")
        try require(output(.southJump, 1) == 1 << 3 && output(.southJump, 2) == 1 << 1 && output(.southJump, 3) == 1 << 2,
                    "A-to-jump must jump on south, special on east, attack on west")
        for layout in ControllerLayout.allCases {
            try require(Set(layout.outputs) == [1, 2, 3, 4], "\(layout) must keep all four Switch face buttons")
        }
        try require(ControllerLayout.allCases == [.standard, .southJump, .swapped] && ControllerLayout.default == .standard,
                    "Only the three all-button layouts may remain, with Smash standard as the default")
        try require(ControllerLayout.default.next(1) == .southJump && ControllerLayout.default.next(-1) == .swapped,
                    "LB/RB layout cycling must wrap")
        let other: UInt32 = (1 << 7) | (1 << 16)
        try require(ControllerLayout.positionalFaces(fromLabelOrdered: 1 << 1 | other) == 1 << 2 | other &&
                    ControllerLayout.positionalFaces(fromLabelOrdered: 1 << 2) == 1 << 1 &&
                    ControllerLayout.positionalFaces(fromLabelOrdered: 1 << 3) == 1 << 4 &&
                    ControllerLayout.positionalFaces(fromLabelOrdered: 1 << 4) == 1 << 3 &&
                    ControllerLayout.positionalFaces(fromLabelOrdered: 0b11110) == 0b11110,
                    "Nintendo label order must convert A->right, B->bottom, X->top, Y->left")
        try require(PadSample(buttons: ControllerLayout.positionalFaces(fromLabelOrdered: 1 << 1)).mapped(for: .default).buttons == 1 << 2,
                    "A Switch pad's printed A (right) must attack under the default layout, as on a Switch")
        print("PASS three button layouts remap only face buttons and keep all four Switch buttons; default is Smash standard; Switch label order converts to positions")

        let legacyPreferences = Data(#"{"capacity":3,"labels":["a","b","c","d","e","f","g","h"]}"#.utf8)
        var decoded = try JSONDecoder().decode(LobbyPreferences.self, from: legacyPreferences)
        try require(decoded.isValid && decoded.layouts == nil && decoded.layout(2) == .default,
                    "Preferences written before layouts must stay valid with the default layout")
        decoded.setLayout(.southJump, slot: 2)
        let reloaded = try JSONDecoder().decode(LobbyPreferences.self, from: JSONEncoder().encode(decoded))
        try require(reloaded.isValid && reloaded.layout(2) == .southJump && reloaded.layout(0) == .default,
                    "Per-player layout did not persist")
        var future = reloaded
        future.layouts?[4] = "turbo"
        future.layouts?.removeLast()
        try require(future.isValid && future.layout(4) == .default && future.layout(7) == .default &&
                    future.layout(2) == .southJump,
                    "Unknown or missing layout entries must fall back to the default without resetting preferences")
        future.layouts?[5] = "positions_x_attack"
        future.layouts?[6] = "brawlhalla_style"
        try require(future.layout(5) == .standard && future.layout(6) == .standard,
                    "Saved choices of the removed Extra attack and Brawlhalla-style layouts must load as Smash standard")
        future.setLayout(.swapped, slot: 7)
        try require(future.layouts?.count == 8 && future.layout(7) == .swapped, "Short layout lists must extend on save")
        print("PASS per-player layouts persist; older preference files still load")
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
