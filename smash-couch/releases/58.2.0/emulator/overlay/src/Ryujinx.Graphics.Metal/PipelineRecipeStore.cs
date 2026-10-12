using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal;

internal sealed class PipelineRecipeStore : IDisposable
{
    // Every recorded pipeline is rebuilt at boot (and so lands in Apple's cache);
    // unrecorded variants compile on first draw (~25 ms each when new). Three
    // overnight tours reached 8,065 records, so the cap is 16,384 / 32 MiB.
    // A .57 runtime rejects files above 8,192 records: roll the cache back with it.
    private const int MaxRecords = 16384;
    private const int MaxRecordsPerProgramKind = 64;
    private const int MaxPayloadBytes = 1024;
    private const int MaxFileBytes = 32 * 1024 * 1024;
    private const int HeaderBytes = 8 + 4 + 32 + 4;
    private const int FooterBytes = 32;
    private const int RecordFixedBytes = 32 + 32 + 1 + 2;
    private const uint FormatVersion = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCMPRC01");

    private readonly object _gate = new();
    // Orders file writes (checkpoints and the final save). Always taken before _gate.
    private readonly object _saveGate = new();
    private int _checkpoints;
    private int _checkpointFailures;
    private int _declinedByLimit;
    private bool _totalLimitLogged;
    private bool _programKindLimitLogged;
    private bool _fileLimitLogged;

    public int Checkpoints => Volatile.Read(ref _checkpoints);
    public int CheckpointFailures => Volatile.Read(ref _checkpointFailures);
    public int DeclinedByLimit => Volatile.Read(ref _declinedByLimit);
    private readonly string? _directory;
    private readonly string? _recipesPath;
    private readonly string? _lockPath;
    private readonly byte[] _compatibilityHash;
    private readonly Action<string>? _log;
    private readonly List<Recipe> _records = [];
    private readonly HashSet<RecordIdentity> _recordIdentities = [];
    private readonly Dictionary<ProgramKindKey, List<Recipe>> _byProgramAndKind = [];
    private readonly HashSet<GroupKindKey> _groups = [];

    private FileStream? _lockStream;
    private FileFingerprint? _baselineFingerprint;
    private bool _baselineWasMissing;
    private bool _enabled;
    private bool _dirty;
    private bool _disposed;
    private int _addedCount;
    private int _serializedLength = HeaderBytes + FooterBytes;
    private string _failure = string.Empty;

    private PipelineRecipeStore(string? directory, byte[] compatibilityHash, Action<string>? log)
    {
        _directory = directory;
        _recipesPath = directory is null ? null : Path.Combine(directory, "pipeline-recipes.bin");
        _lockPath = directory is null ? null : Path.Combine(directory, "pipeline-recipes.lock");
        _compatibilityHash = compatibilityHash;
        _log = log;
    }

    public static PipelineRecipeStore Open(string? directory, string compatibilityKey, Action<string>? log = null)
    {
        if (compatibilityKey is null)
        {
            throw new ArgumentNullException(nameof(compatibilityKey));
        }

        if (compatibilityKey.Length == 0)
        {
            throw new ArgumentException("Compatibility key cannot be empty.", nameof(compatibilityKey));
        }

        byte[] compatibilityHash = SHA256.HashData(Encoding.UTF8.GetBytes(compatibilityKey));

        if (directory is null)
        {
            return new PipelineRecipeStore(null, compatibilityHash, log);
        }

        ValidateCanonicalAbsoluteDirectory(directory);

        PipelineRecipeStore store = new(directory, compatibilityHash, log);

        try
        {
            store.Initialize();
        }
        catch (Exception exception)
        {
            store.FailClosed("Could not open pipeline recipe cache", exception);
        }

        return store;
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled && !_disposed;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public int AddedCount
    {
        get
        {
            lock (_gate)
            {
                return _addedCount;
            }
        }
    }

    public string Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public byte[][] Find(string programKey, byte kind)
    {
        string normalizedKey = NormalizeKey(programKey, nameof(programKey));
        ValidateKind(kind, nameof(kind));

        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_enabled || !_byProgramAndKind.TryGetValue(new ProgramKindKey(normalizedKey, kind), out List<Recipe>? recipes))
            {
                return [];
            }

            byte[][] result = new byte[recipes.Count][];
            for (int index = 0; index < recipes.Count; index++)
            {
                result[index] = recipes[index].Payload.ToArray();
            }

            return result;
        }
    }

    public bool HasGroup(string groupKey, byte kind)
    {
        string normalizedKey = NormalizeKey(groupKey, nameof(groupKey));
        ValidateKind(kind, nameof(kind));

        lock (_gate)
        {
            ThrowIfDisposed();
            return _enabled && _groups.Contains(new GroupKindKey(normalizedKey, kind));
        }
    }

    // alreadyHeld: the store held this exact record before the call (false for a limit refusal).
    public bool Record(string groupKey, string programKey, byte kind, ReadOnlySpan<byte> payload, out bool alreadyHeld)
    {
        alreadyHeld = false;
        string normalizedGroupKey = NormalizeKey(groupKey, nameof(groupKey));
        string normalizedProgramKey = NormalizeKey(programKey, nameof(programKey));
        ValidateRecipePayload(kind, payload);

        lock (_gate)
        {
            ThrowIfDisposed();

            if (!_enabled)
            {
                return false;
            }

            byte[] payloadCopy = payload.ToArray();
            Recipe recipe = new(
                Convert.FromHexString(normalizedGroupKey),
                Convert.FromHexString(normalizedProgramKey),
                normalizedGroupKey,
                normalizedProgramKey,
                kind,
                payloadCopy);
            RecordIdentity identity = new(recipe);

            if (_recordIdentities.Contains(identity))
            {
                alreadyHeld = true;
                return false;
            }

            ProgramKindKey programKind = new(normalizedProgramKey, kind);
            _byProgramAndKind.TryGetValue(programKind, out List<Recipe>? sameProgramKind);
            if (_records.Count >= MaxRecords || (sameProgramKind?.Count ?? 0) >= MaxRecordsPerProgramKind)
            {
                Interlocked.Increment(ref _declinedByLimit);
                bool total = _records.Count >= MaxRecords;
                bool logged = total ? _totalLimitLogged : _programKindLimitLogged;
                if (!logged)
                {
                    // Once per reason: at the cap every unseen variant is declined, and a
                    // line per draw would flood the log for the rest of the session.
                    if (total) _totalLimitLogged = true; else _programKindLimitLogged = true;
                    SafeLog($"Pipeline recipe {(total ? "total" : "per-program/kind")} limit reached; declining such records ({_records.Count} total, {sameProgramKind?.Count ?? 0} for this program/kind). Later declines are counted, not logged.");
                }
                return false;
            }

            long proposedSize = (long)_serializedLength + RecordFixedBytes + payloadCopy.Length;
            if (proposedSize > MaxFileBytes)
            {
                Interlocked.Increment(ref _declinedByLimit);
                if (!_fileLimitLogged)
                {
                    _fileLimitLogged = true;
                    SafeLog("Pipeline recipe file-size limit reached; declining new records. Later declines are counted, not logged.");
                }
                return false;
            }

            _records.Add(recipe);
            _serializedLength = checked(_serializedLength + RecordFixedBytes + payloadCopy.Length);
            _recordIdentities.Add(identity);
            if (sameProgramKind is null)
            {
                sameProgramKind = [];
                _byProgramAndKind.Add(programKind, sameProgramKind);
            }
            sameProgramKind.Add(recipe);
            _groups.Add(new GroupKindKey(normalizedGroupKey, kind));
            _addedCount++;
            _dirty = true;
            return true;
        }
    }

    // Persists new records at a safe boundary so a killed or crashed session
    // keeps what it prepared. Runs off the render thread; only the in-memory
    // snapshot is taken under _gate. The checkpoint's own backup is removed.
    public bool Checkpoint()
    {
        lock (_saveGate)
        {
            List<Recipe> snapshot;
            lock (_gate)
            {
                if (_disposed || !_enabled || !_dirty)
                {
                    return false;
                }
                // Recipes are immutable; copying the list keeps the render
                // thread's Record path off the serialization cost.
                snapshot = [.. _records];
                _dirty = false;
            }

            try
            {
                SaveAtomically(Serialize(snapshot, _compatibilityHash), keepBackup: false);
                Interlocked.Increment(ref _checkpoints);
                return true;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _dirty = true;
                }
                Interlocked.Increment(ref _checkpointFailures);
                SafeLog($"Pipeline recipe checkpoint failed: {exception}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_saveGate)
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_enabled && _dirty)
                {
                    SaveAtomically(Serialize(_records, _compatibilityHash), keepBackup: true);
                    _dirty = false;
                }
            }
            catch (Exception exception)
            {
                SetFailure("Could not persist pipeline recipe cache", exception);
            }
            finally
            {
                _enabled = false;
                try
                {
                    _lockStream?.Dispose();
                }
                catch (Exception exception)
                {
                    SetFailure("Could not release pipeline recipe cache lock", exception);
                }

                _lockStream = null;
            }
        }
    }

    private void Initialize()
    {
        string directory = _directory!;
        string lockPath = _lockPath!;
        string recipesPath = _recipesPath!;

        EnsureDirectory(directory);
        EnsureNoReparsePointInPath(lockPath);
        EnsureRegularFileOrMissing(lockPath);

        _lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
        EnsureNoReparsePointInPath(lockPath);

        CacheSnapshot? snapshot = ReadSnapshotIfPresent(recipesPath);
        if (snapshot is null)
        {
            _baselineWasMissing = true;
        }
        else
        {
            List<Recipe> loaded = Parse(snapshot.Bytes, _compatibilityHash);
            foreach (Recipe recipe in loaded)
            {
                AddLoadedRecipe(recipe);
            }
            _baselineFingerprint = snapshot.Fingerprint;
        }

        _enabled = true;
    }

    private void AddLoadedRecipe(Recipe recipe)
    {
        _records.Add(recipe);
        _serializedLength = checked(_serializedLength + RecordFixedBytes + recipe.Payload.Length);
        _recordIdentities.Add(new RecordIdentity(recipe));

        ProgramKindKey key = new(recipe.ProgramHex, recipe.Kind);
        if (!_byProgramAndKind.TryGetValue(key, out List<Recipe>? recipes))
        {
            recipes = [];
            _byProgramAndKind.Add(key, recipes);
        }
        recipes.Add(recipe);
        _groups.Add(new GroupKindKey(recipe.GroupHex, recipe.Kind));
    }

    private void SaveAtomically(byte[] bytes, bool keepBackup)
    {
        VerifyBaselineUnchanged();

        string recipesPath = _recipesPath!;
        string directory = _directory!;
        string temporaryPath = CreateTemporaryPath(directory);
        bool tempCreated = false;

        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                tempCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            VerifyBaselineUnchanged();

            string? backupFailure = null;
            if (_baselineWasMissing)
            {
                File.Move(temporaryPath, recipesPath);
                tempCreated = false;
            }
            else
            {
                string backupPath = CreateUniqueBackupPath(directory);
                EnsureNoReparsePointInPath(recipesPath);
                VerifyBaselineUnchanged();
                File.Replace(temporaryPath, recipesPath, backupPath, ignoreMetadataErrors: true);
                tempCreated = false;

                CacheSnapshot? backup = ReadSnapshotIfPresent(backupPath);
                if (backup is null || _baselineFingerprint is null || !FingerprintsEqual(backup.Fingerprint, _baselineFingerprint))
                {
                    backupFailure = "Atomic replacement did not preserve the prior recipe file in its backup.";
                }
                else if (!keepBackup)
                {
                    try
                    {
                        EnsureNoReparsePointInPath(backupPath);
                        File.Delete(backupPath);
                    }
                    catch (Exception exception)
                    {
                        SafeLog($"Could not remove checkpoint backup {backupPath}: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }

            // The replacement has committed: re-baseline from the file now on
            // disk before any verification can throw, so one failed check does
            // not make every later save report an external change.
            CacheSnapshot? saved = ReadSnapshotIfPresent(recipesPath);
            if (saved is not null)
            {
                _baselineWasMissing = false;
                _baselineFingerprint = saved.Fingerprint;
            }
            if (backupFailure is not null)
            {
                throw new IOException(backupFailure);
            }
            if (saved is null || !saved.Bytes.AsSpan().SequenceEqual(bytes))
            {
                throw new IOException("Saved recipe file does not match the committed bytes.");
            }
        }
        finally
        {
            if (tempCreated)
            {
                try
                {
                    if (PathEntryExists(temporaryPath))
                    {
                        EnsureNoReparsePointInPath(temporaryPath);
                        File.Delete(temporaryPath);
                    }
                }
                catch (Exception exception)
                {
                    SafeLog($"Could not remove temporary recipe file: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private void VerifyBaselineUnchanged()
    {
        if (_baselineWasMissing)
        {
            EnsureNoReparsePointInPath(_recipesPath!);
            if (PathEntryExists(_recipesPath!))
            {
                throw new IOException("Recipe file appeared externally after this store opened; refusing to overwrite it.");
            }
            return;
        }

        CacheSnapshot? current = ReadSnapshotIfPresent(_recipesPath!);

        if (current is null || _baselineFingerprint is null || !FingerprintsEqual(current.Fingerprint, _baselineFingerprint))
        {
            throw new IOException("Recipe file changed externally after this store opened; refusing to overwrite it.");
        }
    }

    private static CacheSnapshot? ReadSnapshotIfPresent(string path)
    {
        EnsureNoReparsePointInPath(path);
        if (!TryGetAttributes(path, out FileAttributes attributes))
        {
            return null;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Recipe file path names a directory.");
        }

        FileFingerprint before = ReadFingerprintMetadata(path, []);
        if (before.Length < HeaderBytes + FooterBytes || before.Length > MaxFileBytes)
        {
            throw new InvalidDataException($"Recipe file size {before.Length} is outside the allowed range.");
        }

        byte[] bytes = new byte[checked((int)before.Length)];
        using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
        {
            if (stream.Length != bytes.Length)
            {
                throw new IOException("Recipe file length changed while opening.");
            }
            stream.ReadExactly(bytes);
        }

        EnsureNoReparsePointInPath(path);
        FileFingerprint after = ReadFingerprintMetadata(path, SHA256.HashData(bytes));
        if (!MetadataEqual(before, after))
        {
            throw new IOException("Recipe file metadata changed while opening.");
        }

        return new CacheSnapshot(bytes, after);
    }

    private static FileFingerprint ReadFingerprintMetadata(string path, byte[] hash)
    {
        FileInfo info = new(path);
        info.Refresh();
        if (!info.Exists)
        {
            throw new FileNotFoundException("Recipe file disappeared while inspecting it.", path);
        }

        return new FileFingerprint(hash, info.Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.Attributes);
    }

    private static bool MetadataEqual(FileFingerprint left, FileFingerprint right)
    {
        return left.Length == right.Length &&
            left.CreationTicks == right.CreationTicks &&
            left.LastWriteTicks == right.LastWriteTicks &&
            left.Attributes == right.Attributes;
    }

    private static bool FingerprintsEqual(FileFingerprint left, FileFingerprint right)
    {
        return MetadataEqual(left, right) && CryptographicOperations.FixedTimeEquals(left.Hash, right.Hash);
    }

    private static List<Recipe> Parse(byte[] bytes, byte[] compatibilityHash)
    {
        if (bytes.Length < HeaderBytes + FooterBytes || bytes.Length > MaxFileBytes)
        {
            throw new InvalidDataException("Recipe file has invalid size.");
        }

        int contentLength = bytes.Length - FooterBytes;
        byte[] expectedChecksum = SHA256.HashData(bytes.AsSpan(0, contentLength));
        if (!CryptographicOperations.FixedTimeEquals(expectedChecksum, bytes.AsSpan(contentLength, FooterBytes)))
        {
            throw new InvalidDataException("Recipe file checksum mismatch.");
        }

        ReadOnlySpan<byte> content = bytes.AsSpan(0, contentLength);
        if (!content[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Recipe file magic mismatch.");
        }

        int offset = Magic.Length;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported recipe file version {version}.");
        }

        if (!CryptographicOperations.FixedTimeEquals(content.Slice(offset, 32), compatibilityHash))
        {
            throw new InvalidDataException("Recipe file compatibility key mismatch.");
        }
        offset += 32;

        uint recordCount = BinaryPrimitives.ReadUInt32LittleEndian(content.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);
        if (recordCount > MaxRecords)
        {
            throw new InvalidDataException($"Recipe file record count {recordCount} exceeds the limit.");
        }

        List<Recipe> recipes = new(checked((int)recordCount));
        HashSet<RecordIdentity> identities = [];
        Dictionary<ProgramKindKey, int> programKindCounts = [];

        for (uint index = 0; index < recordCount; index++)
        {
            RequireRange(offset, RecordFixedBytes, contentLength, "truncated recipe record header");
            byte[] group = content.Slice(offset, 32).ToArray();
            offset += 32;
            byte[] program = content.Slice(offset, 32).ToArray();
            offset += 32;
            byte kind = content[offset++];
            ValidateKindOnDisk(kind);
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(offset, sizeof(ushort)));
            offset += sizeof(ushort);

            if (payloadLength > MaxPayloadBytes)
            {
                throw new InvalidDataException($"Recipe payload length {payloadLength} exceeds the limit.");
            }
            RequireRange(offset, payloadLength, contentLength, "truncated recipe payload");

            byte[] payload = content.Slice(offset, payloadLength).ToArray();
            offset += payloadLength;
            ValidateRecipePayloadOnDisk(kind, payloadLength);

            Recipe recipe = new(group, program, Convert.ToHexString(group), Convert.ToHexString(program), kind, payload);
            if (!identities.Add(new RecordIdentity(recipe)))
            {
                throw new InvalidDataException("Recipe file contains a duplicate record.");
            }

            ProgramKindKey key = new(recipe.ProgramHex, recipe.Kind);
            programKindCounts.TryGetValue(key, out int count);
            if (count >= MaxRecordsPerProgramKind)
            {
                throw new InvalidDataException("Recipe file exceeds the per-program/kind record limit.");
            }
            programKindCounts[key] = count + 1;
            recipes.Add(recipe);
        }

        if (offset != contentLength)
        {
            throw new InvalidDataException("Recipe file has trailing or unparsed bytes.");
        }

        return recipes;
    }

    private static byte[] Serialize(List<Recipe> records, byte[] compatibilityHash)
    {
        if (records.Count > MaxRecords)
        {
            throw new InvalidDataException("Recipe record count exceeds the limit.");
        }

        long totalLength = HeaderBytes + FooterBytes;
        foreach (Recipe recipe in records)
        {
            ValidateRecipePayloadOnDisk(recipe.Kind, recipe.Payload.Length);
            totalLength += RecordFixedBytes + recipe.Payload.Length;
        }

        if (totalLength > MaxFileBytes)
        {
            throw new InvalidDataException("Serialized recipe file exceeds the file-size limit.");
        }

        byte[] bytes = new byte[(int)totalLength];
        Span<byte> content = bytes.AsSpan(0, bytes.Length - FooterBytes);
        Magic.AsSpan().CopyTo(content);
        int offset = Magic.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(content.Slice(offset, sizeof(uint)), FormatVersion);
        offset += sizeof(uint);
        compatibilityHash.CopyTo(content.Slice(offset, 32));
        offset += 32;
        BinaryPrimitives.WriteUInt32LittleEndian(content.Slice(offset, sizeof(uint)), (uint)records.Count);
        offset += sizeof(uint);

        foreach (Recipe recipe in records.OrderBy(r => r.GroupHex, StringComparer.Ordinal)
                     .ThenBy(r => r.ProgramHex, StringComparer.Ordinal)
                     .ThenBy(r => r.Kind)
                     .ThenBy(r => r.Payload, ByteArrayComparer.Instance))
        {
            recipe.Group.CopyTo(content.Slice(offset, 32));
            offset += 32;
            recipe.Program.CopyTo(content.Slice(offset, 32));
            offset += 32;
            content[offset++] = recipe.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(content.Slice(offset, sizeof(ushort)), checked((ushort)recipe.Payload.Length));
            offset += sizeof(ushort);
            recipe.Payload.CopyTo(content.Slice(offset, recipe.Payload.Length));
            offset += recipe.Payload.Length;
        }

        byte[] checksum = SHA256.HashData(content);
        checksum.AsSpan().CopyTo(bytes.AsSpan(content.Length, FooterBytes));
        return bytes;
    }

    private static string CreateTemporaryPath(string directory)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string path = Path.Combine(directory, $".pipeline-recipes.bin.tmp-{Guid.NewGuid():N}");
            if (!PathEntryExists(path)) return path;
        }

        throw new IOException("Could not reserve a unique temporary recipe path.");
    }

    private static string CreateUniqueBackupPath(string directory)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string path = Path.Combine(directory, $"pipeline-recipes.bin.backup-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
            if (!PathEntryExists(path)) return path;
        }

        throw new IOException("Could not reserve a unique recipe backup path.");
    }

    private static void EnsureDirectory(string directory)
    {
        EnsureNoReparsePointInPath(directory);
        if (TryGetAttributes(directory, out FileAttributes attributes))
        {
            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidDataException("Recipe cache path exists but is not a directory.");
            }
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        EnsureNoReparsePointInPath(directory);
        if (!TryGetAttributes(directory, out attributes) || (attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("Recipe cache directory could not be verified.");
        }
    }

    private static void EnsureRegularFileOrMissing(string path)
    {
        EnsureNoReparsePointInPath(path);
        if (TryGetAttributes(path, out FileAttributes attributes) && (attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Recipe lock path names a directory.");
        }
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new IOException("Recipe cache path has no root.");
        string current = root;
        string remainder = fullPath[root.Length..];

        foreach (string component in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (IsSymbolicLinkPath(current))
            {
                throw new InvalidDataException($"Recipe cache path contains a symbolic link: {current}");
            }

            if (TryGetAttributes(current, out FileAttributes attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Recipe cache path contains a reparse point: {current}");
            }
        }
    }

    private static bool IsSymbolicLinkPath(string path)
    {
        try
        {
            if (new FileInfo(path).LinkTarget is not null) return true;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool PathEntryExists(string path)
    {
        if (IsSymbolicLinkPath(path)) return true;
        if (File.Exists(path) || Directory.Exists(path)) return true;
        return TryGetAttributes(path, out _);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void ValidateCanonicalAbsoluteDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("Recipe cache directory must be an absolute canonical path.", nameof(directory));
        }

        string fullPath = Path.GetFullPath(directory);
        string supplied = Path.TrimEndingDirectorySeparator(directory);
        string canonical = Path.TrimEndingDirectorySeparator(fullPath);
        if (!string.Equals(supplied, canonical, StringComparison.Ordinal))
        {
            throw new ArgumentException("Recipe cache directory must be a canonical absolute path.", nameof(directory));
        }
    }

    private static string NormalizeKey(string key, string parameterName)
    {
        if (key is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Recipe keys must be exactly 64 hexadecimal characters.", parameterName);
        }

        return key.ToUpperInvariant();
    }

    private static void ValidateKind(byte kind, string parameterName)
    {
        if (kind > 3)
        {
            throw new ArgumentOutOfRangeException(parameterName, kind, "Recipe kind must be 0 through 3.");
        }
    }

    private static void ValidateKindOnDisk(byte kind)
    {
        if (kind > 3)
        {
            throw new InvalidDataException($"Recipe kind {kind} is invalid.");
        }
    }

    private static void ValidateRecipePayload(byte kind, ReadOnlySpan<byte> payload)
    {
        ValidateKind(kind, nameof(kind));
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "Recipe payload exceeds 1024 bytes.");
        }

        if (kind == 3 ? payload.Length != 0 : payload.Length == 0)
        {
            throw new ArgumentException(kind == 3 ? "Compute recipes must have an empty payload." : "Graphics recipes must have a nonempty payload.", nameof(payload));
        }
    }

    private static void ValidateRecipePayloadOnDisk(byte kind, int payloadLength)
    {
        ValidateKindOnDisk(kind);
        if (payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException($"Recipe payload length {payloadLength} exceeds the limit.");
        }

        if (kind == 3 ? payloadLength != 0 : payloadLength == 0)
        {
            throw new InvalidDataException("Recipe payload length is invalid for its kind.");
        }
    }

    private static void RequireRange(int offset, int length, int limit, string message)
    {
        if (offset < 0 || length < 0 || offset > limit || length > limit - offset)
        {
            throw new InvalidDataException(message);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PipelineRecipeStore));
        }
    }

    private void FailClosed(string message, Exception exception)
    {
        SetFailure(message, exception);
        _enabled = false;
        try
        {
            _lockStream?.Dispose();
        }
        catch (Exception lockException)
        {
            SafeLog($"Could not release failed recipe cache lock: {lockException.GetType().Name}: {lockException.Message}");
        }
        _lockStream = null;
    }

    private void SetFailure(string message, Exception exception)
    {
        _failure = $"{message}: {exception.GetType().Name}: {exception.Message}";
        SafeLog(_failure);
    }

    private void SafeLog(string message)
    {
        try
        {
            _log?.Invoke(message);
        }
        catch
        {
            // Logging must never make cache failure escape into gameplay.
        }
    }

    private sealed class Recipe
    {
        public Recipe(byte[] group, byte[] program, string groupHex, string programHex, byte kind, byte[] payload)
        {
            Group = group;
            Program = program;
            GroupHex = groupHex;
            ProgramHex = programHex;
            Kind = kind;
            Payload = payload;
        }

        public byte[] Group { get; }
        public byte[] Program { get; }
        public string GroupHex { get; }
        public string ProgramHex { get; }
        public byte Kind { get; }
        public byte[] Payload { get; }
    }

    private sealed class RecordIdentity : IEquatable<RecordIdentity>
    {
        private readonly string _group;
        private readonly string _program;
        private readonly byte _kind;
        private readonly byte[] _payload;

        public RecordIdentity(Recipe recipe)
        {
            _group = recipe.GroupHex;
            _program = recipe.ProgramHex;
            _kind = recipe.Kind;
            _payload = recipe.Payload;
        }

        public bool Equals(RecordIdentity? other)
        {
            return other is not null && _kind == other._kind &&
                string.Equals(_group, other._group, StringComparison.Ordinal) &&
                string.Equals(_program, other._program, StringComparison.Ordinal) &&
                _payload.AsSpan().SequenceEqual(other._payload);
        }

        public override bool Equals(object? obj) => obj is RecordIdentity other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(_group, StringComparer.Ordinal);
            hash.Add(_program, StringComparer.Ordinal);
            hash.Add(_kind);
            foreach (byte value in _payload) hash.Add(value);
            return hash.ToHashCode();
        }
    }

    private readonly record struct ProgramKindKey(string ProgramHex, byte Kind);
    private readonly record struct GroupKindKey(string GroupHex, byte Kind);
    private sealed record CacheSnapshot(byte[] Bytes, FileFingerprint Fingerprint);
    private sealed record FileFingerprint(byte[] Hash, long Length, long CreationTicks, long LastWriteTicks, FileAttributes Attributes);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int commonLength = Math.Min(left.Length, right.Length);
            for (int index = 0; index < commonLength; index++)
            {
                int comparison = left[index].CompareTo(right[index]);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
