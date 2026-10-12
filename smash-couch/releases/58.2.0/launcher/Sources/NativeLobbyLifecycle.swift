import Foundation

enum NativeLobbyLifecycle {
    // A failed Process.run() may leave a retained Process object whose PID is 0.
    // A positive PID remains a close barrier after exit until finished(_:) clears it.
    static func closeMustWait(modelIsRunning: Bool, retainedChild: Process?) -> Bool {
        modelIsRunning || (retainedChild?.processIdentifier ?? 0) > 0
    }
}
