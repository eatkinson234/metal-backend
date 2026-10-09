import AppKit
import SwiftUI

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    var window: NSWindow!
    var model: LobbyStore!
    var keyboardMonitor: Any?

    func applicationDidFinishLaunching(_ notification: Notification) {
        model = LobbyStore()
        let size = requestedSize ?? NSSize(width: 1180, height: 760)
        window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "Smash Couch · Design Preview"
        window.identifier = NSUserInterfaceItemIdentifier("SmashCouchDesignPreviewFinal")
        window.delegate = self
        window.minSize = NSSize(width: 900, height: 600)
        window.titlebarAppearsTransparent = true
        window.backgroundColor = NSColor(red: 0.955, green: 0.961, blue: 0.967, alpha: 1)
        window.appearance = NSAppearance(named: .aqua)
        #if LEGACY_VIEW
        window.contentView = NSHostingView(rootView: LegacyCouchView(model: model))
        #else
        window.contentView = NSHostingView(rootView: CouchView(model: model))
        #endif
        installMenus()
        keyboardMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.handleKey(event) ?? event
        }
        window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    var requestedSize: NSSize? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--size"), args.indices.contains(i + 1) else { return nil }
        let values = args[i + 1].split(separator: "x").compactMap { Double($0) }
        guard values.count == 2, values[0] >= 900, values[1] >= 600 else { return nil }
        return NSSize(width: values[0], height: values[1])
    }

    func installMenus() {
        let main = NSMenu()
        let appItem = NSMenuItem(); let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Close preview", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        appItem.submenu = appMenu; main.addItem(appItem)
        let viewItem = NSMenuItem(); let viewMenu = NSMenu(title: "View")
        for (title, action, key) in [("Play", #selector(showPlay), "1"), ("Controllers", #selector(showControllers), "2"), ("Session Settings…", #selector(showSettings), ","), ("Help", #selector(showHelp), "?"), ("Save Preview Screenshot", #selector(savePreviewScreenshot), "S")] {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key); item.target = self; viewMenu.addItem(item)
        }
        viewItem.submenu = viewMenu; main.addItem(viewItem)
        let motion = NSMenuItem(title: "Reduced Motion Preview", action: #selector(toggleReducedMotion(_:)), keyEquivalent: ""); motion.target = self; viewMenu.addItem(motion)
        let sizeMenu = NSMenu(title: "Window Size")
        let sizes = NSMenuItem(title: "Window Size", action: nil, keyEquivalent: ""); sizes.submenu = sizeMenu; viewMenu.addItem(sizes)
        for size in ["900x600", "1180x760", "1600x1000"] {
            let item = NSMenuItem(title: size, action: #selector(changeSize(_:)), keyEquivalent: ""); item.target = self; item.representedObject = size; sizeMenu.addItem(item)
        }
        let sceneItem = NSMenuItem(); let sceneMenu = NSMenu(title: "Preview Scenes")
        for scene in PreviewScene.allCases where scene != .live {
            let item = NSMenuItem(title: scene.rawValue.capitalized, action: #selector(changeScene(_:)), keyEquivalent: "")
            item.target = self; item.representedObject = scene.rawValue; sceneMenu.addItem(item)
        }
        sceneItem.submenu = sceneMenu; main.addItem(sceneItem)
        let inputItem = NSMenuItem(); let inputMenu = NSMenu(title: "Simulated Controller")
        for (title, code) in [("Next focus", "next"), ("Previous focus", "previous"), ("D-pad left", "left"), ("D-pad right", "right"), ("A / Cross: select", "confirm"), ("B / Circle: back", "back"), ("Menu / Options", "menu")] {
            let item = NSMenuItem(title: title, action: #selector(simulateInput(_:)), keyEquivalent: ""); item.target = self; item.representedObject = code; inputMenu.addItem(item)
        }
        inputItem.submenu = inputMenu; main.addItem(inputItem)
        NSApp.mainMenu = main
    }

    @objc func showPlay() { model.show(.play) }
    @objc func showControllers() { model.show(.controllers) }
    @objc func showSettings() { model.show(.settings) }
    @objc func showHelp() { model.show(.help) }
    @objc func changeScene(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let scene = PreviewScene(rawValue: raw) else { return }
        model.applyPreviewScene(scene)
    }
    @objc func simulateInput(_ sender: NSMenuItem) {
        let inputs: [String: LobbyNavigation] = ["next": .next, "previous": .previous, "left": .left, "right": .right, "confirm": .confirm, "back": .back, "menu": .menu]
        if let name = sender.representedObject as? String, let input = inputs[name] { model.navigate(input) }
    }
    @objc func changeSize(_ sender: NSMenuItem) {
        guard let size = sender.representedObject as? String else { return }
        let values = size.split(separator: "x").compactMap { Double($0) }
        guard values.count == 2 else { return }
        window.setContentSize(NSSize(width: values[0], height: values[1])); window.center()
    }
    @objc func toggleReducedMotion(_ sender: NSMenuItem) {
        model.reduceMotionOverride.toggle(); sender.state = model.reduceMotionOverride ? .on : .off
    }

    func handleKey(_ event: NSEvent) -> NSEvent? {
        guard window.isKeyWindow, window.attachedSheet == nil,
              !event.modifierFlags.contains(.command), !event.modifierFlags.contains(.control),
              !(window.firstResponder is NSTextView), !model.running else { return event }
        if event.keyCode == 53 { model.show(.play); return nil }
        if model.page == .controllers, event.keyCode == 123 || event.keyCode == 124 {
            model.navigate(event.keyCode == 123 ? .left : .right)
            return nil
        }
        return event
    }

    @objc func savePreviewScreenshot() {
        let page = model.page, scene = model.previewScene
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
            guard let self, self.model.page == page, self.model.previewScene == scene else { return }
            self.captureStablePreview()
        }
    }

    private func captureStablePreview() {
        let showingControls = window.attachedSheet != nil
        guard let view = (window.attachedSheet ?? window).contentView,
              let image = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: image)
        let path = Bundle.main.object(forInfoDictionaryKey: "SmashCouchEvidenceRoot") as? String ?? NSTemporaryDirectory()
        let name = "\(model.previewScene.rawValue)-\(model.page.rawValue)\(showingControls ? "-controls" : "")-\(Int(view.bounds.width))x\(Int(view.bounds.height))-\(Int(Date().timeIntervalSince1970)).png"
        try? image.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path).appendingPathComponent(name), options: .withoutOverwriting)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool { true }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        if let keyboardMonitor { NSEvent.removeMonitor(keyboardMonitor) }
        try? model.saveProfiles()
    }
}

@main struct CouchMain {
    @MainActor static func main() {
        let app = NSApplication.shared; let delegate = AppDelegate()
        app.delegate = delegate; app.setActivationPolicy(.regular)
        withExtendedLifetime(delegate) { app.run() }
    }
}
