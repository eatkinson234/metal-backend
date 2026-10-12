import Foundation
import Darwin

enum NativeLobbyConfiguration {
    static let mutableProfileKeys: Set<String> = ["id", "player_index"]

    static func validateSeed(_ data: Data) throws -> [String: Any] {
        let config = try object(data)
        try validateNativeBase(config)
        guard config["remember_window_state"] as? Bool == true else {
            throw LobbyError("Approved .53 seed must retain its remembered-window-state preference")
        }
        guard let inputs = config["input_config"] as? [[String: Any]], inputs.count == 1 else {
            throw LobbyError("Gate requires one schema-67 couch-1 seed profile")
        }
        guard inputs[0]["name"] == nil else {
            throw LobbyError("Approved .53 seed profile must not contain unsupported name metadata")
        }
        try validateProfile(inputs[0], slot: 1)
        return config
    }

    static func prepare(seed: Data, capacity: Int, labels: [String]) throws -> Data {
        guard (1...8).contains(capacity), labels.count == 8,
              labels.allSatisfy(LobbyPreferences.validLabel) else {
            throw LobbyError("Capacity or private slot labels are invalid")
        }
        let seedConfig = try validateSeed(seed)
        guard let template = (seedConfig["input_config"] as? [[String: Any]])?.first else {
            throw LobbyError("Validated seed profile is missing")
        }
        var preparedConfig = seedConfig
        var profiles: [[String: Any]] = []
        for slot in 1...capacity {
            var profile = template
            profile["id"] = "couch-\(slot)"
            profile["player_index"] = "Player\(slot)"
            // Keep user-facing slot labels in lobby-preferences.json and receipts.
            profiles.append(profile)
        }
        preparedConfig["input_config"] = profiles
        preparedConfig["start_fullscreen"] = false
        preparedConfig["remember_window_state"] = false
        // Ryujinx's in-game exit confirmation deadlocks a Cmd-Q quit (Main returns while
        // emulation threads keep running), so lobby sessions always exit without the prompt.
        preparedConfig["show_confirm_exit"] = false
        let output = try JSONSerialization.data(withJSONObject: preparedConfig,
                                                options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try validatePrepared(output, againstSeed: seed, capacity: capacity, labels: labels)
        return output
    }

    static func validatePrepared(_ data: Data, againstSeed seed: Data,
                                 capacity: Int, labels: [String]) throws {
        guard (1...8).contains(capacity), labels.count == 8,
              labels.allSatisfy(LobbyPreferences.validLabel) else {
            throw LobbyError("Prepared capacity or labels are invalid")
        }
        let seedConfig = try validateSeed(seed)
        let config = try object(data)
        try validateNativeBase(config)
        guard config["start_fullscreen"] as? Bool == false,
              config["remember_window_state"] as? Bool == false,
              config["show_confirm_exit"] as? Bool == false,
              let template = (seedConfig["input_config"] as? [[String: Any]])?.first,
              let profiles = config["input_config"] as? [[String: Any]], profiles.count == capacity else {
            throw LobbyError("Prepared config must disable saved window geometry and the exit prompt, use windowed launch, and match the selected profile capacity")
        }
        guard try canonical(withoutLobbyManagedSettings: config) == canonical(withoutLobbyManagedSettings: seedConfig) else {
            throw LobbyError("Preparation changed native config options outside input_config and the three required temporary session settings")
        }

        var ids = Set<String>()
        var playerIndices = Set<String>()
        for (offset, profile) in profiles.enumerated() {
            let slot = offset + 1
            try validateProfile(profile, slot: slot)
            guard profile["name"] == nil else {
                throw LobbyError("Prepared native controller profile must omit unsupported name metadata")
            }
            guard let id = profile["id"] as? String,
                  let player = profile["player_index"] as? String,
                  ids.insert(id).inserted, playerIndices.insert(player).inserted else {
                throw LobbyError("Prepared native profile IDs and player indices must be unique")
            }
            var actualUnchanged = profile
            var seedUnchanged = template
            for key in mutableProfileKeys {
                actualUnchanged.removeValue(forKey: key)
                seedUnchanged.removeValue(forKey: key)
            }
            guard try canonical(actualUnchanged) == canonical(seedUnchanged) else {
                throw LobbyError("Preparation changed a template field other than id, player_index, or name")
            }
        }
    }

    // The accepted .53 CPU serializer captures preserve every field in the
    // approved run63 seed and the capacity-1/4/8 prepared projections. Keep a
    // strict identity projection for present fields; do not apply historical
    // .17 deletion rules to the .53 package.
    static func expectedNativeRoundTrip(_ data: Data) throws -> Data {
        try canonical(object(data))
    }

    static func launchArguments(privateData: URL, gamePath: String) -> [String] {
        ["-r", privateData.path, "--graphics-backend", "metal", "--fullscreen", gamePath]
    }

    static func canonicalJSON(_ data: Data) throws -> Data {
        try canonical(object(data))
    }

    private static func validateNativeBase(_ config: [String: Any]) throws {
        guard config["version"] as? Int == 67,
              config["graphics_backend"] as? String == "Metal",
              config["vsync_mode"] as? Int == 0,
              config["enable_custom_vsync_interval"] as? Bool == false,
              config["docked_mode"] as? Bool == true,
              config["use_hypervisor"] as? Bool == true,
              config["use_input_global_config"] == nil else {
            throw LobbyError("Expected the exact schema-67 Metal seed policy with global-input override absent")
        }
    }

    private static func validateProfile(_ profile: [String: Any], slot: Int) throws {
        guard profile["version"] as? Int == 1,
              profile["backend"] as? String == "GamepadSDL2",
              profile["id"] as? String == "couch-\(slot)",
              profile["player_index"] as? String == "Player\(slot)",
              profile["controller_type"] as? String == "ProController" else {
            throw LobbyError("Expected a complete positional ProController couch-\(slot)/Player\(slot) profile")
        }
        guard let buttons = profile["right_joycon"] as? [String: Any],
              buttons["button_a"] as? String == "B",
              buttons["button_b"] as? String == "A",
              buttons["button_x"] as? String == "Y",
              buttons["button_y"] as? String == "X" else {
            throw LobbyError("Native positional profile is missing the fixed B/X attack, A special, Y jump mapping")
        }
    }

    private static func object(_ data: Data) throws -> [String: Any] {
        guard let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw LobbyError("Private native config is not a JSON object")
        }
        return value
    }

    private static func canonical(_ value: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
    }

    private static func canonical(withoutLobbyManagedSettings config: [String: Any]) throws -> Data {
        var copy = config
        copy.removeValue(forKey: "input_config")
        copy.removeValue(forKey: "start_fullscreen")
        copy.removeValue(forKey: "remember_window_state")
        copy.removeValue(forKey: "show_confirm_exit")
        return try canonical(copy)
    }
}

struct PreparedConfigReceipt {
    let seedSHA256: String
    let preparedSHA256: String
    let preparedBytes: Data
    let expectedNativeRoundTripSHA256: String
    let expectedNativeRoundTripBytes: Data
    let capacity: Int
    let labels: [String]
    let backupPath: String
}

struct PreparedConfigVerification: Equatable {
    let expectedSHA256: String
    let observedSHA256: String
    let exactBytesMatch: Bool
    let structuralValidationPassed: Bool
    let structuralFailure: String?

    var isLaunchable: Bool { exactBytesMatch && structuralValidationPassed }
}

struct PreparedConfigMismatch: LocalizedError {
    let expectedSHA256: String
    let observedSHA256: String
    let reason: String
    var errorDescription: String? {
        "Prepared private config refused: expected \(expectedSHA256), observed \(observedSHA256); \(reason)"
    }
}

struct NativeConfigRoundTripVerification: Equatable {
    let expectedNativeRoundTripSHA256: String
    let observedConfigSHA256: String
    let matchedRepresentation: String?

    var isExpected: Bool { matchedRepresentation != nil }
}

struct NativeConfigRoundTripMismatch: LocalizedError {
    let expectedSHA256: String
    let observedSHA256: String
    var errorDescription: String? {
        "Native exit Config.json preserved: expected the prepared or exact canonical serialized form " +
        "(\(expectedSHA256)), observed \(observedSHA256); no rollback was performed"
    }
}

struct NativeLobbyLaunchReceipt: Codable, Equatable {
    let receiptVersion: Int
    let sessionID: String
    let createdAtUTC: String
    var updatedAtUTC: String
    var state: String
    let seedConfigSHA256: String
    var expectedPreparedConfigSHA256: String?
    var observedPreparedConfigSHA256: String?
    var observedConfigSHA256AtFailure: String?
    var expectedNativeRoundTripConfigSHA256: String?
    var observedConfigSHA256AtExit: String?
    var nativeRoundTripStatus: String?
    var selfTelemetryPath: String?
    var selfTelemetryStatus: String?
    var selfTelemetryStopReason: String?
    var selfTelemetrySampleCount: Int?
    var selfTelemetryLastSequence: UInt64?
    var selfTelemetryLastFootprintBytes: UInt64?
    var selfTelemetryPeakFootprintBytes: UInt64?
    var selfTelemetryChildPID: Int32?
    var selfTelemetrySurvivorObserved: Bool?
    let capacity: Int
    let labels: [String]
    let nativeAppPath: String
    let nativeExecutablePath: String
    let packageManifestPath: String
    let packageManifestSHA256: String
    let privateDataPath: String
    let configPath: String
    let gamePath: String
    var appSignatureVerified: Bool
    var childPID: Int32?
    var childExitStatus: Int32?
    var failure: String?
    var rollbackOutcome: String?
}

enum NativeLobbyLaunchReceiptStore {
    static func persist(_ receipt: NativeLobbyLaunchReceipt, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(receipt)
        let manager = FileManager.default
        if manager.fileExists(atPath: url.path) {
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink == false else {
                throw LobbyError("Launch receipt path is not a non-symlink regular file")
            }
        }
        let temporary = url.deletingLastPathComponent()
            .appendingPathComponent(".\(url.lastPathComponent).\(UUID().uuidString).tmp")
        defer { try? manager.removeItem(at: temporary) }
        try data.write(to: temporary, options: .atomic)
        try manager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: temporary.path)
        let handle = try FileHandle(forWritingTo: temporary)
        try handle.synchronize()
        try handle.close()
        let renameStatus = temporary.path.withCString { source in
            url.path.withCString { destination in Darwin.rename(source, destination) }
        }
        guard renameStatus == 0 else {
            throw LobbyError("Atomic launch receipt rename failed: \(String(cString: strerror(errno)))")
        }
        let directoryFD = url.deletingLastPathComponent().path.withCString {
            Darwin.open($0, O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        }
        guard directoryFD >= 0 else {
            throw LobbyError("Could not open launch-receipt directory for durability: \(String(cString: strerror(errno)))")
        }
        defer { Darwin.close(directoryFD) }
        guard Darwin.fsync(directoryFD) == 0 else {
            throw LobbyError("Could not sync launch-receipt directory: \(String(cString: strerror(errno)))")
        }
        let persisted = try Data(contentsOf: url)
        guard persisted == data, try JSONDecoder().decode(NativeLobbyLaunchReceipt.self, from: persisted) == receipt else {
            throw LobbyError("Durable launch receipt read-back did not match the session record")
        }
    }
}

enum NativeConfigTransaction {
    static func apply(configURL: URL, backupURL: URL, seed: Data,
                      expectedSeedSHA256: String, capacity: Int,
                      labels: [String]) throws -> PreparedConfigReceipt {
        try requireRegularFile(configURL)
        let current = try Data(contentsOf: configURL)
        let seedHash = LobbyGate.digest(data: seed)
        guard seedHash == expectedSeedSHA256.lowercased(),
              LobbyGate.digest(data: current) == seedHash, current == seed else {
            throw LobbyError("Private Config.json no longer matches the root-approved seed; preserve it and refuse")
        }

        if FileManager.default.fileExists(atPath: backupURL.path) {
            try requireRegularFile(backupURL)
            guard try Data(contentsOf: backupURL) == seed else {
                throw LobbyError("Existing private seed backup differs; preserve it and refuse")
            }
        } else {
            do { try seed.write(to: backupURL, options: .withoutOverwriting) }
            catch { throw LobbyError("Could not preserve exact seed config: \(error.localizedDescription)") }
        }

        let prepared = try NativeLobbyConfiguration.prepare(seed: seed, capacity: capacity, labels: labels)
        let preparedHash = LobbyGate.digest(data: prepared)
        let expectedNativeRoundTrip = try NativeLobbyConfiguration.expectedNativeRoundTrip(prepared)
        let expectedNativeRoundTripHash = LobbyGate.digest(data: expectedNativeRoundTrip)
        try prepared.write(to: configURL, options: .atomic)
        let committed = try Data(contentsOf: configURL)
        guard committed == prepared, LobbyGate.digest(data: committed) == preparedHash else {
            throw LobbyError("Atomic private config preparation did not commit exact approved bytes")
        }
        return PreparedConfigReceipt(seedSHA256: seedHash,
                                     preparedSHA256: preparedHash,
                                     preparedBytes: prepared,
                                     expectedNativeRoundTripSHA256: expectedNativeRoundTripHash,
                                     expectedNativeRoundTripBytes: expectedNativeRoundTrip,
                                     capacity: capacity,
                                     labels: labels,
                                     backupPath: backupURL.path)
    }

    static func inspectPrepared(configURL: URL, seed: Data,
                                receipt: PreparedConfigReceipt) throws -> PreparedConfigVerification {
        try requireRegularFile(configURL)
        let current = try Data(contentsOf: configURL)
        let observedHash = LobbyGate.digest(data: current)
        let exact = current == receipt.preparedBytes && observedHash == receipt.preparedSHA256
        var structurePassed = false
        var structuralFailure: String?
        do {
            try NativeLobbyConfiguration.validatePrepared(current, againstSeed: seed,
                capacity: receipt.capacity, labels: receipt.labels)
            structurePassed = true
        } catch { structuralFailure = error.localizedDescription }
        return PreparedConfigVerification(expectedSHA256: receipt.preparedSHA256,
            observedSHA256: observedHash, exactBytesMatch: exact,
            structuralValidationPassed: structurePassed, structuralFailure: structuralFailure)
    }

    static func requireLaunchable(_ verification: PreparedConfigVerification) throws {
        guard verification.isLaunchable else {
            throw PreparedConfigMismatch(expectedSHA256: verification.expectedSHA256,
                observedSHA256: verification.observedSHA256,
                reason: verification.structuralFailure ?? "prepared bytes changed after admission")
        }
    }

    @discardableResult
    static func restoreSeedIfUnchanged(configURL: URL, seed: Data,
                                       preparedSHA256: String) throws -> Bool {
        try requireRegularFile(configURL)
        let current = try Data(contentsOf: configURL)
        guard LobbyGate.digest(data: current) == preparedSHA256.lowercased() else {
            throw LobbyError("Private config changed after preparation; preserve discrepancy and refuse rollback")
        }
        try seed.write(to: configURL, options: .atomic)
        guard try Data(contentsOf: configURL) == seed else {
            throw LobbyError("Exact seed restoration could not be verified; preserve the private clone")
        }
        return true
    }

    static func inspectAfterChildExit(configURL: URL,
                                      receipt: PreparedConfigReceipt) throws -> NativeConfigRoundTripVerification {
        try requireRegularFile(configURL)
        let current = try Data(contentsOf: configURL)
        let observedSHA256 = LobbyGate.digest(data: current)
        if current == receipt.preparedBytes && observedSHA256 == receipt.preparedSHA256 {
            return NativeConfigRoundTripVerification(
                expectedNativeRoundTripSHA256: receipt.expectedNativeRoundTripSHA256,
                observedConfigSHA256: observedSHA256,
                matchedRepresentation: "exact_prepared_bytes")
        }

        let canonicalCurrent = try NativeLobbyConfiguration.canonicalJSON(current)
        guard canonicalCurrent == receipt.expectedNativeRoundTripBytes else {
            return NativeConfigRoundTripVerification(
                expectedNativeRoundTripSHA256: receipt.expectedNativeRoundTripSHA256,
                observedConfigSHA256: observedSHA256,
                matchedRepresentation: nil)
        }
        return NativeConfigRoundTripVerification(
            expectedNativeRoundTripSHA256: receipt.expectedNativeRoundTripSHA256,
            observedConfigSHA256: observedSHA256,
            matchedRepresentation: "native_schema_serialized")
    }

    static func restoreSeedAfterChildExit(configURL: URL, seed: Data,
                                          receipt: PreparedConfigReceipt,
                                          verification: NativeConfigRoundTripVerification) throws {
        guard verification.isExpected else {
            throw NativeConfigRoundTripMismatch(expectedSHA256: verification.expectedNativeRoundTripSHA256,
                                                observedSHA256: verification.observedConfigSHA256)
        }
        try requireRegularFile(configURL)
        let latest = try inspectAfterChildExit(configURL: configURL, receipt: receipt)
        guard latest == verification else {
            throw NativeConfigRoundTripMismatch(expectedSHA256: verification.expectedNativeRoundTripSHA256,
                                                observedSHA256: latest.observedConfigSHA256)
        }
        try seed.write(to: configURL, options: .atomic)
        guard try Data(contentsOf: configURL) == seed else {
            throw LobbyError("Exact seed restoration after expected native serialization failed")
        }
    }

    private static func requireRegularFile(_ url: URL) throws {
        let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink == false else {
            throw LobbyError("Expected a non-symlink regular config file: \(url.path)")
        }
    }
}
