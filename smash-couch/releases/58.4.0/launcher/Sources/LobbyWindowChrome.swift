import AppKit

// Full screen and menus for the lobby window, kept apart from the app delegate so the CPU self-test
// can check the wiring without opening a window. The lobby takes its own full-screen Space with the
// green button, View > Enter Full Screen (⌃⌘F) or Fn/Globe-F, like any Mac app.
enum LobbyWindowChrome {
    static func collectionBehavior(_ current: NSWindow.CollectionBehavior) -> NSWindow.CollectionBehavior {
        current.subtracting([.fullScreenAuxiliary, .fullScreenNone]).union(.fullScreenPrimary)
    }

    /// The app menu (Quit ⌘Q), View (Enter Full Screen ⌃⌘F; AppKit retitles it Exit Full Screen) and
    /// Window (Minimize ⌘M). Returns the Window menu too, for NSApp.windowsMenu, so macOS adds its
    /// standard window items there.
    static func mainMenu() -> (main: NSMenu, windows: NSMenu) {
        let main = NSMenu()
        func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenu {
            let menu = NSMenu(title: title)
            items.forEach(menu.addItem)
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.submenu = menu
            main.addItem(item)
            return menu
        }
        _ = submenu("Smash Couch", [NSMenuItem(title: "Quit Smash Couch",
                                               action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")])
        let fullScreen = NSMenuItem(title: "Enter Full Screen", action: #selector(NSWindow.toggleFullScreen(_:)),
                                    keyEquivalent: "f")
        fullScreen.keyEquivalentModifierMask = [.control, .command]
        _ = submenu("View", [fullScreen])
        let windows = submenu("Window", [NSMenuItem(title: "Minimize",
                                                    action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")])
        return (main, windows)
    }

    /// Fn/Globe-F with no other modifier, macOS's own full-screen shortcut. A menu key equivalent
    /// cannot carry the fn modifier, so the delegate's local key monitor checks for it.
    static func isGlobeF(_ event: NSEvent) -> Bool {
        event.type == .keyDown && event.modifierFlags.contains(.function)
            && event.modifierFlags.intersection([.command, .control, .option, .shift]).isEmpty
            && event.charactersIgnoringModifiers?.lowercased() == "f"
    }
}
