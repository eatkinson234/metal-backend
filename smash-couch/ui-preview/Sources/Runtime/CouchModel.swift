import AppKit
import Darwin
import SwiftUI
import GameController
import CoreHaptics
import Security
import CryptoKit

@_silgen_name("couch_socket") func couchSocket() -> Int32
@_silgen_name("couch_send") func couchSend(_ fd:Int32,_ path:UnsafePointer<CChar>,_ data:UnsafeRawPointer,_ count:Int32)->Int32

@MainActor final class CouchModel: ObservableObject {
    @Published var revision=0
    @Published var message="Connect controllers. Use D-pad left/right to choose a slot, then press Menu to join."
    @Published var running=false
    @Published var muted=false
    @Published var fullscreen=true
    @Published var capacity=1
    @Published var profiles=(1...8).map { PlayerProfile(label:"Controller \($0)") }
    @Published var hud=false
    @Published var capture=false
    var book=SlotBook()
    var controllers:[ObjectIdentifier:(GCController,UUID)]=[:]
    var prior:[UUID:PadSample]=[:]
    var names:[UUID:String]=[:]
    var lastActivity:[UUID:Date]=[:]
    var lastAudibleVolume=1.0
    var timer:DispatchSourceTimer?
    var fastPolling=false
    var child:Process?
    var socketFD:Int32 = -1
    var socketPath=""
    var token:[UInt8]=[]
    var sequence:UInt64=0
    var tickCount=0
    var lastActiveTick:TimeInterval=0
    var maxActiveGapMs:Double=0
    var activeTicks=0
    var gapsOver16ms=0
    var gapsOver150ms=0
    var wasFocused=true
    var inputActivity:NSObjectProtocol?
    var frontmostPID:pid_t? = NSWorkspace.shared.frontmostApplication?.processIdentifier
    var session:URL?
    var logHandle:FileHandle?
    var pulseEngines:[UUID:CHHapticEngine]=[:]
    var lockFD:Int32 = -1
    var ownsLock=false
    let home=FileManager.default.homeDirectoryForCurrentUser
    var root:URL { home.appendingPathComponent("Library/Application Support/Smash Couch") }
    var dataRoot:URL { root.appendingPathComponent("Data") }
    var emulator:URL { root.appendingPathComponent("Ryujinx Couch.app/Contents/MacOS/Ryujinx") }
    var profileURL:URL { root.appendingPathComponent("controller-profiles.json") }
    var preferencesURL:URL { root.appendingPathComponent("session-preferences.json") }
    var currentPID:pid_t? { child?.isRunning == true ? child?.processIdentifier:nil }
    init() {
        do {
            try FileManager.default.createDirectory(at:root,withIntermediateDirectories:true)
            lockFD = open(root.appendingPathComponent("launcher.lock").path,O_RDWR|O_CREAT,0o600)
            guard lockFD >= 0 && flock(lockFD,LOCK_EX|LOCK_NB)==0 else { throw SetupError("Smash Couch is already open.") }
            ownsLock=true
            if let bytes=try? Data(contentsOf:preferencesURL),let prefs=try? JSONDecoder().decode(SessionPreferences.self,from:bytes),prefs.isValid {
                capacity=prefs.capacity;muted=prefs.muted;fullscreen=prefs.fullscreen;lastAudibleVolume=prefs.lastAudibleVolume
            }
            if let bytes=try? Data(contentsOf:profileURL), let saved=try? JSONDecoder().decode([PlayerProfile].self,from:bytes), saved.count==8 { profiles=saved }
        } catch { message=error.localizedDescription }
        NSWorkspace.shared.notificationCenter.addObserver(forName:NSWorkspace.didActivateApplicationNotification,object:nil,queue:.main) { [weak self] note in
            MainActor.assumeIsolated {
                self?.frontmostPID=(note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?.processIdentifier
            }
        }
        // This launcher is the separate input process for the emulator, Apple's documented exception.
        GCController.shouldMonitorBackgroundEvents=true
        NotificationCenter.default.addObserver(forName:.GCControllerDidConnect,object:nil,queue:.main){[weak self] n in
            MainActor.assumeIsolated { if let c=n.object as? GCController { self?.connected(c) } }
        }
        NotificationCenter.default.addObserver(forName:.GCControllerDidDisconnect,object:nil,queue:.main){[weak self] n in
            MainActor.assumeIsolated { if let c=n.object as? GCController { self?.disconnected(c) } }
        }
        for c in GCController.controllers(){connected(c)}
        // Input polling must retain its cadence while the lobby window is hidden behind the game.
        // Keep model/controller ownership on the main queue; strict dispatch timing avoids
        // the observed ~100 ms coalescing of a background AppKit run-loop Timer.
        let poll=DispatchSource.makeTimerSource(flags:.strict,queue:.main)
        poll.schedule(deadline:.now(),repeating:.milliseconds(100),leeway:.milliseconds(10))
        poll.setEventHandler { [weak self] in MainActor.assumeIsolated { self?.tick() } }
        timer=poll;poll.resume()
    }
    func connected(_ c:GCController) {
        let key=ObjectIdentifier(c);guard controllers[key]==nil else{return}
        let id=UUID();controllers[key]=(c,id);names[id]=c.vendorName ?? "Game controller"
        prior[id]=sample(c);message="Connected: \(names[id]!). Press Menu to identify and join the selected slot."
        revision += 1
    }
    func disconnected(_ c:GCController) {
        guard let (_,id)=controllers.removeValue(forKey:ObjectIdentifier(c)) else{return}
        let old=book.slots.firstIndex{$0.connection==id};book.disconnect(id);prior.removeValue(forKey:id)
        pulseEngines.removeValue(forKey:id)
        message=old.map{"Player \($0+1) disconnected. Its slot is reserved. Reconnect, choose that slot, and press Menu."} ?? "An unassigned controller disconnected."
        revision += 1
    }
    func sample(_ c:GCController)->PadSample {
        let state=c.input.capture();var b:UInt32=0
        let buttons:[(Int,GCButtonElementName)]=[(1,.a),(2,.b),(3,.x),(4,.y),(5,.leftThumbstickButton),(6,.rightThumbstickButton),(7,.leftShoulder),(8,.rightShoulder),(15,.options),(16,.menu)]
        for (bit,name) in buttons where state.buttons[name]?.pressedInput.isPressed == true {b |= 1 << bit}
        if let d=state.dpads[.directionPad] {
            if d.up.isPressed{b |= 1 << 11};if d.down.isPressed{b |= 1 << 12};if d.left.isPressed{b |= 1 << 13};if d.right.isPressed{b |= 1 << 14}
        }
        let l=state.dpads[.leftThumbstick],r=state.dpads[.rightThumbstick]
        return PadSample(buttons:b,axes:[l?.xAxis.value ?? 0,l?.yAxis.value ?? 0,r?.xAxis.value ?? 0,r?.yAxis.value ?? 0,state.buttons[.leftTrigger]?.pressedInput.value ?? 0,state.buttons[.rightTrigger]?.pressedInput.value ?? 0])
    }
    func tick() {
        // Capture one Darwin continuous-clock sample before any controller.input.capture().
        // Held controls stay fresh through heartbeats because freshness is tied to
        // this snapshot, never to last input-change time.
        let captureTime = mach_continuous_time()
        book.capacity=capacity
        let front=frontmostPID
        let ours = front == ProcessInfo.processInfo.processIdentifier || front == currentPID
        if !ours && wasFocused {book.suspend()}
        wasFocused=ours
        updateInputActivity(running && ours && !controllers.isEmpty)
        let fast=ours && !controllers.isEmpty
        if fast != fastPolling {
            fastPolling=fast
            timer?.schedule(deadline:.now(),repeating:.milliseconds(fast ? 4:100),leeway:.microseconds(fast ? 250:10_000))
        }
        let now=ProcessInfo.processInfo.systemUptime
        if ours && running {
            if lastActiveTick>0 {
                let gap=(now-lastActiveTick)*1000
                maxActiveGapMs=max(maxActiveGapMs,gap);activeTicks += 1
                if gap>16.7 {gapsOver16ms += 1};if gap>150 {gapsOver150ms += 1}
            }
            lastActiveTick=now
        } else {lastActiveTick=0}
        var joins:[UUID]=[]
        var launchRequested=false
        for (c,id) in controllers.values {
            let s=sample(c), previous=prior[id] ?? s;prior[id]=s
            if s != previous {lastActivity[id]=Date()}
            if ours {
                if !running && book.canLaunch(id,current:s,previous:previous) { launchRequested=true }
                if !book.slots.contains(where:{$0.connection==id}) {
                    if s.down(13) && !previous.down(13){book.selected=(book.selected+capacity-1)%capacity}
                    if s.down(14) && !previous.down(14){book.selected=(book.selected+1)%capacity}
                    if s.down(16) && !previous.down(16){joins.append(id)}
                }
                let layout=book.slots.firstIndex(where:{$0.connection==id}).map { profiles[$0].layout } ?? "positions"
                book.update(id,sample:s.mapped(for:layout))
            }
        }
        if joins.count==1 {
            let id=joins[0]
            if book.join(id,at:book.selected) {
                let joined=book.selected
                message="Player \(joined+1): \(profiles[joined].label). Release all controls, then press Menu again to launch."
                pulse(joined)
                // Automatic progression is only to never-used slots, never a disconnected player's slot.
                if let next=(0..<capacity).first(where:{!book.slots[$0].reserved}){book.selected=next}
            } else {message="That slot is occupied. Select an empty or disconnected slot and press Menu again."}
        } else if joins.count>1 {message="Two controllers requested the slot together. Release Menu and join one at a time."}
        if launchRequested && joins.isEmpty { book.suspend();launch() }
        if socketFD>=0 {
            sequence &+= 1;let bytes=book.packet(token:token,sequence:sequence,captureTime:captureTime,enabled:ours && running)
            let result=socketPath.withCString{p in bytes.withUnsafeBytes{couchSend(socketFD,p,$0.baseAddress!,Int32(bytes.count))}}
            if result<0 && running && tickCount%250==0 {message="Waiting for emulator input connection (\(result)). Inputs stay neutral until connected."}
        }
        tickCount += 1
        if tickCount%25==0 {revision += 1}
        if tickCount%125==0 && ownsLock {
            let status:[String:Any] = ["time":Date().timeIntervalSince1970,"running":running,"pid":Int(currentPID ?? 0),"focused":ours,"connected":controllers.count,"input_timing":["max_active_gap_ms":maxActiveGapMs,"active_ticks":activeTicks,"gaps_over_16ms":gapsOver16ms,"gaps_over_150ms":gapsOver150ms],"slots":book.slots.enumerated().map { i,s -> [String:Any] in ["player":i+1,"label":profiles[i].label,"connected":s.connection != nil,"reserved":s.reserved,"ready":s.ready,"buttons":s.sample.buttons,"axes":s.sample.axes] }]
            if let data=try? JSONSerialization.data(withJSONObject:status,options:.prettyPrinted){try? data.write(to:root.appendingPathComponent("live-status.json"),options:.atomic)}
        }
    }
    func pulse(_ i:Int) {
        guard let id=book.slots[i].connection,let (c,_)=controllers.values.first(where:{$0.1==id}),let h=c.haptics else{return}
        do {
            guard let engine=h.createEngine(withLocality:.default) else { return };try engine.start()
            let event=CHHapticEvent(eventType:.hapticTransient,parameters:[.init(parameterID:.hapticIntensity,value:0.65),.init(parameterID:.hapticSharpness,value:0.4)],relativeTime:0)
            let pattern=try CHHapticPattern(events:[event],parameters:[]);try engine.makePlayer(with:pattern).start(atTime:0)
            pulseEngines[id]=engine
        } catch {message="Player \(i+1) identified on screen; this controller's vibration is unavailable."}
    }
    func select(_ i:Int){book.selected=i;revision += 1}
    func clear(_ i:Int){book.clear(i);revision += 1}
    func saveProfiles() throws {
        guard ownsLock else { return }
        let data=try JSONEncoder().encode(profiles);try data.write(to:profileURL,options:.atomic)
        let prefs=SessionPreferences(capacity:capacity,muted:muted,fullscreen:fullscreen,lastAudibleVolume:lastAudibleVolume)
        guard prefs.isValid else { throw SetupError("Invalid session preferences") }
        try JSONEncoder().encode(prefs).write(to:preferencesURL,options:.atomic)
    }
    func emulatorRunning()->Bool {
        let p=Process();p.executableURL=URL(fileURLWithPath:"/usr/bin/pgrep");p.arguments=["-x","Ryujinx"];p.standardOutput=FileHandle.nullDevice;p.standardError=FileHandle.nullDevice
        do{try p.run();p.waitUntilExit();return p.terminationStatus==0}catch{return true}
    }
    func launch() {
        guard !running else{return}
        do {
            guard ownsLock,lockFD>=0,flock(lockFD,LOCK_EX|LOCK_NB)==0 else{throw SetupError("Another launcher owns this setup.")}
            guard !emulatorRunning() else{throw SetupError("Close every Ryujinx window first. No running emulator configuration will be edited.")}
            guard (0..<capacity).contains(where:{book.slots[$0].connection != nil}) else{throw SetupError("Join at least one controller before launching. Release all controls to enable input in the game.")}
            let manifest=try JSONDecoder().decode([String:String].self,from:Data(contentsOf:root.appendingPathComponent("build-manifest.json")))
            guard !manifest.isEmpty else{throw SetupError("Build manifest is empty.")}
            for (relative,expected) in manifest {
                let file=root.appendingPathComponent(relative)
                guard !relative.contains(".."),file.standardizedFileURL.path.hasPrefix(root.path+"/"),SHA256.hash(data:try Data(contentsOf:file)).map({String(format:"%02x",$0)}).joined()==expected else{throw SetupError("Candidate build has changed. Rebuild or restore it before launching.")}
            }
            let config=dataRoot.appendingPathComponent("Config.json"),original=try Data(contentsOf:config)
            if let current=try JSONSerialization.jsonObject(with:original) as? [String:Any],let volume=current["audio_volume"] as? Double,volume>0,volume<=1 {lastAudibleVolume=volume}
            let output=try CouchConfiguration.prepare(data:original,profiles:profiles,capacity:capacity,muted:muted,fullScreen:fullscreen,lastAudibleVolume:lastAudibleVolume)
            let x=try JSONSerialization.jsonObject(with:output) as! [String:Any]
            let dirs=x["game_dirs"] as! [String]
            let games=dirs.flatMap{(try? FileManager.default.contentsOfDirectory(at:URL(fileURLWithPath:$0),includingPropertiesForKeys:nil)) ?? []}
            guard let game=games.first(where:{$0.lastPathComponent.lowercased().contains("01006a800016e000") && ["xci","nsp"].contains($0.pathExtension.lowercased())}) else{throw SetupError("Smash base game was not found in the configured game directories.")}
            guard FileManager.default.isExecutableFile(atPath:emulator.path) else{throw SetupError("The separate Ryujinx Couch build is missing.")}
            let stamp=ISO8601DateFormatter().string(from:Date()).replacingOccurrences(of:":",with:"-")
            let backup=root.appendingPathComponent("Backups/Config-\(stamp)-\(UUID().uuidString.prefix(6)).json")
            try FileManager.default.createDirectory(at:backup.deletingLastPathComponent(),withIntermediateDirectories:true)
            try original.write(to:backup,options:.withoutOverwriting)
            guard try Data(contentsOf:config)==original,!emulatorRunning() else{throw SetupError("Configuration changed during preparation; launch cancelled.")}
            try output.write(to:config,options:.atomic);try saveProfiles()
            let temp=URL(fileURLWithPath:NSTemporaryDirectory()).appendingPathComponent("sc-\(UUID().uuidString.prefix(12))")
            try FileManager.default.createDirectory(at:temp,withIntermediateDirectories:false,attributes:[.posixPermissions:0o700]);session=temp
            socketPath=temp.appendingPathComponent("input.sock").path
            // Darwin Unix socket paths are limited to 104 bytes; use a private short /tmp directory if needed.
            if socketPath.utf8.count>=104 {
                try FileManager.default.removeItem(at:temp)
                let short=URL(fileURLWithPath:"/tmp/sc-\(getuid())-\(UUID().uuidString.prefix(12))")
                try FileManager.default.createDirectory(at:short,withIntermediateDirectories:false,attributes:[.posixPermissions:0o700]);session=short;socketPath=short.appendingPathComponent("in").path
            }
            token=Array(repeating:0,count:16);guard SecRandomCopyBytes(kSecRandomDefault,16,&token)==errSecSuccess else{throw SetupError("Unable to create session token")}
            sequence=0;lastActiveTick=0;maxActiveGapMs=0;activeTicks=0;gapsOver16ms=0;gapsOver150ms=0;socketFD=couchSocket();guard socketFD>=0 else{throw SetupError("Cannot create input transport")}
            let logs=root.appendingPathComponent("Logs");try FileManager.default.createDirectory(at:logs,withIntermediateDirectories:true)
            let log=logs.appendingPathComponent("session-\(stamp).log");FileManager.default.createFile(atPath:log.path,contents:nil)
            logHandle=try FileHandle(forWritingTo:log)
            let p=Process();p.executableURL=emulator;p.currentDirectoryURL=emulator.deletingLastPathComponent();p.arguments=["-r",dataRoot.path]
            if fullscreen{p.arguments!.append("--fullscreen")};p.arguments!.append(game.path)
            var env=ProcessInfo.processInfo.environment;env["DOTNET_DefaultStackSize"]="200000";env["SMASH_COUCH_SOCKET"]=socketPath;env["SMASH_COUCH_TOKEN"]=token.map{String(format:"%02x",$0)}.joined()
            env=DiagnosticEnvironment.prepare(env,hud:hud,capture:capture)
            p.environment=env;p.standardOutput=logHandle;p.standardError=logHandle
            p.terminationHandler={[weak self] task in DispatchQueue.main.async{self?.finished(task.terminationStatus)}}
            child=p;try p.run();running=true
            message="Running with \(capacity) fixed slots. A disconnected slot stays reserved. Close the game normally to finish."
            // Wait for AppKit to register the child window before handing it focus.
            // Do not steal focus if the user already moved to another application.
            DispatchQueue.main.asyncAfter(deadline:.now()+1.0) { [weak self] in
                guard let self=self, self.currentPID==p.processIdentifier,
                      NSWorkspace.shared.frontmostApplication?.processIdentifier==ProcessInfo.processInfo.processIdentifier else{return}
                let options=NSWorkspace.OpenConfiguration();options.activates=true;options.createsNewApplicationInstance=false
                NSWorkspace.shared.openApplication(at:self.emulator.deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent(),configuration:options) { _,_ in }
            }
        } catch {message=error.localizedDescription;cleanupTransport()}
    }
    func updateInputActivity(_ active:Bool) {
        if active && inputActivity == nil {
            // Temporary precision request only while forwarding user input.
            // Neither display sleep nor system idle sleep is disabled.
            inputActivity=ProcessInfo.processInfo.beginActivity(options:[.userInitiatedAllowingIdleSystemSleep,.latencyCritical],reason:"Forwarding game controller input to the active local game")
        } else if !active, let activity=inputActivity {
            ProcessInfo.processInfo.endActivity(activity);inputActivity=nil
        }
    }
    func cleanupTransport(){updateInputActivity(false);if socketFD>=0{close(socketFD);socketFD = -1};if let s=session{try? FileManager.default.removeItem(at:s)};session=nil;try? logHandle?.close();logHandle=nil}
    func finished(_ status:Int32){running=false;child=nil;cleanupTransport();book.suspend();message="Emulator closed (status \(status)). Saves remain in the separate Couch data folder."}
    func status(_ i:Int)->String {
        let s=book.slots[i]
        guard let id=s.connection else{return s.reserved ? "Disconnected — reserved":"Press Menu to join"}
        let activity=(lastActivity[id]?.timeIntervalSinceNow ?? -5) > -0.35 ? " • input":""
        return "\(names[id] ?? "Controller") • \(s.ready ? "ready":"release controls")\(activity)"
    }
}
