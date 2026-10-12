import AppKit
import SwiftUI
import GameController
import CoreHaptics
import Security
import Darwin

@_silgen_name("couch_socket") func couchSocket() -> Int32
@_silgen_name("couch_send") func couchSend(_ fd: Int32, _ path: UnsafePointer<CChar>, _ data: UnsafeRawPointer, _ count: Int32) -> Int32

@MainActor final class NativeLobbyModel: ObservableObject {
    @Published var message = "Turn on a controller and press A to join. Everyone can join at once. Press Menu to start."
    @Published var running = false
    @Published var revision = 0
    @Published var preferences = LobbyPreferences.defaults

    /// Player spots, trigger latches, timing and the packet sequence, behind one lock: in events mode the
    /// input queue sends from them too (see InputSampling.swift).
    let pipe = InputPipe()
    /// How controllers are read (UserDefaults "inputSampling"), fixed when the launcher starts.
    let sampling: InputSampling
    /// Events mode only: the input queue and its heartbeat.
    var eventInput: EventInput?
    /// A copy of the player spots for the lobby; every change goes through pipe.modify.
    var book: SlotBook { pipe.read { $0.book } }
    // The object map is only for sampling a currently connected GCController.
    // Ownership is granted only by a fresh join press (a face button or Menu) on that controller.
    var controllers: [ObjectIdentifier: (GCController, UUID)] = [:]
    var prior: [UUID: PadSample] = [:]
    var names: [UUID: String] = [:]
    /// First connected, first numbered: orders simultaneous joins and the "controllers here" strip.
    var connectionOrder: [UUID: UInt64] = [:]
    var nextConnectionOrder: UInt64 = 0
    var lastActivity: [UUID: Date] = [:]
    var lastVisibleFingerprint: [Int] = []
    /// Nintendo pads whose GameController face names follow the printed labels instead of positions.
    var labelOrderedPads: Set<ObjectIdentifier> = []
    var shortcutsResumeAt = Date.distantPast
    /// Newest pipeline-recipe save before launch, so the exit message only claims a save that happened.
    var recipesStampAtLaunch: Date?
    var timer: DispatchSourceTimer?
    var fastPolling = false
    /// The poll timer's own grid, to time how late each tick runs.
    var pollGrid = PollLateness()
    var child: Process?
    var selfTelemetrySupervisor: NativeSelfTelemetrySupervisor?
    var selfTelemetryFailure: String?
    var lockFD: Int32 = -1
    var socketFD: Int32 = -1
    var socketPath = ""
    var token: [UInt8] = []
    var session: URL?
    var logHandle: FileHandle?
    var frontmostPID = NSWorkspace.shared.frontmostApplication?.processIdentifier
    var actionsWereAllowed = true
    /// The controller whose connect message asked to click back on the lobby (see tick()).
    var clickBackName: String?
    /// Set by the app delegate: brings the lobby window back to the front after a game exits.
    var bringLobbyForward: () -> Void = {}
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

    /// Connected controllers that have not joined, in connection order.
    var waitingControllers: [UUID] {
        controllers.values.map(\.1).filter { id in !book.slots.contains { $0.connection == id } }
            .sorted { (connectionOrder[$0] ?? .max, $0.uuidString) < (connectionOrder[$1] ?? .max, $1.uuidString) }
    }

    func recentlyActive(_ id: UUID) -> Bool {
        !running && (lastActivity[id]?.timeIntervalSinceNow ?? -5) > -0.35
    }

    init(gate: LobbyGate) throws {
        self.gate = gate
        sampling = InputSampling.from(UserDefaults.standard.string(forKey: InputSampling.defaultsKey))
        let lockPath = root.appendingPathComponent("launcher.lock")
        lockFD = open(lockPath.path, O_RDWR | O_CREAT | O_NOFOLLOW, 0o600)
        guard lockFD >= 0, flock(lockFD, LOCK_EX | LOCK_NB) == 0 else {
            throw LobbyError("Another native Metal controller lobby owns this private root")
        }
        loadPrivatePreferences()

        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification,
            object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated {
                self?.frontmostPID = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier
            }
        }
        GCController.shouldMonitorBackgroundEvents = true
        NSLog("SmashCouch input sampling: %@ (UserDefaults %@)", sampling.rawValue, InputSampling.defaultsKey)
        if sampling == .events { eventInput = EventInput(pipe: pipe) }
        NotificationCenter.default.addObserver(forName: .GCControllerDidConnect, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated { if let pad = note.object as? GCController { self?.connected(pad) } }
        }
        NotificationCenter.default.addObserver(forName: .GCControllerDidDisconnect, object: nil, queue: .main) { [weak self] note in
            MainActor.assumeIsolated { if let pad = note.object as? GCController { self?.disconnected(pad) } }
        }
        for pad in GCController.controllers() { connected(pad) }
        let poll = DispatchSource.makeTimerSource(flags: .strict, queue: .main)
        let firstTick = DispatchTime.now()
        poll.schedule(deadline: firstTick, repeating: .milliseconds(100), leeway: .milliseconds(10))
        pollGrid.start(at: firstTick.uptimeNanoseconds, period: 100_000_000)
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

    func removePlayer(_ slot: Int) {
        guard !running, book.slots.indices.contains(slot) else { return }
        let state = book.slots[slot]
        guard state.reserved || state.connection != nil else { return }
        if let id = state.connection { prior[id] = nil }
        pipe.modify { input in
            if let id = state.connection { input.resetLatches(id) }
            input.book.clear(slot)
        }
        message = "P\(slot + 1) left. Press A to join again."
        revision += 1
    }

    /// Opens System Settings at Bluetooth for pairing. Only opens the pane; never changes a setting.
    /// Presses only count with the lobby in front, so the message says to come back here afterwards.
    func openBluetoothSettings() {
        let panes = ["x-apple.systempreferences:com.apple.BluetoothSettings",
                     "x-apple.systempreferences:com.apple.preferences.Bluetooth"]
        for pane in panes {
            if let url = URL(string: pane), NSWorkspace.shared.open(url) {
                message = "Pick the controller in Bluetooth settings, then click back on this window and press A to join."
                return
            }
        }
        message = "Open System Settings, then Bluetooth, to pair a controller. Then click back on this window and press A to join."
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
        let symbols = [GCInputButtonA, GCInputButtonB, GCInputButtonX, GCInputButtonY]
            .map { pad.physicalInputProfile.buttons[$0]?.sfSymbolsName ?? "?" }
        if Self.reportsNintendoLabels(name: names[id]!, buttonASymbol: symbols[0]) { labelOrderedPads.insert(key) }
        NSLog("SmashCouch controller connected: %@ face symbols a/b/x/y=%@ label-ordered=%d",
              names[id]!, symbols.joined(separator: ","), labelOrderedPads.contains(key) ? 1 : 0)
        connectionOrder[id] = nextConnectionOrder
        nextConnectionOrder += 1
        // Events mode reads this controller on the input queue from now on; the poll reads it every tick.
        prior[id] = eventInput?.attach(pad, id: id, name: names[id]!, labelOrdered: labelOrderedPads.contains(key))
            ?? sample(pad, id: id)
        // After pairing in System Settings the lobby is behind it, and presses do not count until it is in front.
        let focus = LobbyFocus(frontmostPID: frontmostPID, launcherPID: ProcessInfo.processInfo.processIdentifier,
                               gamePID: currentPID, running: running, hasControllers: true)
        clickBackName = focus.allowsActions ? nil : shortName(names[id]!)
        message = SlotBook.connectedMessage(shortName(names[id]!), actionsAllowed: focus.allowsActions)
        revision += 1
    }

    func disconnected(_ pad: GCController) {
        guard let (_, id) = controllers.removeValue(forKey: ObjectIdentifier(pad)) else { return }
        labelOrderedPads.remove(ObjectIdentifier(pad))
        let oldSlot = book.slots.firstIndex { $0.connection == id }
        eventInput?.detach(pad, id: id)
        pipe.modify { input in
            input.book.disconnect(id)
            input.latches.reset(id)
            input.detach(id)
        }
        prior.removeValue(forKey: id)
        connectionOrder.removeValue(forKey: id)
        names.removeValue(forKey: id)
        lastActivity.removeValue(forKey: id)
        pulseEngines.removeValue(forKey: id)
        if let oldSlot {
            message = "P\(oldSlot + 1)'s controller disconnected. The spot is held: turn it back on and press A."
        } else {
            message = "A controller that had not joined disconnected. No player spots changed."
        }
        revision += 1
    }

    /// GameController names face buttons by position (Xbox layout: A = bottom). A Nintendo pad
    /// whose "A" element shows the A glyph is reported by label (A = right), so it gets swapped.
    nonisolated static func reportsNintendoLabels(name: String, buttonASymbol: String) -> Bool {
        ControllerFamily.from(name: name) == .nintendo && buttonASymbol.hasPrefix("a.")
    }

    /// Poll mode: the controller's current state with its triggers latched to exactly 0 or 1 (shield), timed
    /// for the session log while a game runs. Call once per poll per controller: the latch steps on every call.
    func sample(_ pad: GCController, id: UUID) -> PadSample {
        let state = pad.input.capture()
        let raw = PadSample(state, labelOrdered: labelOrderedPads.contains(ObjectIdentifier(pad)))
        let name = names[id] ?? "Game controller"
        return pipe.modify { input in
            let latched = input.latches.latch(id, raw)
            input.timing?.record(id, name: name, eventTimestamp: state.lastEventTimestamp,
                                 eventLatency: state.lastEventLatency, raw: raw, latched: latched)
            return latched
        }
    }

    /// The lobby tick (4 ms while the lobby is in front or a game runs, else 100 ms). Poll mode: it also
    /// samples every controller, applies the samples to the spots and sends the packet. Events mode: the
    /// input queue already did all three, so the lobby reads each controller's newest sample and only hands
    /// the queue its own decisions (joins, the start hand-over, the game running).
    func tick() {
        let captured = mach_continuous_time()
        if let late = pollGrid.tick(at: DispatchTime.now().uptimeNanoseconds), fastPolling, sampling == .poll {
            pipe.modify { $0.timing?.recordTick(late: late.late, missed: late.missed) }
        }
        let focus = LobbyFocus(frontmostPID: frontmostPID, launcherPID: ProcessInfo.processInfo.processIdentifier,
                               gamePID: currentPID, running: running, hasControllers: !controllers.isEmpty)
        // Presses held while actions were not allowed must not count as fresh edges when they return.
        let actionsJustAllowed = focus.allowsActions && !actionsWereAllowed
        actionsWereAllowed = focus.allowsActions
        if actionsJustAllowed, let name = clickBackName {
            // Clicked back on the lobby after a connect: the "click this window" hint has done its job.
            clickBackName = nil
            if message == SlotBook.connectedMessage(name, actionsAllowed: false) {
                message = SlotBook.connectedMessage(name, actionsAllowed: true)
            }
        }
        updateInputActivity(focus.latencyCritical)
        if focus.fastPoll != fastPolling {
            fastPolling = focus.fastPoll
            let firstTick = DispatchTime.now()
            timer?.schedule(deadline: firstTick, repeating: .milliseconds(fastPolling ? 4 : 100),
                            leeway: .microseconds(fastPolling ? 250 : 10_000))
            pollGrid.start(at: firstTick.uptimeNanoseconds, period: fastPolling ? 4_000_000 : 100_000_000)
            eventInput?.setPace(fast: fastPolling)
        }

        let time = Date()
        var joinRequests: [SlotJoinRequest] = []
        var launchRequested = false
        var launchTooSoon = false
        let latest = sampling == .events ? pipe.read { $0.latest } : [:]
        for (pad, id) in controllers.values {
            let now = sampling == .events ? latest[id] ?? prior[id] ?? .neutral : sample(pad, id: id)
            let before = actionsJustAllowed ? now : (prior[id] ?? now)
            prior[id] = now
            if now.buttons != before.buttons || axesChanged(now.axes, before.axes) { lastActivity[id] = time }
            guard let slot = book.slots.firstIndex(where: { $0.connection == id }) else {
                // Any face button or Menu joins, in the lobby or mid-game; no slot choice needed.
                if focus.allowsActions && SlotBook.isJoinPress(now, before) {
                    joinRequests.append(SlotJoinRequest(connection: id, order: connectionOrder[id] ?? .max))
                }
                continue
            }
            if focus.allowsActions && !running && book.slots[slot].ready && time >= shortcutsResumeAt {
                // Lobby-only controller shortcuts: View leaves the slot, Menu starts the game.
                // Each must be the only input held (axes neutral).
                if now.buttons == 1 << 15 && !before.down(15) && now.axes.allSatisfy({ abs($0) < 0.08 }) {
                    removePlayer(slot)
                    continue
                }
                if book.isLaunchPress(id, current: now, previous: before) {
                    if book.canLaunch(id, current: now, previous: before, at: time) { launchRequested = true } else { launchTooSoon = true }
                }
            }
            // Live input never depends on which app is in front; packets carry it only while the game runs.
            if sampling == .poll { pipe.modify { $0.book.update(id, sample: now) } }
        }

        if !joinRequests.isEmpty {
            // Joins win the poll: a Menu press in the same poll as a join never starts the game.
            let outcomes = pipe.modify { $0.book.joinAll(joinRequests, at: time) }
            for (_, outcome) in outcomes {
                if case let .joined(slot) = outcome { pulse(slot: slot, announce: false) }
                if case let .reclaimed(slot) = outcome { pulse(slot: slot, announce: false) }
            }
            if let joined = SlotBook.joinMessage(outcomes.map(\.1), capacity: book.capacity, running: running) {
                message = joined
                NSLog("SmashCouch joins: %@", outcomes.map { "\(names[$0.0].map(shortName) ?? "controller")=\($0.1)" }
                    .joined(separator: ", "))
            }
        } else if launchRequested && !running {
            // Several Menu presses in one poll start the game once. The Menu press (and anything else
            // held now) stays out of the game until released; sticks, triggers and new presses do not wait.
            pipe.modify { $0.book.withholdHeldButtons() }
            launch()
        } else if launchTooSoon && !running {
            message = "Someone just joined. Press Menu again to start."
        }

        if sampling == .poll {
            pipe.modify { $0.send(captureTime: captured, enabled: focus.inputLive) }
        } else {
            // A spot joined in this tick by a controller that already let go is ready at once, as on the
            // next poll; the input queue's next packet (at most a heartbeat away) carries all of it.
            pipe.modify { input in
                input.live = focus.inputLive
                input.applyLatest()
            }
        }
        if pipe.read({ $0.timing?.isDue(at: DispatchTime.now().uptimeNanoseconds) == true }) { flushTiming(reason: "periodic") }
        let visible = visibleFingerprint()
        if visible != lastVisibleFingerprint {
            lastVisibleFingerprint = visible
            revision += 1
        }
    }

    /// Everything the lobby view shows that tick() can change. While the game runs, button
    /// activity is excluded: the lobby is hidden and must not re-render at the 4 ms input rate.
    private func visibleFingerprint() -> [Int] {
        let waiting = waitingControllers
        var values = [controllers.count, running ? 1 : 0, book.visibleSlotCount(waiting: waiting.count), book.capacity]
        for (index, slot) in book.slots.enumerated() {
            let id = slot.connection
            values.append(LobbyFingerprint.slot(index, slot, sample: id.flatMap { prior[$0] },
                                                active: id.map(recentlyActive) ?? false, running: running))
        }
        values += waiting.map { id in
            LobbyFingerprint.waiting(sample: prior[id], active: recentlyActive(id), running: running)
        }
        return values
    }

    /// Appends this window's input timing to the session log (one write) and starts the next window.
    func flushTiming(reason: String) {
        guard let log = logHandle else { return }
        let connected = Set(controllers.values.map(\.1))
        let events = sampling == .events
        let lines: [String] = pipe.modify { input in
            guard input.timing != nil else { return [] }
            var players: [UUID: Int] = [:]
            for (index, slot) in input.book.slots.enumerated() {
                if let id = slot.connection { players[id] = index }
            }
            let now = DispatchTime.now().uptimeNanoseconds
            let eventLine = events ? input.eventLine(at: now, reason: reason) : nil
            let lines = input.timing?.flush(at: now, players: players, connected: connected, reason: reason) ?? []
            return lines + (eventLine.map { [$0] } ?? [])
        }
        InputTiming.append(lines, to: log.fileDescriptor)
    }

    private func axesChanged(_ first: [Float], _ second: [Float]) -> Bool {
        guard first.count == second.count else { return true }
        return zip(first, second).contains { abs($0 - $1) >= 0.12 }
    }

    func pulse(slot: Int, announce: Bool = true) {
        guard book.slots.indices.contains(slot), let id = book.slots[slot].connection else { return }
        pulse(connection: id, label: "P\(slot + 1)", announce: announce)
    }

    func pulseUnassignedController(_ id: UUID) {
        guard book.slots.firstIndex(where: { $0.connection == id }) == nil else { return }
        pulse(connection: id, label: names[id] ?? "connected controller")
    }

    /// Haptic work (engine creation/start) runs off the main queue so a pulse never delays the
    /// 4 ms controller poll; `announce` is false for pulses triggered by tick() itself.
    private func pulse(connection id: UUID, label: String, announce: Bool = true) {
        guard let (pad, _) = controllers.values.first(where: { $0.1 == id }) else { return }
        guard let haptics = pad.haptics else {
            if announce { message = "\(label) has no vibration; watch its card light up when you press a button." }
            return
        }
        nonisolated(unsafe) let device = haptics
        DispatchQueue.global(qos: .utility).async {
            var result: CHHapticEngine?
            if let engine = device.createEngine(withLocality: .default) {
                do {
                    try engine.start()
                    let event = CHHapticEvent(eventType: .hapticTransient,
                        parameters: [.init(parameterID: .hapticIntensity, value: 0.65),
                                     .init(parameterID: .hapticSharpness, value: 0.4)], relativeTime: 0)
                    let pattern = try CHHapticPattern(events: [event], parameters: [])
                    try engine.makePlayer(with: pattern).start(atTime: 0)
                    result = engine
                } catch {}
            }
            nonisolated(unsafe) let started = result
            DispatchQueue.main.async {
                MainActor.assumeIsolated {
                    if let started { self.pulseEngines[id] = started }
                    if announce {
                        self.message = started != nil ? "\(label) vibrated." :
                            "\(label) has no vibration; watch its card light up when you press a button."
                    }
                }
            }
        }
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
        pipe.modify { $0.book.suspend() }
        recordSelfTelemetry(summary)
        if summary.survivorObserved {
            message = "Exact native child remains alive after bounded TERM/KILL. Input is neutral and supervision is retained; recovery is not guaranteed."
        } else {
            message = "Native self-telemetry supervisor requested stop: \(summary.stopReason ?? summary.status). Input is neutral while the exact child exits."
        }
    }

    func requestStopOfRetainedChild(reason: String) {
        guard let game = child, game.isRunning, game.processIdentifier > 0 else { return }
        pipe.modify { $0.book.suspend() }
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

    /// Newest modification time of the game's saved Metal pipeline recipes, if any.
    func recipesStamp() -> Date? {
        let recipes = root.appendingPathComponent("Data/games/01006a800016e000/cache/metal-pipeline-recipes", isDirectory: true)
        guard let walk = FileManager.default.enumerator(at: recipes, includingPropertiesForKeys: [.contentModificationDateKey]) else { return nil }
        return walk.compactMap { item -> Date? in
            guard let url = item as? URL, url.lastPathComponent == "pipeline-recipes.bin" else { return nil }
            return try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
        }.max()
    }

    func launch() {
        recipesStampAtLaunch = recipesStamp()
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
        // Spots never move, so the profile count covers every joined or held spot.
        let profiles = SlotBook.launchProfileCount(joinedOrHeld: book.takenSlots)
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
                capacity: profiles, labels: Array(preferences.labels.prefix(profiles)),
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
            socketFD = couchSocket()
            guard socketFD >= 0 else { throw LobbyError("Could not create the controller input socket") }

            let logStamp = ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
            let log = root.appendingPathComponent("session-\(logStamp)-\(UUID().uuidString.prefix(6)).log")
            FileManager.default.createFile(atPath: log.path, contents: nil)
            logHandle = try FileHandle(forWritingTo: log)
            InputTiming.makeAppendOnly(logHandle!.fileDescriptor)

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
                capacity: profiles, labels: preferences.labels)
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
            let fd = socketFD, path = socketPath
            let transport = InputTransport(token: token) { packet in
                path.withCString { path in
                    packet.withUnsafeBytes { couchSend(fd, path, $0.baseAddress!, Int32(packet.count)) }
                }
            }
            pipe.modify { input in
                input.book.beginSession(profiles: profiles)
                input.timing = InputTimingSession(startedAt: DispatchTime.now().uptimeNanoseconds)
                input.startTransport(transport)
            }
            if sampling == .events { InputTiming.append([InputSampling.sessionLine()], to: logHandle?.fileDescriptor ?? -1) }
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
            let joined = book.slots.filter { $0.connection != nil }.count
            message = SlotBook.playingMessage(joined: joined, profiles: profiles)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
                self?.activateChild(process, attemptsRemaining: 40)
            }
        } catch {
            let refusal = "Native Metal .53 session refused: \(error.localizedDescription)"
            if let game = child, game.isRunning {
                running = true
                pipe.modify { $0.book.suspend() }
                requestStopOfRetainedChild(reason: "launcher_setup_failure")
            } else {
                running = false
                pipe.modify { $0.book.endSession() }
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
        // No packet can go out once this returns (sends happen inside the pipe's lock), so the socket can close.
        pipe.modify { input in
            input.timing = nil
            input.transport = nil
        }
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
        flushTiming(reason: "exit")
        cleanupTransport()
        pipe.modify { input in
            input.book.withholdHeldButtons()
            input.book.endSession()
        }
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
        shortcutsResumeAt = Date().addingTimeInterval(1.5)
        message = selfTelemetryFailure.map { reason in
            reason == "sampledThreshold"
                ? "The game passed its \(NativeSelfTelemetryPolicy.sampledFootprintThreshold >> 30) GB memory limit and was closed (\(reason), status \(status)). Press Menu to play again."
                : "The game stopped responding and was closed (\(reason), status \(status)). New shaders from this session were not saved. Press Menu to play again."
        } ?? (status == 0
            ? (recipesStamp().map { $0 > (recipesStampAtLaunch ?? .distantPast) } == true
                ? "Game closed normally and saved the shaders it learned, so those stutter less next time. Press Menu to play again."
                : "Game closed normally. Press Menu to play again.")
            : "The game closed unexpectedly (status \(status)). Press Menu to play again.")
        message += receiptFailure
        activeConfigURL = nil
        activeSeedConfig = nil
        activePreparedConfigReceipt = nil
        revision += 1
        bringLobbyForward()
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

extension PadSample {
    /// A controller state as packet buttons and axes, triggers still analog (the pipe latches them). The same
    /// for the poll's capture() and for events mode's buffered states.
    init(_ state: any GCDevicePhysicalInputState, labelOrdered: Bool) {
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
        if labelOrdered { bits = PadSample.positionalFaces(fromLabelOrdered: bits) }
        self.init(buttons: bits, axes: [left?.xAxis.value ?? 0, left?.yAxis.value ?? 0,
            right?.xAxis.value ?? 0, right?.yAxis.value ?? 0,
            state.buttons[.leftTrigger]?.pressedInput.value ?? 0,
            state.buttons[.rightTrigger]?.pressedInput.value ?? 0])
    }
}

/// Events mode (opt-in; see InputSampling.swift). Each controller's GameController input states arrive
/// on one serial user-interactive queue, and every state that changes what the controller sends goes out as
/// a packet at once. A heartbeat on the same queue (4 ms, or 100 ms at the lobby poll's slow pace) drains
/// anything still buffered, sends when nothing else did, and watches for a controller whose buffered states
/// stop coming (BufferedInputWatch): that controller is then read by capture() on every heartbeat, the poll
/// but off the main thread.
/// Not main-actor isolated: attach and detach run on the main thread and only set GameController's atomic
/// properties and queue work; everything else runs on `queue`. Shared state is only touched in the pipe's lock.
final class EventInput: @unchecked Sendable {
    /// Only used on `queue` (attach hands a new one over through queue.async).
    private struct Pad: @unchecked Sendable {
        let input: GCControllerLiveInput
        let name: String
        let labelOrdered: Bool
        /// The last state applied, before latching (what capture() is compared with).
        var lastRaw: PadSample
        var arrived = false
        var watch = BufferedInputWatch()
    }

    let queue = DispatchQueue(label: "local.smash.couch.controller-input", qos: .userInteractive)
    /// Marks `queue`, so a callback that arrives anywhere else is moved onto it first.
    private static let queueKey = DispatchSpecificKey<Bool>()
    private let pipe: InputPipe
    private let heartbeat: DispatchSourceTimer
    /// Only touched on `queue`.
    private var pads: [UUID: Pad] = [:]
    /// The heartbeat's pace (see setPace). Starts slow, as the lobby's poll does. Only touched on `queue`.
    private var fast = false

    init(pipe: InputPipe) {
        self.pipe = pipe
        queue.setSpecific(key: Self.queueKey, value: true)
        heartbeat = DispatchSource.makeTimerSource(flags: .strict, queue: queue)
        heartbeat.setEventHandler { [weak self] in self?.beat() }
        schedule(fast: false)
        heartbeat.resume()
    }

    /// The heartbeat keeps the lobby poll's pace (tick() passes LobbyFocus.fastPoll whenever it changes): 4 ms
    /// while a game runs or the lobby is in front, else 100 ms, so an idle launcher in the background does
    /// not wake 250 times a second.
    func setPace(fast: Bool) {
        queue.async { [self] in
            guard fast != self.fast else { return }
            self.fast = fast
            schedule(fast: fast)
        }
    }

    /// (Re)starts the heartbeat and its lateness grid at the given pace. On `queue`, or in init before it runs.
    private func schedule(fast: Bool) {
        let period = InputSampling.heartbeatPeriod(fast: fast), first = DispatchTime.now()
        heartbeat.schedule(deadline: first, repeating: .nanoseconds(Int(period)),
                           leeway: fast ? .microseconds(250) : .milliseconds(10))
        pipe.modify { $0.heartbeatGrid.start(at: first.uptimeNanoseconds, period: period) }
    }

    /// Seeds the controller's first sample from capture() (main thread, as the poll does), then moves it to
    /// buffered states on `queue`. Returns the first sample, triggers latched.
    func attach(_ pad: GCController, id: UUID, name: String, labelOrdered: Bool) -> PadSample {
        let input = pad.input
        let state = input.capture()
        let raw = PadSample(state, labelOrdered: labelOrdered)
        let stamp = state.lastEventTimestamp, age = state.lastEventLatency
        let first = pipe.modify { $0.attach(id, name: name, raw: raw, eventTimestamp: stamp, eventLatency: age) }
        let entry = Pad(input: input, name: name, labelOrdered: labelOrdered, lastRaw: raw)
        queue.async { self.pads[id] = entry }  // before the handler, so its first call finds the controller
        input.queue = queue  // first: GameController's callbacks then run on it
        input.inputStateQueueDepth = 20
        input.inputStateAvailableHandler = { [weak self] _ in self?.onQueue { $0.drain(id) } }
        return first
    }

    /// Runs `work` on `queue`: at once when already there (GameController calls back on it), else queued.
    private func onQueue(_ work: @escaping @Sendable (EventInput) -> Void) {
        if DispatchQueue.getSpecific(key: Self.queueKey) == true { work(self) } else { queue.async { work(self) } }
    }

    /// The controller disconnected: no more callbacks; anything already queued for it is ignored by the pipe.
    func detach(_ pad: GCController, id: UUID) {
        pad.input.inputStateAvailableHandler = nil
        queue.async { self.pads.removeValue(forKey: id) }
    }

    /// Every buffered state of one controller, oldest first, each straight into the pipe (on `queue`).
    private func drain(_ id: UUID) {
        guard var pad = pads[id], !pad.watch.polling else { return }
        for _ in 0..<InputSampling.drainLimit {
            guard let state = pad.input.nextInputState() else { break }
            pad.lastRaw = PadSample(state, labelOrdered: pad.labelOrdered)
            pad.arrived = true
            apply(id, pad, state)
        }
        pads[id] = pad
    }

    private func apply(_ id: UUID, _ pad: Pad, _ state: any GCDevicePhysicalInputState) {
        let stamp = state.lastEventTimestamp, age = state.lastEventLatency, raw = pad.lastRaw
        let now = DispatchTime.now().uptimeNanoseconds, captured = mach_continuous_time()
        pipe.modify {
            $0.receive(id, name: pad.name, raw: raw, eventTimestamp: stamp, eventLatency: age, at: now, captureTime: captured)
        }
    }

    /// The heartbeat (on `queue`): drain and watch every controller, then the pipe's heartbeat packet.
    private func beat() {
        for id in Array(pads.keys) {
            drain(id)
            guard var pad = pads[id] else { continue }
            let state = pad.input.capture()
            let raw = PadSample(state, labelOrdered: pad.labelOrdered)
            if pad.watch.beat(bufferedArrived: pad.arrived, captureDiffers: raw != pad.lastRaw) {
                NSLog("SmashCouch controller input: %@ changed with no buffered states for %d heartbeats; reading it every heartbeat instead",
                      pad.name, BufferedInputWatch.quietBeats)
            }
            pad.arrived = false
            if pad.watch.polling {
                pad.lastRaw = raw
                apply(id, pad, state)
            }
            pads[id] = pad
        }
        let polled = pads.values.filter { $0.watch.polling }.count
        let now = DispatchTime.now().uptimeNanoseconds, captured = mach_continuous_time()
        pipe.modify { input in
            input.counts.polledPads = polled
            input.heartbeat(at: now, captureTime: captured)
        }
    }
}

struct NativeLobbyView: View {
    @ObservedObject var model: NativeLobbyModel

    var body: some View {
        let _ = model.revision
        CouchLobbyView(scene: model.lobbyScene(), actions: LobbyActions(
            launch: { model.launch() },
            identifySlot: { model.pulse(slot: $0) },
            identifyController: { model.pulseUnassignedController($0) },
            setLabel: { model.setLabel($0, slot: $1) },
            removePlayer: { model.removePlayer($0) },
            openBluetoothSettings: { model.openBluetoothSettings() }))
    }
}

extension NativeLobbyModel {
    // Read-only snapshot for the console-style view; ownership logic stays in the model.
    func lobbyScene() -> LobbyScene {
        // Face glyphs follow the live buttons only while the lobby is showing (see visibleFingerprint).
        let waiting = waitingControllers.map { id -> WaitingControllerScene in
            let name = names[id] ?? "Game controller"
            return WaitingControllerScene(id: id, name: name, family: ControllerFamily.from(name: name),
                                          pressedFaces: running ? [] : prior[id]?.pressedFaces ?? [],
                                          active: recentlyActive(id))
        }
        let slots = (0..<8).map { index -> PlayerSlotScene in
            let state = book.slots[index]
            let label = preferences.labels.indices.contains(index) ? preferences.labels[index] : "Controller \(index + 1)"
            if let id = state.connection {
                let name = names[id] ?? "Game controller"
                return PlayerSlotScene(id: index, label: label, phase: state.ready ? .ready : .releasing, controllerName: name,
                                       family: ControllerFamily.from(name: name), activity: recentlyActive(id),
                                       pressedFaces: running ? [] : prior[id]?.pressedFaces ?? [])
            }
            // Mid-game the game only has the spots it launched with (SlotBook.launchProfileCount).
            return PlayerSlotScene(id: index, label: label, phase: state.reserved ? .reserved : .open,
                                   controllerName: nil, family: nil, activity: false,
                                   nextGameOnly: running && index >= book.capacity)
        }
        return LobbyScene(visibleSlots: book.visibleSlotCount(waiting: waiting.count), slots: slots, waiting: waiting,
                          connectedCount: controllers.count, message: message, running: running,
                          canLaunch: book.slots.prefix(book.capacity).contains(where: { $0.connection != nil }),
                          joinsOpen: !running || book.nextOpenSlot() != nil)
    }
}

@MainActor final class NativeLobbyDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var window: NSWindow!
    var model: NativeLobbyModel!
    var keyMonitor: Any?
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
        NSWindow.allowsAutomaticWindowTabbing = false  // one lobby window: keep tab items out of View/Window
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Smash Couch"
        window.delegate = self
        window.contentView = NSHostingView(rootView: NativeLobbyView(model: model))
        window.minSize = NSSize(width: 1120, height: 760)
        window.titlebarAppearsTransparent = true
        window.collectionBehavior = LobbyWindowChrome.collectionBehavior(window.collectionBehavior)
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        let menus = LobbyWindowChrome.mainMenu()
        NSApp.mainMenu = menus.main
        NSApp.windowsMenu = menus.windows
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard LobbyWindowChrome.isGlobeF(event) else { return event }
            let target = event.window
            MainActor.assumeIsolated { (target ?? NSApp.keyWindow)?.toggleFullScreen(nil) }
            return nil
        }
        model.bringLobbyForward = { [weak self] in
            guard let window = self?.window else { return }
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
        }
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
