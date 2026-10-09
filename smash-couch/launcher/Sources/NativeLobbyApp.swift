import AppKit
import SwiftUI
import GameController
import CoreHaptics
import Security
import Darwin

@_silgen_name("couch_socket") func couchSocket() -> Int32
@_silgen_name("couch_send") func couchSend(_ fd: Int32, _ path: UnsafePointer<CChar>, _ data: UnsafeRawPointer, _ count: Int32) -> Int32

struct LobbyControllerCard: Identifiable {
    let id: UUID
    let name: String
    let selectedSlot: Int
    let active: Bool
}

@MainActor final class NativeLobbyModel: ObservableObject {
    @Published var message = "Connect a controller. Choose a player slot with the D-pad, press Menu to join, release controls, then press Menu again to launch."
    @Published var running = false
    @Published var revision = 0
    @Published var preferences = LobbyPreferences.defaults

    var book = SlotBook()
    // The object map is only for sampling a currently connected GCController.
    // Ownership is granted exclusively by a fresh Menu edge at the selected slot.
    var controllers: [ObjectIdentifier: (GCController, UUID)] = [:]
    var prior: [UUID: PadSample] = [:]
    var names: [UUID: String] = [:]
    var selectionByConnection: [UUID: Int] = [:]
    var lastActivity: [UUID: Date] = [:]
    var timer: DispatchSourceTimer?
    var fastPolling = false
    var child: Process?
    var selfTelemetrySupervisor: NativeSelfTelemetrySupervisor?
    var selfTelemetryFailure: String?
    var lockFD: Int32 = -1
    var socketFD: Int32 = -1
    var socketPath = ""
    var token: [UInt8] = []
    var sequence: UInt64 = 0
    var session: URL?
    var logHandle: FileHandle?
    var frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
    var wasFocused = true
    var inputActivity: NSObjectProtocol?
    var pulseEngines: [UUID: CHHapticEngine] = [:]
    var activeLaunchReceipt: NativeLobbyLaunchReceipt?
    var activeLaunchReceiptURL: URL?
    var activeSessionLease: NativePreviewSessionLease?
    var activeConfigURL: URL?
    var activeSeedConfig: Data?
    var activePreparedConfigReceipt: PreparedConfigReceipt?
    let gate: LobbyGate
    let root = LobbyGate.privateRoot
    var currentPID: pid_t? { child?.isRunning == true ? child?.processIdentifier : nil }

    func persistLaunchReceipt(_ receipt: inout NativeLobbyLaunchReceipt, to url: URL) throws {
        receipt.updatedAtUTC = ISO8601DateFormatter().string(from: Date())
        try NativeLobbyLaunchReceiptStore.persist(receipt, to: url)
        activeLaunchReceipt = receipt
        activeLaunchReceiptURL = url
    }

    func saveActiveLaunchReceipt() throws {
        guard var receipt = activeLaunchReceipt, let url = activeLaunchReceiptURL else { return }
        try persistLaunchReceipt(&receipt, to: url)
    }

    var controllerCards: [LobbyControllerCard] {
        controllers.values.compactMap { pair in
            let id = pair.1
            guard book.slots.firstIndex(where: { $0.connection == id }) == nil else { return nil }
            return LobbyControllerCard(id: id,
                name: names[id] ?? "Game controller",
                selectedSlot: selectionByConnection[id] ?? 0,
                active: (lastActivity[id]?.timeIntervalSinceNow ?? -5) > -0.35)
        }
    }

    init(gate: LobbyGate) throws {
        self.gate = gate
        let lockPath = root.appendingPathComponent("launcher.lock")
        lockFD = open(lockPath.path, O_RDWR | O_CREAT | O_NOFOLLOW, 0o600)
        guard lockFD >= 0, flock(lockFD, LOCK_EX | LOCK_NB) == 0 else {
            throw LobbyError("Another native Metal controller lobby owns this private root")
        }
        loadPrivatePreferences()
        guard book.setCapacity(preferences.capacity) else { throw LobbyError("Private lobby capacity is invalid") }

        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                self?.frontmostPID = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier
            }
        }
        GCController.shouldMonitorBackgroundEvents = true
        NotificationCenter.default.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated { if let pad = note.object as? GCController { self?.connected(pad) } }
        }
        NotificationCenter.default.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated { if let pad = note.object as? GCController { self?.disconnected(pad) } }
        }
        for pad in GCController.controllers() { connected(pad) }
        let poll = DispatchSource.makeTimerSource(flags: .strict, queue: .main)
        poll.schedule(deadline: .now(), repeating: .milliseconds(100), leeway: .milliseconds(10))
        poll.setEventHandler { [weak self] in MainActor.assumeIsolated { self?.tick() } }
        timer = poll
        poll.resume()
    }

    func loadPrivatePreferences() {
        let url = root.appendingPathComponent("lobby-preferences.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        do {
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard values.isRegularFile == true, values.isSymbolicLink == false,
                  let saved = try? JSONDecoder().decode(LobbyPreferences.self, from: Data(contentsOf: url)), saved.isValid else {
                message = "Private lobby preferences are invalid; using safe defaults."
                return
            }
            preferences = saved
        } catch { message = "Private lobby preferences could not be read; using safe defaults." }
    }

    func savePrivatePreferences() {
        guard preferences.isValid else { return }
        let url = root.appendingPathComponent("lobby-preferences.json")
        do {
            if FileManager.default.fileExists(atPath: url.path) {
                let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
                guard values.isRegularFile == true, values.isSymbolicLink == false else {
                    throw LobbyError("Private preferences path is not a regular file")
                }
            }
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(preferences).write(to: url, options: .atomic)
        } catch { message = "Private lobby preference save failed: \(error.localizedDescription)" }
    }

    func setCapacity(_ requested: Int) {
        guard !running else { return }
        guard book.setCapacity(requested) else {
            message = "Cannot reduce capacity across a joined or reserved player slot. Reclaim or keep that slot first."
            revision += 1
            return
        }
        for id in Array(selectionByConnection.keys) {
            selectionByConnection[id] = (selectionByConnection[id] ?? 0) % requested
        }
        preferences.capacity = requested
        savePrivatePreferences()
        message = "Capacity set to \(requested). Slot reservations stay fixed until explicitly reassigned."
        revision += 1
    }

    func setLabel(_ label: String, slot: Int) {
        guard !running, preferences.labels.indices.contains(slot), LobbyPreferences.validLabel(label) else { return }
        preferences.labels[slot] = label
        savePrivatePreferences()
        revision += 1
    }

    func connected(_ pad: GCController) {
        let key = ObjectIdentifier(pad)
        guard controllers[key] == nil else { return }
        let id = UUID()
        controllers[key] = (pad, id)
        names[id] = pad.vendorName ?? "Game controller"
        selectionByConnection[id] = 0
        prior[id] = sample(pad)
        message = "Connected: \(names[id]!). Choose P1–P\(book.capacity) with this pad's D-pad, then press Menu."
        revision += 1
    }

    func disconnected(_ pad: GCController) {
        guard let (_, id) = controllers.removeValue(forKey: ObjectIdentifier(pad)) else { return }
        let oldSlot = book.slots.firstIndex { $0.connection == id }
        book.disconnect(id)
        prior.removeValue(forKey: id)
        selectionByConnection.removeValue(forKey: id)
        names.removeValue(forKey: id)
        lastActivity.removeValue(forKey: id)
        pulseEngines.removeValue(forKey: id)
        if let oldSlot {
            message = "P\(oldSlot + 1) disconnected and remains reserved. Reconnect, choose P\(oldSlot + 1), and press Menu to reclaim."
        } else {
            message = "An unassigned controller disconnected. Other slot reservations did not move."
        }
        revision += 1
    }

    func sample(_ pad: GCController) -> PadSample {
        let state = pad.input.capture()
        var bits: UInt32 = 0
        let buttons: [(Int, GCButtonElementName)] = [
            (1, .a), (2, .b), (3, .x), (4, .y), (5, .leftThumbstickButton),
            (6, .rightThumbstickButton), (7, .leftShoulder), (8, .rightShoulder),
            (15, .options), (16, .menu)
        ]
        for (bit, name) in buttons where state.buttons[name]?.pressedInput.isPressed == true { bits |= 1 << bit }
        if let dpad = state.dpads[.directionPad] {
            if dpad.up.isPressed { bits |= 1 << 11 }
            if dpad.down.isPressed { bits |= 1 << 12 }
            if dpad.left.isPressed { bits |= 1 << 13 }
            if dpad.right.isPressed { bits |= 1 << 14 }
        }
        let left = state.dpads[.leftThumbstick]
        let right = state.dpads[.rightThumbstick]
        return PadSample(buttons: bits, axes: [left?.xAxis.value ?? 0, left?.yAxis.value ?? 0,
            right?.xAxis.value ?? 0, right?.yAxis.value ?? 0,
            state.buttons[.leftTrigger]?.pressedInput.value ?? 0,
            state.buttons[.rightTrigger]?.pressedInput.value ?? 0]).mappedForNativeLobby()
    }

    func tick() {
        let captured = mach_continuous_time()
        let ours = frontmostPID == ProcessInfo.processInfo.processIdentifier || frontmostPID == currentPID
        if !ours && wasFocused { book.suspend() }
        let focusJustReturned = ours && !wasFocused
        wasFocused = ours
        updateInputActivity(running && ours && !controllers.isEmpty)
        let fast = ours && !controllers.isEmpty
        if fast != fastPolling {
            fastPolling = fast
            timer?.schedule(deadline: .now(), repeating: .milliseconds(fast ? 4 : 100),
                            leeway: .microseconds(fast ? 250 : 10_000))
        }

        var joinRequests: [SlotJoinRequest] = []
        var launchRequests: [UUID] = []
        for (pad, id) in controllers.values {
            let now = sample(pad)
            let before = focusJustReturned ? now : (prior[id] ?? now)
            prior[id] = now
            if now.buttons != before.buttons || axesChanged(now.axes, before.axes) { lastActivity[id] = Date() }
            guard ours else { continue }
            let assigned = book.slots.firstIndex { $0.connection == id }
            if assigned == nil {
                let movedLeft = now.down(13) && !before.down(13)
                let movedRight = now.down(14) && !before.down(14)
                if movedLeft != movedRight {
                    let direction = movedLeft ? -1 : 1
                    selectionByConnection[id] = SlotBook.movedSelection(selectionByConnection[id] ?? 0,
                                                                          direction: direction,
                                                                          capacity: book.capacity)
                }
                if now.down(16) && !before.down(16) {
                    joinRequests.append(SlotJoinRequest(connection: id,
                        slot: selectionByConnection[id] ?? 0))
                }
            } else {
                if !running && book.canLaunch(id, current: now, previous: before) { launchRequests.append(id) }
                book.update(id, sample: now)
            }
        }

        if ours {
            if joinRequests.count > 1 || (!running && !joinRequests.isEmpty && !launchRequests.isEmpty) || launchRequests.count > 1 {
                message = "Simultaneous Menu actions were rejected. Release controls and join or launch one controller at a time."
            } else if joinRequests.count == 1 {
                let request = joinRequests[0]
                let outcome = book.applyJoinRequests([request])
                switch outcome {
                case let .joined(slot):
                    message = "P\(slot + 1) claimed. Release all controls before using it; names and connect order do not assign ownership."
                    pulse(slot: slot)
                case let .occupied(slot):
                    message = "P\(slot + 1) is occupied. A reserved disconnect must be explicitly reclaimed after that slot is free."
                case let .invalidSlot(slot):
                    message = "P\(slot + 1) is above the selected capacity. Choose an active slot with the D-pad."
                case .duplicateConnection:
                    message = "This connection already owns a slot; its assignment did not change."
                case .ambiguous, .none:
                    message = "No controller slot changed."
                }
            } else if launchRequests.count == 1, !running {
                book.suspend()
                launch()
            }
        }

        if socketFD >= 0 {
            sequence &+= 1
            let packet = book.packet(token: token, sequence: sequence, captureTime: captured,
                                     enabled: ours && running)
            _ = socketPath.withCString { path in
                packet.withUnsafeBytes { couchSend(socketFD, path, $0.baseAddress!, Int32(packet.count)) }
            }
        }
        revision += 1
    }

    private func axesChanged(_ first: [Float], _ second: [Float]) -> Bool {
        guard first.count == second.count else { return true }
        return zip(first, second).contains { abs($0 - $1) >= 0.12 }
    }

    func pulse(slot: Int) {
        guard book.slots.indices.contains(slot), let id = book.slots[slot].connection else { return }
        pulse(connection: id, label: "P\(slot + 1)")
    }

    func pulseUnassignedController(_ id: UUID) {
        guard book.slots.firstIndex(where: { $0.connection == id }) == nil else { return }
        pulse(connection: id, label: names[id] ?? "connected controller")
    }

    private func pulse(connection id: UUID, label: String) {
        guard let (pad, _) = controllers.values.first(where: { $0.1 == id }) else { return }
        guard let haptics = pad.haptics else {
            message = "\(label) is identified by button activity; haptics are not available on this controller."
            return
        }
        do {
            guard let engine = haptics.createEngine(withLocality: .default) else {
                message = "\(label) is identified by button activity; no haptic engine is available."
                return
            }
            try engine.start()
            let event = CHHapticEvent(eventType: .hapticTransient,
                parameters: [.init(parameterID: .hapticIntensity, value: 0.65),
                             .init(parameterID: .hapticSharpness, value: 0.4)], relativeTime: 0)
            let pattern = try CHHapticPattern(events: [event], parameters: [])
            try engine.makePlayer(with: pattern).start(atTime: 0)
            pulseEngines[id] = engine
            message = "Identify pulse sent to \(label)."
        } catch {
            message = "\(label) is identified by button activity; haptics are unavailable on this controller."
        }
    }

    func status(_ slot: Int) -> String {
        let state = book.slots[slot]
        guard let id = state.connection else {
            return state.reserved ? "Disconnected — reserved" : "Press Menu to join"
        }
        let name = names[id] ?? "Game controller"
        let activity = (lastActivity[id]?.timeIntervalSinceNow ?? -5) > -0.35 ? " · button activity" : ""
        return "\(name) · \(state.ready ? "ready" : "release controls")\(activity)"
    }

    func selectedByText(_ slot: Int) -> String? {
        let waiting = controllers.values.compactMap { (_, id) -> String? in
            guard book.slots.firstIndex(where: { $0.connection == id }) == nil,
                  selectionByConnection[id] == slot else { return nil }
            return names[id] ?? "Game controller"
        }
        guard !waiting.isEmpty else { return nil }
        return "Selected by \(waiting.joined(separator: ", "))"
    }

    func verifySignature(_ app: URL) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        process.arguments = ["--verify", "--deep", "--strict", app.path]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw LobbyError("Native Metal .53 app signature check failed") }
    }

    func startSelfTelemetrySupervisor(for game: Process, telemetry: URL,
                                      privateData: URL, nonce: String) throws {
        let supervisor = NativeSelfTelemetrySupervisor()
        guard supervisor.start(process: game, telemetryURL: telemetry, privateData: privateData,
            nonce: nonce, onIncident: selfTelemetryIncidentHandler()) else {
            throw LobbyError("Could not attach the self-telemetry supervisor to the retained native child")
        }
        selfTelemetrySupervisor = supervisor
        activeLaunchReceipt?.selfTelemetryPath = telemetry.path
        activeLaunchReceipt?.selfTelemetryStatus = "monitoring"
        activeLaunchReceipt?.selfTelemetryChildPID = game.processIdentifier
    }

    private func selfTelemetryIncidentHandler() -> (NativeSelfTelemetrySummary) -> Void {
        { [weak self] summary in
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.handleSelfTelemetryIncident(summary) }
            }
        }
    }

    // Bounded activation of only the Process this lobby launched. If the user
    // moves to a different app, leave focus there and stop retrying.
    private func activateChild(_ process: Process, attemptsRemaining: Int) {
        guard child === process, process.isRunning, attemptsRemaining > 0 else { return }
        let pid = process.processIdentifier
        let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier
        if foreground == pid { return }
        let launcherPID = ProcessInfo.processInfo.processIdentifier
        guard foreground == launcherPID || foreground == nil else { return }
        if foreground == launcherPID,
           let application = NSRunningApplication(processIdentifier: pid), !application.isTerminated {
            _ = application.activate(options: [])
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
            self?.activateChild(process, attemptsRemaining: attemptsRemaining - 1)
        }
    }

    func recordSelfTelemetry(_ summary: NativeSelfTelemetrySummary) {
        guard activeLaunchReceipt?.childPID == summary.childPID else { return }
        activeLaunchReceipt?.selfTelemetryStatus = summary.status
        activeLaunchReceipt?.selfTelemetryStopReason = summary.stopReason
        activeLaunchReceipt?.selfTelemetrySampleCount = summary.sampleCount
        activeLaunchReceipt?.selfTelemetryLastSequence = summary.lastSequence
        activeLaunchReceipt?.selfTelemetryLastFootprintBytes = summary.lastFootprintBytes
        activeLaunchReceipt?.selfTelemetryPeakFootprintBytes = summary.peakFootprintBytes
        activeLaunchReceipt?.selfTelemetryChildPID = summary.childPID
        activeLaunchReceipt?.selfTelemetrySurvivorObserved = summary.survivorObserved
        try? saveActiveLaunchReceipt()
    }

    func handleSelfTelemetryIncident(_ summary: NativeSelfTelemetrySummary) {
        guard child?.processIdentifier == summary.childPID else { return }
        selfTelemetryFailure = summary.stopReason ?? summary.status
        book.suspend()
        recordSelfTelemetry(summary)
        if summary.survivorObserved {
            message = "Exact native child remains alive after bounded TERM/KILL. Input is neutral and supervision is retained; recovery is not guaranteed."
        } else {
            message = "Native self-telemetry supervisor requested stop: \(summary.stopReason ?? summary.status). Input is neutral while the exact child exits."
        }
    }

    func requestStopOfRetainedChild(reason: String) {
        guard let game = child, game.isRunning, game.processIdentifier > 0 else { return }
        book.suspend()
        selfTelemetryFailure = reason
        if let selfTelemetrySupervisor {
            selfTelemetrySupervisor.requestStop()
        } else {
            // Telemetry setup can fail after Process.run. Adopt the same exact
            // retained Process for bounded TERM/KILL supervision; never fall
            // back to an untracked one-shot signal.
            let supervisor = NativeSelfTelemetrySupervisor()
            guard supervisor.startStopping(process: game, onIncident: selfTelemetryIncidentHandler()) else {
                activeLaunchReceipt?.selfTelemetryStatus = "stop_supervisor_unavailable"
                activeLaunchReceipt?.selfTelemetryStopReason = reason
                message = "The retained native child remains held because stop-only supervision could not attach; config rollback is blocked until confirmed exit."
                try? saveActiveLaunchReceipt()
                return
            }
            selfTelemetrySupervisor = supervisor
            activeLaunchReceipt?.selfTelemetryStatus = "stop_only_supervision"
            activeLaunchReceipt?.selfTelemetryStopReason = reason
            activeLaunchReceipt?.selfTelemetryChildPID = game.processIdentifier
            try? saveActiveLaunchReceipt()
        }
    }

    func launch() {
        guard !running else { return }
        guard activeSessionLease == nil else {
            message = "A prior Native Metal session still has an unresolved cleanup lease; review its receipt and child before recovery."
            revision += 1
            return
        }
        var configURL: URL?
        var seedData: Data?
        var configReceipt: PreparedConfigReceipt?
        var childStarted = false
        var prechildCleanupVerified = false
        do {
            let approved = try LobbyGate.loadDefault()
            let app = URL(fileURLWithPath: approved.nativeApp)
            let executable = app.appendingPathComponent("Contents/MacOS/Ryujinx")
            let data = URL(fileURLWithPath: approved.privateData)
            let config = data.appendingPathComponent("Config.json")
            let sessionID = UUID().uuidString.lowercased()
            let telemetry = try NativeSelfTelemetryFile.targetURL(sessionID: sessionID, under: data)
            let telemetryNonce = try NativeSelfTelemetrySupervisor.freshNonce()
            try NativeSelfTelemetryFile.validateAbsentTarget(telemetry, under: data)
            configURL = config
            let stamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
            activeLaunchReceiptURL = root.appendingPathComponent(
                "session-\(stamp)-\(sessionID.prefix(8)).launch-receipt.json")
            activeLaunchReceipt = NativeLobbyLaunchReceipt(
                receiptVersion: 1, sessionID: sessionID, createdAtUTC: ISO8601DateFormatter().string(from: Date()),
                updatedAtUTC: ISO8601DateFormatter().string(from: Date()), state: "preflight",
                seedConfigSHA256: approved.configSHA256, expectedPreparedConfigSHA256: nil,
                observedPreparedConfigSHA256: nil, observedConfigSHA256AtFailure: nil,
                expectedNativeRoundTripConfigSHA256: nil, observedConfigSHA256AtExit: nil,
                nativeRoundTripStatus: nil, selfTelemetryPath: nil, selfTelemetryStatus: nil,
                selfTelemetryStopReason: nil, selfTelemetrySampleCount: nil,
                selfTelemetryLastSequence: nil, selfTelemetryLastFootprintBytes: nil,
                selfTelemetryPeakFootprintBytes: nil, selfTelemetryChildPID: nil,
                selfTelemetrySurvivorObserved: nil,
                capacity: book.capacity, labels: Array(preferences.labels.prefix(book.capacity)),
                nativeAppPath: app.path, nativeExecutablePath: executable.path,
                packageManifestPath: approved.packageManifest,
                packageManifestSHA256: approved.packageManifestSHA256,
                privateDataPath: data.path, configPath: config.path, gamePath: approved.gamePath,
                appSignatureVerified: false, childPID: nil, childExitStatus: nil,
                failure: nil, rollbackOutcome: nil)
            try saveActiveLaunchReceipt()

            guard approved.packageManifestSHA256 == gate.packageManifestSHA256,
                  approved.configSHA256 == gate.configSHA256,
                  book.slots.prefix(book.capacity).contains(where: { $0.connection != nil }) else {
                throw LobbyError("Gate changed or no controller has explicitly joined an active slot")
            }
            guard !emulatorIsRunning() else {
                throw LobbyError("Close every Ryujinx process before the private Native Metal session")
            }
            try verifySignature(app)
            activeLaunchReceipt?.appSignatureVerified = true
            activeLaunchReceipt?.selfTelemetryPath = telemetry.path
            try saveActiveLaunchReceipt()

            let original = try Data(contentsOf: config)
            seedData = original
            guard LobbyGate.digest(data: original) == approved.configSHA256.lowercased() else {
                throw LobbyError("Private seed Config.json changed before prelaunch preparation")
            }

            activeSessionLease = try NativePreviewSessionClaim.acquire(in: root, sessionID: sessionID)

            let uid = getuid()
            let temp = URL(fileURLWithPath: "/tmp/scn-\(uid)-\(UUID().uuidString.prefix(12))")
            try FileManager.default.createDirectory(at: temp, withIntermediateDirectories: false,
                                                    attributes: [.posixPermissions: 0o700])
            session = temp
            socketPath = temp.appendingPathComponent("in").path
            token = Array(repeating: 0, count: 16)
            guard SecRandomCopyBytes(kSecRandomDefault, 16, &token) == errSecSuccess else {
                throw LobbyError("Could not create the private input session token")
            }
            sequence = 0
            socketFD = couchSocket()
            guard socketFD >= 0 else { throw LobbyError("Could not create the controller input socket") }

            let logStamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
            let log = root.appendingPathComponent("session-\(logStamp)-\(UUID().uuidString.prefix(6)).log")
            FileManager.default.createFile(atPath: log.path, contents: nil)
            logHandle = try FileHandle(forWritingTo: log)

            let process = Process()
            process.executableURL = executable
            process.currentDirectoryURL = executable.deletingLastPathComponent()
            process.arguments = NativeLobbyConfiguration.launchArguments(privateData: data,
                                                                          gamePath: approved.gamePath)
            let environment = NativePreviewEnvironment.child(base: ProcessInfo.processInfo.environment,
                privateData: data, telemetry: telemetry, nonce: telemetryNonce, socket: socketPath, token: token)
            process.environment = environment
            process.standardOutput = logHandle
            process.standardError = logHandle
            process.terminationHandler = { [weak self] done in
                DispatchQueue.main.async { self?.finished(done) }
            }

            // Prepare the private Config.json only after all other fallible setup,
            // then recheck that no emulator appeared and launch immediately.
            guard !emulatorIsRunning() else {
                throw LobbyError("A Ryujinx process appeared before private config preparation")
            }
            let backup = root.appendingPathComponent("seed-config-backup.json")
            configReceipt = try NativeConfigTransaction.apply(configURL: config, backupURL: backup,
                seed: original, expectedSeedSHA256: approved.configSHA256,
                capacity: book.capacity, labels: preferences.labels)
            activeConfigURL = config
            activeSeedConfig = original
            activePreparedConfigReceipt = configReceipt
            activeLaunchReceipt?.expectedNativeRoundTripConfigSHA256 = configReceipt?.expectedNativeRoundTripSHA256
            let preparationCheck = try NativeConfigTransaction.inspectPrepared(configURL: config,
                seed: original, receipt: configReceipt!)
            activeLaunchReceipt?.expectedPreparedConfigSHA256 = preparationCheck.expectedSHA256
            activeLaunchReceipt?.observedPreparedConfigSHA256 = preparationCheck.observedSHA256
            activeLaunchReceipt?.state = "prepared"
            guard preparationCheck.isLaunchable else {
                throw PreparedConfigMismatch(expectedSHA256: preparationCheck.expectedSHA256,
                    observedSHA256: preparationCheck.observedSHA256,
                    reason: preparationCheck.structuralFailure ?? "prepared bytes changed during prelaunch setup")
            }
            try saveActiveLaunchReceipt()
            guard !emulatorIsRunning() else {
                throw LobbyError("A Ryujinx process appeared after private config preparation; restoring only if its bytes still match")
            }

            // Check the telemetry destination before the last Config.json read.
            // No filesystem or policy work follows that final config check.
            try NativeSelfTelemetryFile.validateAbsentTarget(telemetry, under: data)
            let finalCheck = try NativeConfigTransaction.inspectPrepared(configURL: config,
                seed: original, receipt: configReceipt!)
            activeLaunchReceipt?.expectedPreparedConfigSHA256 = finalCheck.expectedSHA256
            activeLaunchReceipt?.observedPreparedConfigSHA256 = finalCheck.observedSHA256
            guard finalCheck.isLaunchable else {
                let mismatch = PreparedConfigMismatch(expectedSHA256: finalCheck.expectedSHA256,
                    observedSHA256: finalCheck.observedSHA256,
                    reason: finalCheck.structuralFailure ?? "private Config.json changed immediately before Process.run")
                activeLaunchReceipt?.state = "refused_before_child"
                activeLaunchReceipt?.failure = mismatch.localizedDescription
                try saveActiveLaunchReceipt()
                throw mismatch
            }

            // No filesystem work or policy decision may intervene between the exact
            // byte/hash admission above and this Process.run call.
            child = process
            try process.run()
            childStarted = true
            activeLaunchReceipt?.state = "started"
            activeLaunchReceipt?.childPID = process.processIdentifier
            activeLaunchReceipt?.failure = nil
            activeLaunchReceipt?.selfTelemetryChildPID = process.processIdentifier
            running = true
            // Attach the regular watchdog before the next fallible lease update.
            // If this setup fails, catch adopts this same Process in stop-only mode.
            try startSelfTelemetrySupervisor(for: process, telemetry: telemetry,
                                             privateData: data, nonce: telemetryNonce)
            guard let lease = activeSessionLease else {
                throw LobbyError("Native Metal active-session lease was lost after child start")
            }
            activeSessionLease = try NativePreviewSessionClaim.recordChildStarted(in: root, lease: lease,
                childPID: process.processIdentifier)
            try saveActiveLaunchReceipt()
            message = "Native Metal .53 session running with \(book.capacity) fixed logical slots. Self telemetry monitors this exact child; close the game normally to finish."
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                self?.activateChild(process, attemptsRemaining: 40)
            }
        } catch {
            let refusal = "Native Metal .53 session refused: \(error.localizedDescription)"
            if let game = child, game.isRunning {
                running = true
                book.suspend()
                requestStopOfRetainedChild(reason: "launcher_setup_failure")
            } else {
                running = false
                if let pid = child?.processIdentifier { _ = selfTelemetrySupervisor?.childExited(pid: pid) }
                selfTelemetrySupervisor = nil
                cleanupTransport()
            }
            var rollbackMessage = ""
            if !childStarted, child?.isRunning != true, let configURL, let seedData {
                do {
                    if let configReceipt {
                        try NativeConfigTransaction.restoreSeedIfUnchanged(configURL: configURL, seed: seedData,
                            preparedSHA256: configReceipt.preparedSHA256)
                        activeLaunchReceipt?.rollbackOutcome = "seed_restored_exactly"
                        rollbackMessage = " Exact root-approved seed restored because no native child started."
                    } else {
                        guard try Data(contentsOf: configURL) == seedData else {
                            throw LobbyError("Private config changed before native child start; preserve discrepancy")
                        }
                        activeLaunchReceipt?.rollbackOutcome = "seed_unchanged_before_child"
                        rollbackMessage = " Exact root-approved seed remained unchanged because no native child started."
                    }
                    prechildCleanupVerified = true
                } catch {
                    activeLaunchReceipt?.rollbackOutcome = "preserved_private_config_discrepancy"
                    rollbackMessage = " Config discrepancy preserved: \(error.localizedDescription)"
                }
            }
            activeLaunchReceipt?.state = childStarted ? "failed_after_child_start" : "refused_before_child"
            activeLaunchReceipt?.failure = error.localizedDescription
            if !childStarted, activeLaunchReceipt?.childPID == nil, let game = child,
               game.processIdentifier > 0, game.isRunning {
                activeLaunchReceipt?.childPID = game.processIdentifier
            }
            if let mismatch = error as? PreparedConfigMismatch {
                activeLaunchReceipt?.expectedPreparedConfigSHA256 = mismatch.expectedSHA256
                activeLaunchReceipt?.observedPreparedConfigSHA256 = mismatch.observedSHA256
            } else if let configReceipt {
                activeLaunchReceipt?.expectedPreparedConfigSHA256 = configReceipt.preparedSHA256
                activeLaunchReceipt?.expectedNativeRoundTripConfigSHA256 = configReceipt.expectedNativeRoundTripSHA256
            }
            if let configURL, let observed = try? Data(contentsOf: configURL) {
                activeLaunchReceipt?.observedConfigSHA256AtFailure = LobbyGate.digest(data: observed)
            }
            var receiptFailure = ""
            var receiptSaved = false
            do {
                try saveActiveLaunchReceipt()
                receiptSaved = true
            } catch { receiptFailure = " Launch receipt update failed: \(error.localizedDescription)" }
            if prechildCleanupVerified, receiptSaved, let lease = activeSessionLease {
                do {
                    try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: root, lease: lease,
                        exitedChildPID: nil)
                    activeSessionLease = nil
                } catch {
                    receiptFailure += " Pre-child session lease remains held: \(error.localizedDescription)"
                }
            }
            message = refusal + rollbackMessage
                + receiptFailure
        }
    }

    func emulatorIsRunning() -> Bool {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/pgrep")
        process.arguments = ["-x", "Ryujinx"]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do { try process.run(); process.waitUntilExit(); return process.terminationStatus == 0 }
        catch { return true }
    }

    func updateInputActivity(_ active: Bool) {
        if active && inputActivity == nil {
            inputActivity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiatedAllowingIdleSystemSleep, .latencyCritical],
                reason: "Forwarding physical controller input to the private native test")
        } else if !active, let activity = inputActivity {
            ProcessInfo.processInfo.endActivity(activity)
            inputActivity = nil
        }
    }

    func cleanupTransport() {
        updateInputActivity(false)
        if socketFD >= 0 { Darwin.close(socketFD); socketFD = -1 }
        if let session { try? FileManager.default.removeItem(at: session) }
        session = nil
        try? logHandle?.close()
        logHandle = nil
    }

    func finished(_ process: Process) {
        let status = process.terminationStatus
        let completedPID = process.processIdentifier
        let receiptOwnsChild = activeLaunchReceipt?.childPID == completedPID && completedPID > 0
        if let summary = selfTelemetrySupervisor?.childExited(pid: completedPID) {
            recordSelfTelemetry(summary)
        }
        selfTelemetrySupervisor = nil
        running = false
        child = nil
        cleanupTransport()
        book.suspend()
        var receiptFailure = ""
        var configCleanupVerified = false
        var finalReceiptSaved = false
        if let current = activeLaunchReceipt {
            if current.state == "started" { activeLaunchReceipt?.state = "exited" }
            activeLaunchReceipt?.childExitStatus = status
            if receiptOwnsChild, let configURL = activeConfigURL,
               let seed = activeSeedConfig, let prepared = activePreparedConfigReceipt {
                do {
                    let verification = try NativeConfigTransaction.inspectAfterChildExit(
                        configURL: configURL, receipt: prepared)
                    activeLaunchReceipt?.observedConfigSHA256AtExit = verification.observedConfigSHA256
                    activeLaunchReceipt?.nativeRoundTripStatus = verification.matchedRepresentation ?? "unexpected_config_changes"
                    guard verification.isExpected else {
                        throw NativeConfigRoundTripMismatch(
                            expectedSHA256: verification.expectedNativeRoundTripSHA256,
                            observedSHA256: verification.observedConfigSHA256)
                    }
                    try NativeConfigTransaction.restoreSeedAfterChildExit(configURL: configURL,
                        seed: seed, receipt: prepared, verification: verification)
                    activeLaunchReceipt?.rollbackOutcome = "seed_restored_after_expected_native_roundtrip"
                    configCleanupVerified = (try? Data(contentsOf: configURL)) == seed
                    if !configCleanupVerified {
                        throw LobbyError("Exact root-approved seed bytes were not present after restoration")
                    }
                } catch let mismatch as NativeConfigRoundTripMismatch {
                    activeLaunchReceipt?.observedConfigSHA256AtExit = mismatch.observedSHA256
                    activeLaunchReceipt?.nativeRoundTripStatus = "unexpected_config_changes"
                    activeLaunchReceipt?.rollbackOutcome = "preserved_post_exit_config_discrepancy"
                    receiptFailure = " Post-exit config differed from the exact prepared or canonical serializer form; preserved it."
                } catch {
                    activeLaunchReceipt?.observedConfigSHA256AtExit = (try? Data(contentsOf: configURL))
                        .map { LobbyGate.digest(data: $0) }
                    activeLaunchReceipt?.nativeRoundTripStatus = "inspection_or_seed_restore_failed"
                    activeLaunchReceipt?.rollbackOutcome = "preserved_post_exit_config_discrepancy"
                    receiptFailure = " Post-exit config verification/restoration failed; preserved it: \(error.localizedDescription)"
                }
            } else {
                activeLaunchReceipt?.nativeRoundTripStatus = "exact_child_or_config_receipt_unavailable"
                activeLaunchReceipt?.rollbackOutcome = "preserved_post_exit_config_discrepancy"
                receiptFailure = " Exact child/config receipt unavailable at exit; no config rollback was attempted."
            }
            do {
                try saveActiveLaunchReceipt()
                finalReceiptSaved = true
            } catch { receiptFailure += " Launch receipt update failed: \(error.localizedDescription)" }
        }
        if configCleanupVerified, finalReceiptSaved, let lease = activeSessionLease {
            do {
                try NativePreviewSessionClaim.releaseAfterVerifiedCleanup(in: root, lease: lease,
                    exitedChildPID: completedPID)
                activeSessionLease = nil
            } catch {
                receiptFailure += " Active-session lease remains held because verified cleanup could not release it: \(error.localizedDescription)"
            }
        }
        message = selfTelemetryFailure.map { reason in
            "Native Metal .53 session closed after self-telemetry stop request (\(reason), status \(status)). Retain and review its private launch receipt and log."
        } ?? "Native Metal .53 session closed (status \(status)). A new private session may start after verified cleanup."
        message += receiptFailure
        activeConfigURL = nil
        activeSeedConfig = nil
        activePreparedConfigReceipt = nil
        revision += 1
    }

    func close() {
        if let process = child, !process.isRunning {
            _ = selfTelemetrySupervisor?.childExited(pid: process.processIdentifier)
            selfTelemetrySupervisor = nil
        }
        cleanupTransport()
        if lockFD >= 0 { flock(lockFD, LOCK_UN); Darwin.close(lockFD); lockFD = -1 }
    }
}

struct NativeLobbyView: View {
    @ObservedObject var model: NativeLobbyModel

    var body: some View {
        let _ = model.revision
        VStack(alignment: .leading, spacing: 10) {
            Text("Smash Couch Native Metal .53 Preview").font(.largeTitle.bold())
            Text("Private native Metal .53 session · one game child at a time · Xbox B/X attack, A special/back, Y jump.")
            Text("For each unassigned pad: use its D-pad to choose P1–P\(model.book.capacity), press Menu to join or reclaim, then release all controls. After it is ready, press Menu again or click Launch; close the game normally to finish.")
                .font(.callout)
            HStack {
                Text("Logical players")
                Picker("Logical players", selection: Binding(get: { model.preferences.capacity },
                    set: { model.setCapacity($0) })) {
                    ForEach(1...8, id: \.self) { Text("\($0)").tag($0) }
                }.frame(width: 90).labelsHidden().disabled(model.running)
                Spacer()
                Text("\(model.controllers.count) currently connected").foregroundStyle(.secondary)
            }

            if !model.controllerCards.isEmpty {
                VStack(alignment: .leading, spacing: 3) {
                    Text("Unassigned controller activity").font(.headline)
                    ForEach(model.controllerCards) { card in
                        HStack(spacing: 7) {
                            Circle().fill(card.active ? Color.green : Color.gray).frame(width: 8, height: 8)
                            Text("\(card.name) · selecting P\(card.selectedSlot + 1)")
                            Text(card.active ? "button activity" : "waiting")
                                .foregroundStyle(card.active ? .primary : .secondary)
                            Button("Identify") { model.pulseUnassignedController(card.id) }
                        }.font(.caption)
                    }
                }
            }

            ScrollView {
                VStack(spacing: 4) {
                    ForEach(0..<8, id: \.self) { slot in
                        HStack(spacing: 8) {
                            Text("P\(slot + 1)").font(.headline).frame(width: 42, alignment: .leading)
                            TextField("Slot label", text: Binding(
                                get: { model.preferences.labels[slot] },
                                set: { model.setLabel($0, slot: slot) }
                            )).frame(width: 150).disabled(model.running)
                            Text(slot < model.book.capacity ? model.status(slot) : "Above capacity")
                                .font(.caption).frame(maxWidth: .infinity, alignment: .leading)
                            if let selection = model.selectedByText(slot) {
                                Text(selection).font(.caption).foregroundStyle(.secondary)
                            }
                            Button("Identify") { model.pulse(slot: slot) }
                                .disabled(model.book.slots[slot].connection == nil)
                        }
                        .padding(6)
                        .background(model.book.slots[slot].reserved ? Color.accentColor.opacity(0.10) : Color.clear)
                        .cornerRadius(6)
                    }
                }
            }.frame(minHeight: 170, maxHeight: 290)

            Text(model.message).font(.callout).foregroundStyle(.secondary).frame(minHeight: 40, alignment: .leading)
            HStack {
                Spacer()
                Button("Launch Native Metal .53") { model.launch() }
                    .buttonStyle(.borderedProminent)
                    .disabled(model.running || !model.book.slots.prefix(model.book.capacity).contains(where: { $0.connection != nil }))
            }
            Text("Slot labels stay private and never identify a controller. If a controller disconnects, reconnect it and press Menu to reclaim its reserved slot.")
                .font(.caption).foregroundStyle(.secondary)
            Text("Close the game normally before closing this lobby.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(20)
        .frame(minWidth: 940, minHeight: 600)
    }
}

@MainActor final class NativeLobbyDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var window: NSWindow!
    var model: NativeLobbyModel!
    let gate: LobbyGate

    init(gate: LobbyGate) { self.gate = gate }

    // Process.isRunning becomes false before the queued termination handler
    // completes config restoration, receipt persistence, and lease release.
    // Use retained positive PID identity so a failed run() with PID 0 does not
    // strand the lobby, while an exited child remains protected until cleared.
    private var launchLifecyclePending: Bool {
        NativeLobbyLifecycle.closeMustWait(modelIsRunning: model?.running ?? false,
                                           retainedChild: model?.child)
    }

    private func explainPendingClose() {
        model?.message = "Wait for native session cleanup to finish before closing this controller lobby."
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        do { model = try NativeLobbyModel(gate: gate) }
        catch { fputs("Native Metal .53 session refused: \(error.localizedDescription)\n", stderr); NSApp.terminate(nil); return }
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 980, height: 640),
            styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Smash Couch Native Metal .53 Preview"
        window.delegate = self
        window.contentView = NSHostingView(rootView: NativeLobbyView(model: model))
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        let main = NSMenu()
        let item = NSMenuItem()
        let menu = NSMenu()
        menu.addItem(withTitle: "Quit Smash Couch Native Metal", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        item.submenu = menu
        main.addItem(item)
        NSApp.mainMenu = main
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if launchLifecyclePending {
            explainPendingClose()
            return false
        }
        return true
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        if launchLifecyclePending {
            explainPendingClose()
            return .terminateCancel
        }
        return .terminateNow
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        // Do not release the lobby lock or transport while finished(_:) may
        // still be restoring the private seed and finalizing the child lease.
        guard !launchLifecyclePending else { return }
        model?.close()
    }
}

@main struct NativeLobbyMain {
    @MainActor static func main() {
        do {
            let gate = try LobbyGate.loadDefault() // Must refuse before AppKit unless root issued a gate.
            let app = NSApplication.shared
            let delegate = NativeLobbyDelegate(gate: gate)
            app.delegate = delegate
            app.setActivationPolicy(.regular)
            withExtendedLifetime(delegate) { app.run() }
        } catch {
            fputs("Native Metal .53 session refused: \(error.localizedDescription)\n", stderr)
            exit(2)
        }
    }
}
