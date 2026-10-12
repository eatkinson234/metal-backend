import Foundation
do {
    let gate = try LobbyGate.loadDefault()
    print("GATE OK", gate.nativeApp)
} catch {
    print("GATE REFUSED", error)
    exit(1)
}
