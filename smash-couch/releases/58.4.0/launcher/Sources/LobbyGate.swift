import Foundation
import CryptoKit

struct LobbyError: LocalizedError {
    let reason: String
    init(_ reason: String) { self.reason = reason }
    var errorDescription: String? { reason }
}

struct LobbyGate: Decodable {
    let gateVersion: Int
    let approved: Bool
    let nativeApp: String
    let packageManifest: String
    let packageManifestSHA256: String
    let privateData: String
    let configSHA256: String
    let gamePath: String

    static let privateRootName = "native-metal-controller-lobby53-preview-r1"
    // Exact root-owned current-progress clone and relocated signed .53 package.
    // The external preparation gate remains approved:false until root admission.
    // The exact private gate and all package/configuration checks remain required.
    static let approvedSeedConfigSHA256 = "63fb88855966316187945f6287ac8790fddadb86972471c11cfe221fb8f07a53"
    static let approvedPackageManifestSHA256 = "5a1c5c6e607b8190ed93a0eba7f6c34e9f9b0449fcdef26a617a9d9e9c743ead"
    static let approvedNativeAppName = "Ryujinx Native Metal VI Cancel R1.app"
    static let approvedNativeBundleName = "Ryujinx Native Metal VI Cancel R1"
    static let approvedNativeBundleIdentifier = "local.smash.couch.native-metal-vi-cancel-r1"
    static let approvedNativeVersion = "1.3.3-metal.53-vi-cancel-r1"
    static let approvedPackageOutputDirectory = "native53-preview-packageoutputs"

    private struct Package: Decodable {
        struct App: Decodable {
            struct Entry: Decodable { let path: String; let sha256: String }
            struct InfoPlist: Decodable {
                let bundleIdentifier: String
                let bundleName: String
                let shortVersion: String
                let bundleVersion: String

                enum CodingKeys: String, CodingKey {
                    case bundleIdentifier = "CFBundleIdentifier"
                    case bundleName = "CFBundleName"
                    case shortVersion = "CFBundleShortVersionString"
                    case bundleVersion = "CFBundleVersion"
                }
            }

            let name: String
            let path: String
            let infoPlist: InfoPlist
            let files: [Entry]

            enum CodingKeys: String, CodingKey {
                case name, path, files
                case infoPlist = "info_plist"
            }
        }
        let app: App
    }

    static var privateRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Smash Couch/Lab/\(privateRootName)")
    }

    static var runtimeRoot: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Smash Couch/Runtime/native-metal-58-couch-r21", isDirectory: true)
    }

    static var approvedNativeApp: URL {
        runtimeRoot.appendingPathComponent(approvedNativeAppName)
    }

    static var approvedManifest: URL {
        runtimeRoot.appendingPathComponent("\(approvedPackageOutputDirectory)/app-manifest.json")
    }

    static func loadDefault() throws -> LobbyGate {
        try validate(gateURL: privateRoot.appendingPathComponent("preview-gate.json"),
                     root: privateRoot,
                     labRoot: privateRoot.deletingLastPathComponent(),
                     prototypeRoot: runtimeRoot)
    }

    static func digest(data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    static func digest(_ url: URL) throws -> String {
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var hash = SHA256()
        while let bytes = try file.read(upToCount: 1024 * 1024), !bytes.isEmpty { hash.update(data: bytes) }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private static func canonical(_ url: URL) -> URL { url.standardizedFileURL }

    private static func inside(_ child: URL, _ parent: URL) -> Bool {
        canonical(child).path.hasPrefix(canonical(parent).path + "/")
    }

    private static func rejectSymlinks(_ path: URL) throws {
        var cursor = canonical(path)
        while true {
            let values = try cursor.resourceValues(forKeys: [.isSymbolicLinkKey])
            if values.isSymbolicLink == true { throw LobbyError("Symlink path rejected: \(cursor.path)") }
            if cursor.path == "/" { break }
            let next = cursor.deletingLastPathComponent()
            if next.path == cursor.path { break }
            cursor = next
        }
    }

    private static func regularFile(_ path: URL) throws {
        try rejectSymlinks(path)
        guard (try path.resourceValues(forKeys: [.isRegularFileKey])).isRegularFile == true else {
            throw LobbyError("Expected a regular file: \(path.path)")
        }
    }

    // Root arguments and manifest pin are injectable only for fixture tests.
    // loadDefault always uses the fixed prototype paths and accepted hashes.
    static func validate(gateURL: URL, root: URL, labRoot: URL, prototypeRoot: URL,
                         expectedManifestSHA256: String = approvedPackageManifestSHA256,
                         expectedSeedSHA256: String = approvedSeedConfigSHA256) throws -> LobbyGate {
        let root = canonical(root)
        guard inside(root, labRoot), root.lastPathComponent == privateRootName else {
            throw LobbyError("Private lobby root must be the dedicated Lab child")
        }
        guard canonical(gateURL) == root.appendingPathComponent("preview-gate.json") else {
            throw LobbyError("Gate must be at the exact private lobby root path")
        }
        try regularFile(gateURL)
        let gate = try JSONDecoder().decode(LobbyGate.self, from: Data(contentsOf: gateURL))
        guard gate.gateVersion == 1, gate.approved else { throw LobbyError("Native controller lobby gate is not approved") }

        let data = canonical(URL(fileURLWithPath: gate.privateData))
        guard data == root.appendingPathComponent("Data"), inside(data, labRoot) else {
            throw LobbyError("Gate must name the dedicated private Data child")
        }
        try rejectSymlinks(data)
        guard (try data.resourceValues(forKeys: [.isDirectoryKey])).isDirectory == true else {
            throw LobbyError("Private lobby Data directory is absent")
        }
        guard let walk = FileManager.default.enumerator(at: data, includingPropertiesForKeys: [.isSymbolicLinkKey], options: []),
              walk.allObjects.allSatisfy({ item in
                  guard let path = item as? URL else { return false }
                  return (try? path.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == false
              }) else { throw LobbyError("Private Data contains a symlink") }

        let config = data.appendingPathComponent("Config.json")
        try regularFile(config)
        let configData = try Data(contentsOf: config)
        guard gate.configSHA256.count == 64,
              gate.configSHA256.lowercased() == expectedSeedSHA256.lowercased(),
              digest(data: configData) == gate.configSHA256.lowercased() else {
            throw LobbyError("Private seed config differs from the fixed root-approved bytes")
        }
        _ = try NativeLobbyConfiguration.validateSeed(configData)

        let manifestURL = canonical(URL(fileURLWithPath: gate.packageManifest))
        let app = canonical(URL(fileURLWithPath: gate.nativeApp))
        let expectedApp = canonical(prototypeRoot.appendingPathComponent(approvedNativeAppName))
        let expectedManifest = canonical(prototypeRoot.appendingPathComponent("\(approvedPackageOutputDirectory)/app-manifest.json"))
        guard app == expectedApp, manifestURL == expectedManifest,
              inside(manifestURL, prototypeRoot), inside(app, prototypeRoot), app.pathExtension == "app",
              manifestURL != gateURL else { throw LobbyError("Gate does not name the exact approved native Metal .53 package paths") }
        try regularFile(manifestURL)
        guard gate.packageManifestSHA256.count == 64,
              gate.packageManifestSHA256.lowercased() == expectedManifestSHA256.lowercased(),
              try digest(manifestURL) == gate.packageManifestSHA256.lowercased() else {
            throw LobbyError("Native Metal runtime package manifest hash mismatch")
        }
        let package = try JSONDecoder().decode(Package.self, from: Data(contentsOf: manifestURL))
        guard canonical(URL(fileURLWithPath: package.app.path)) == app,
              package.app.name == approvedNativeAppName,
              package.app.infoPlist.bundleIdentifier == approvedNativeBundleIdentifier,
              package.app.infoPlist.bundleName == approvedNativeBundleName,
              package.app.infoPlist.shortVersion == approvedNativeVersion,
              package.app.infoPlist.bundleVersion == approvedNativeVersion,
              !package.app.files.isEmpty else {
            throw LobbyError("Manifest identity or version differs from the exact approved native Metal .53 app")
        }
        try rejectSymlinks(app)
        var listed = Set<String>()
        for entry in package.app.files {
            guard !entry.path.hasPrefix("/"), !entry.path.split(separator: "/").contains(".."),
                  inside(app.appendingPathComponent(entry.path), app), listed.insert(entry.path).inserted,
                  entry.sha256.count == 64 else { throw LobbyError("Invalid native package file entry") }
            let file = app.appendingPathComponent(entry.path)
            try regularFile(file)
            guard try digest(file) == entry.sha256.lowercased() else {
                throw LobbyError("Native package file hash mismatch: \(entry.path)")
            }
        }
        guard let appWalk = FileManager.default.enumerator(at: app,
                includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey], options: []) else {
            throw LobbyError("Unable to enumerate native package")
        }
        var actual = Set<String>()
        for case let file as URL in appWalk {
            let props = try file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            if props.isSymbolicLink == true { throw LobbyError("Symlink inside native package") }
            if props.isRegularFile == true { actual.insert(String(file.path.dropFirst(app.path.count + 1))) }
        }
        guard actual == listed else { throw LobbyError("Native package files differ from the exact manifest") }
        guard !FileManager.default.fileExists(atPath: app.appendingPathComponent("Contents/MacOS/Config.json").path) else {
            throw LobbyError("App-local Config.json would violate the native prototype launch guard")
        }
        guard FileManager.default.isExecutableFile(atPath: app.appendingPathComponent("Contents/MacOS/Ryujinx").path) else {
            throw LobbyError("Native Metal .53 app executable is missing")
        }

        let game = canonical(URL(fileURLWithPath: gate.gamePath))
        try regularFile(game)
        let configObject = try JSONSerialization.jsonObject(with: configData) as? [String: Any]
        guard ["xci", "nsp"].contains(game.pathExtension.lowercased()),
              game.lastPathComponent.lowercased().contains("01006a800016e000"),
              (configObject?["game_dirs"] as? [String])?.contains(where: { inside(game, URL(fileURLWithPath: $0)) }) == true else {
            throw LobbyError("Gate names a game outside the private seed config's game directories")
        }
        return gate
    }
}
