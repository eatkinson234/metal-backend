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
        environment["DOTNET_GCHeapHardLimit"] = "0xA0000000"
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
