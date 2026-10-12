import Foundation

enum NativePreviewEnvironment {
    private static let privatePrefixes = ["SMASH_COUCH_", "MTL_", "MTLCAPTURE_", "SMASH_MVK_"]

    static func child(base: [String: String], privateData: URL, telemetry: URL,
                      nonce: String, socket: String, token: [UInt8]) -> [String: String] {
        var environment = base.filter { key, _ in
            !privatePrefixes.contains(where: key.hasPrefix) &&
            key != "METAL_PERFORMANCE_PER_FRAME_SIGNPOSTS" &&
            !["DOTNET_GC", "COMPLUS_GC"].contains(where: key.uppercased().hasPrefix)
        }
        environment["DOTNET_DefaultStackSize"] = "200000"
        environment["DOTNET_gcServer"] = "1"
        environment["DOTNET_GCHeapCount"] = "8"
        // 8 GiB managed cap. 2.5 GiB ran out (OutOfMemoryException) after ~35 minutes of
        // 8-player first-use play in rig tour2, and 4 GiB after 39 minutes in tour6 with a
        // 16k-program cache. .58 r10 removed most per-program retention (3.5 -> 2.2 GiB
        // after boot); the higher cap keeps long sessions clear of the limit and of
        // near-limit gen2 collections. The process footprint has its own sampled stop
        // (half of RAM within 8-24 GiB; 24 GiB on this 48 GiB Mac).
        environment["DOTNET_GCHeapHardLimit"] = "0x200000000"
        environment["SMASH_COUCH_REQUIRE_NATIVE_METAL"] = "1"
        environment["SMASH_COUCH_PRIVATE_DATA"] = privateData.path
        environment["SMASH_COUCH_SELF_TELEMETRY_PATH"] = telemetry.path
        environment["SMASH_COUCH_SELF_TELEMETRY_NONCE"] = nonce
        environment["SMASH_COUCH_SOCKET"] = socket
        environment["SMASH_COUCH_TOKEN"] = token.map { String(format: "%02x", $0) }.joined()
        environment["MTL_DEBUG_LAYER"] = "0"
        environment["MTL_SHADER_VALIDATION"] = "0"
        environment["MTL_HUD_ENABLED"] = "0"
        environment["MTL_CAPTURE_ENABLED"] = "0"
        environment["MTLCAPTURE_WAIT_FOR_SIGNAL"] = "0"
        return environment
    }
}
