import Foundation
import Darwin

struct NativePreviewSessionLease: Codable, Equatable {
    let schemaVersion: Int
    let sessionID: String
    let launcherPID: Int32
    let claimedAtUTC: String
    var childPID: Int32?
}

enum NativePreviewSessionClaim {
    static let fileName = "native-metal-preview-active-session.json"

    // The durable O_EXCL file leases one active run. It is removed only after
    // the caller has proved child/config/receipt cleanup. A leftover or bad
    // record is deliberately never aged out or auto-reclaimed.
    static func acquire(in root: URL, sessionID: String,
                        at date: Date = Date()) throws -> NativePreviewSessionLease {
        let canonicalRoot = root.standardizedFileURL
        guard root.path == canonicalRoot.path,
              (try canonicalRoot.resourceValues(forKeys: [.isDirectoryKey])).isDirectory == true,
              let uuid = UUID(uuidString: sessionID), uuid.uuidString.lowercased() == sessionID else {
            throw LobbyError("Native Metal active-session root or session ID is invalid")
        }

        var cursor = canonicalRoot
        while true {
            guard (try cursor.resourceValues(forKeys: [.isSymbolicLinkKey])).isSymbolicLink != true else {
                throw LobbyError("Symlink in Native Metal active-session path")
            }
            if cursor.path == "/" { break }
            let parent = cursor.deletingLastPathComponent()
            if parent.path == cursor.path { break }
            cursor = parent
        }

        let lease = NativePreviewSessionLease(schemaVersion: 1, sessionID: sessionID,
            launcherPID: getpid(), claimedAtUTC: ISO8601DateFormatter().string(from: date), childPID: nil)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let payload = try encoder.encode(lease) + Data([0x0a])
        let leaseURL = canonicalRoot.appendingPathComponent(fileName)
        let fd = leaseURL.path.withCString {
            Darwin.open($0, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        }
        guard fd >= 0 else {
            if errno == EEXIST {
                throw LobbyError("A prior Native Metal session lease exists; inspect its receipt and child before recovery")
            }
            throw LobbyError("Could not create the private Native Metal session lease: \(String(cString: strerror(errno)))")
        }
        defer { Darwin.close(fd) }
        try writeAll(payload, to: fd)
        guard Darwin.fsync(fd) == 0 else {
            throw LobbyError("Could not sync the private Native Metal session lease")
        }
        try syncDirectory(canonicalRoot)
        return lease
    }

    // Record the exact child PID after Process.run. A failed update leaves the
    // lease fail-closed so no subsequent run can claim an unknown child.
    static func recordChildStarted(in root: URL, lease: NativePreviewSessionLease,
                                   childPID: Int32) throws -> NativePreviewSessionLease {
        guard childPID > 0 else { throw LobbyError("Native Metal child PID is invalid") }
        let leaseURL = try ownedLeaseURL(root: root)
        let current = try readLease(at: leaseURL)
        guard current == lease, current.childPID == nil, current.launcherPID == getpid() else {
            throw LobbyError("Native Metal session lease identity changed before child start")
        }

        var updated = current
        updated.childPID = childPID
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let payload = try encoder.encode(updated) + Data([0x0a])
        let fd = leaseURL.path.withCString { Darwin.open($0, O_WRONLY | O_NOFOLLOW) }
        guard fd >= 0 else { throw LobbyError("Could not reopen the owned Native Metal session lease") }
        defer { Darwin.close(fd) }
        var info = stat()
        guard Darwin.fstat(fd, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG else {
            throw LobbyError("Native Metal session lease is not a regular file")
        }
        guard Darwin.ftruncate(fd, 0) == 0 else {
            throw LobbyError("Could not update the Native Metal session child identity")
        }
        try writeAll(payload, to: fd)
        guard Darwin.fsync(fd) == 0 else {
            throw LobbyError("Could not sync the Native Metal child identity")
        }
        try syncDirectory(leaseURL.deletingLastPathComponent())
        return updated
    }

    // Called only after the caller has durably saved a final receipt and
    // verified that the child (if any) exited and the exact seed was restored.
    // The PID must match the recorded child; pre-child rollback passes nil.
    static func releaseAfterVerifiedCleanup(in root: URL, lease: NativePreviewSessionLease,
                                            exitedChildPID: Int32?) throws {
        let leaseURL = try ownedLeaseURL(root: root)
        let current = try readLease(at: leaseURL)
        guard current == lease, current.launcherPID == getpid(),
              current.childPID == exitedChildPID else {
            throw LobbyError("Native Metal session lease does not match the verified cleanup identity")
        }
        let result = leaseURL.path.withCString { Darwin.unlink($0) }
        guard result == 0 else {
            throw LobbyError("Could not release the completed Native Metal session lease")
        }
        try syncDirectory(leaseURL.deletingLastPathComponent())
    }

    private static func ownedLeaseURL(root: URL) throws -> URL {
        let canonicalRoot = root.standardizedFileURL
        guard root.path == canonicalRoot.path,
              (try canonicalRoot.resourceValues(forKeys: [.isDirectoryKey])).isDirectory == true else {
            throw LobbyError("Native Metal session root is not a canonical directory")
        }
        let leaseURL = canonicalRoot.appendingPathComponent(fileName)
        let values = try leaseURL.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
        guard values.isRegularFile == true, values.isSymbolicLink == false else {
            throw LobbyError("Native Metal active-session lease is absent or unsafe")
        }
        return leaseURL
    }

    private static func readLease(at url: URL) throws -> NativePreviewSessionLease {
        let fd = url.path.withCString { Darwin.open($0, O_RDONLY | O_NOFOLLOW) }
        guard fd >= 0 else { throw LobbyError("Could not read Native Metal active-session lease") }
        defer { Darwin.close(fd) }
        var info = stat()
        guard Darwin.fstat(fd, &info) == 0, (info.st_mode & S_IFMT) == S_IFREG,
              info.st_size > 0, info.st_size <= 4096 else {
            throw LobbyError("Native Metal active-session lease is malformed")
        }
        var data = Data()
        while data.count < Int(info.st_size) {
            var buffer = [UInt8](repeating: 0, count: Int(info.st_size) - data.count)
            let count = buffer.withUnsafeMutableBytes { raw in
                Darwin.read(fd, raw.baseAddress, raw.count)
            }
            if count < 0 && errno == EINTR { continue }
            guard count > 0 else { throw LobbyError("Could not finish reading Native Metal session identity") }
            data.append(contentsOf: buffer.prefix(count))
        }
        guard data.count == Int(info.st_size),
              let lease = try? JSONDecoder().decode(NativePreviewSessionLease.self, from: data),
              lease.schemaVersion == 1, lease.launcherPID > 0,
              UUID(uuidString: lease.sessionID)?.uuidString.lowercased() == lease.sessionID,
              lease.childPID.map({ $0 > 0 }) ?? true else {
            throw LobbyError("Native Metal active-session lease contents are invalid")
        }
        return lease
    }

    private static func writeAll(_ data: Data, to fd: Int32) throws {
        try data.withUnsafeBytes { raw in
            var offset = 0
            while offset < raw.count {
                guard let base = raw.baseAddress else {
                    throw LobbyError("Native Metal session lease payload is empty")
                }
                let written = Darwin.write(fd, base.advanced(by: offset), raw.count - offset)
                if written < 0 && errno == EINTR { continue }
                guard written > 0 else {
                    throw LobbyError("Could not finish writing Native Metal session identity")
                }
                offset += written
            }
        }
    }

    private static func syncDirectory(_ directory: URL) throws {
        let fd = directory.path.withCString { Darwin.open($0, O_RDONLY | O_DIRECTORY | O_NOFOLLOW) }
        guard fd >= 0 else { throw LobbyError("Could not open Native Metal session root to sync its lease") }
        defer { Darwin.close(fd) }
        guard Darwin.fsync(fd) == 0 else { throw LobbyError("Could not sync Native Metal session root") }
    }
}
