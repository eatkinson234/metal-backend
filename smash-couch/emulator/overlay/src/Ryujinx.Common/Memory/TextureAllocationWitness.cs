using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Common.Memory
{
    /// <summary>Default-off, one exact VM-array OOM and at most two unwinding scalar contexts.</summary>
    public static class TextureAllocationWitness
    {
        public const int RecordBytes = 512;
        public const int MaximumRecords = 3;
        private static int _armed;
        private static long _attempt, _context, _firstAttempt;
        private static int _physicalWritten, _textureWritten, _unavailable;
        private static byte[] _record;
        private static FileStream _sink;
        private static Request _first;
        private static GcSnapshot[] _snapshots;
        private static int _publishedSnapshot, _refreshUsed;
        [ThreadStatic] private static Frame _physical;
        [ThreadStatic] private static Frame _texture;
        [ThreadStatic] private static long _failedAttempt;

        public static bool Armed => Volatile.Read(ref _armed) == 1;
        public static bool WriteUnavailable => Volatile.Read(ref _unavailable) != 0;

        public struct Frame
        {
            public long Id;
            public ulong Address, RangeSize, SubrangeSize, OuterCopySize;
            public int RangeCount, Segment, CastSize;
            public int Width, Height, DepthOrLayers, Stride, Format, Levels, TotalSize;
        }

        public readonly struct Scope : IDisposable
        {
            internal readonly Frame Current, Previous;
            internal readonly int Kind;
            internal Scope(int kind, Frame current, Frame previous)
            { Kind = kind; Current = current; Previous = previous; }
            public void Dispose()
            {
                if (Kind == 1) _physical = Previous;
                if (Kind == 2)
                {
                    _texture = Previous;
                    if (Current.Id == _first.Texture.Id) _failedAttempt = 0;
                }
            }
        }

        public struct Request
        {
            public readonly long Attempt, Monotonic;
            public readonly int Thread, Size;
            public readonly ulong Va;
            public readonly bool Tracked, EvaluatedContiguousAndMapped;
            internal readonly Frame Physical, Texture;
            internal int SnapshotSlot;
            internal Request(long attempt, ulong va, int size, bool tracked, bool branch)
            {
                Attempt = attempt; Monotonic = Stopwatch.GetTimestamp();
                Thread = Environment.CurrentManagedThreadId; Va = va; Size = size;
                Tracked = tracked; EvaluatedContiguousAndMapped = branch;
                Physical = _physical; Texture = _texture; SnapshotSlot = Volatile.Read(ref _publishedSnapshot);
            }
        }

        private struct GcSnapshot
        {
            public long CapturedMonotonic, Index, HeapSizeBytes, CommittedBytes, FragmentedBytes, AvailableBytes;
            public long LohSizeAfterBytes, LohFragmentedAfterBytes, EstimatedTotalMemoryBytes;
            public int Gen0, Gen1, Gen2;
            public bool Available;
        }

        public static void InitializeFromEnvironment()
        {
            string requested = Environment.GetEnvironmentVariable("SMASH_COUCH_VM_ALLOCATION_WITNESS");
            if (requested == null) return;
            if (requested != "1") throw new InvalidOperationException("VM allocation witness flag must be1 or absent.");
            string root = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("VM allocation witness requires private data.");
            InitializePrivateDirectory(root);
        }

        public static void InitializePrivateDirectory(string privateRoot)
        {
            if (Armed || _sink != null) throw new InvalidOperationException("VM allocation witness cannot rearm.");
            string root = Path.GetFullPath(privateRoot);
            if (root != privateRoot || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new InvalidOperationException("Canonical existing private root required.");
            for (DirectoryInfo entry = new(root); entry != null; entry = entry.Parent)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Private witness ancestry must not be linked.");
            string directory = Path.Combine(root, "vm-allocation-witness52");
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Existing canonical private witness directory required.");
            try
            {
                // All managed storage, configuration enumeration and GC snapshot work precede pressure.
                _record = new byte[RecordBytes];
                _snapshots = new GcSnapshot[2];
                _snapshots[0] = CaptureSnapshot();
                GcSnapshot snapshot = _snapshots[0];
                object effective;
                try { effective = GC.GetConfigurationVariables(); }
                catch (Exception) { effective = new { available = false }; }
                byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "vm-allocation-witness52-v1", fixedRecordBytes = RecordBytes, maximumRecords = MaximumRecords,
                    stopwatchFrequency = Stopwatch.Frequency, setupMonotonic = Stopwatch.GetTimestamp(),
                    requested = new { server = Environment.GetEnvironmentVariable("DOTNET_gcServer"),
                        heapCount = Environment.GetEnvironmentVariable("DOTNET_GCHeapCount"),
                        heapHardLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit") },
                    effectiveRuntimeConfiguration = effective,
                    snapshot = new { snapshot.Available, snapshot.CapturedMonotonic, snapshot.Index,
                        snapshot.HeapSizeBytes, snapshot.CommittedBytes, snapshot.FragmentedBytes, snapshot.AvailableBytes,
                        snapshot.LohSizeAfterBytes, snapshot.LohFragmentedAfterBytes, snapshot.EstimatedTotalMemoryBytes,
                        snapshot.Gen0, snapshot.Gen1, snapshot.Gen2 },
                    lastCompletedGcAvailable = snapshot.Available && snapshot.Index > 0,
                    vmSite = "Ryujinx.Memory.VirtualMemoryManagerBase.GetSpan/new-byte-size",
                    snapshotScope = "One pre-pressure metadata query. Index0 means no completed GC snapshot; missing values are unknown. Not instant allocator free space. At most one optional first-texture-correlated VM-copy query; last-valid fallback; no post-OOM or per-allocation query.",
                    gcSnapshotQueryBudget = 2, optionalNearRequestQueryBudget = 1, nearRequestTrigger = "First VM-copy request carrying the existing Texture.SynchronizeFull acquisition context", armedOnlyAfterSinkReservation = true
                });
                if (metadata.Length > 16384) throw new InvalidOperationException("Witness setup metadata budget exceeded.");
                using (FileStream init = new(Path.Combine(directory, "initialization.json"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
                { init.Write(metadata); init.Flush(true); }
                _sink = new FileStream(Path.Combine(directory, "first-oom.bin"), new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read,
                    BufferSize = 1, Options = FileOptions.WriteThrough, PreallocationSize = RecordBytes * MaximumRecords });
                _sink.SetLength(RecordBytes * MaximumRecords);
                // Warm the exact synchronous bufferless write/flush path before setting armed.
                _sink.Write(_record); _sink.Flush(true);
                Volatile.Write(ref _armed, 1);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
            }
            catch
            {
                Volatile.Write(ref _armed, 0);
                _sink?.Dispose(); _sink = null; _record = null;
                throw; // Startup refuses the requested diagnostic; it never claims to be armed.
            }
        }

        private static GcSnapshot CaptureSnapshot()
        {
            try
            {
#if TEXTURE_WITNESS_CPU_TEST
                Interlocked.Increment(ref SnapshotQueryAttemptsForTest);
                if (FailSnapshotForTest) throw new InvalidOperationException("Synthetic pre-pressure snapshot refusal");
#endif
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                GcSnapshot result = new()
                {
                    Available = true, CapturedMonotonic = Stopwatch.GetTimestamp(), Index = info.Index,
                    HeapSizeBytes = info.HeapSizeBytes, CommittedBytes = info.TotalCommittedBytes,
                    FragmentedBytes = info.FragmentedBytes, AvailableBytes = info.TotalAvailableMemoryBytes,
                    EstimatedTotalMemoryBytes = GC.GetTotalMemory(false),
                    Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2)
                };
                if (info.GenerationInfo.Length > 3)
                {
                    result.LohSizeAfterBytes = info.GenerationInfo[3].SizeAfterBytes;
                    result.LohFragmentedAfterBytes = info.GenerationInfo[3].FragmentationAfterBytes;
                }
                return result;
            }
            catch (Exception) { return default; }
        }

        public static Scope EnterPhysical(ulong address, ulong rangeSize, ulong subrangeSize,
            ulong outerCopySize, int count, int segment, int castSize)
        {
            if (!Armed || Volatile.Read(ref _firstAttempt) != 0) return default;
            Frame next = new() { Id = Interlocked.Increment(ref _context), Address = address,
                RangeSize = rangeSize, SubrangeSize = subrangeSize, OuterCopySize = outerCopySize,
                RangeCount = count, Segment = segment, CastSize = castSize };
            Scope scope = new(1, next, _physical); _physical = next; return scope;
        }

        public static Scope EnterTexture(int width, int height, int depthOrLayers, int stride,
            int format, int levels, int totalSize)
        {
            if (!Armed || Volatile.Read(ref _firstAttempt) != 0) return default;
            Frame next = new() { Id = Interlocked.Increment(ref _context), Width = width, Height = height,
                DepthOrLayers = depthOrLayers, Stride = stride, Format = format, Levels = levels, TotalSize = totalSize };
            Scope scope = new(2, next, _texture); _texture = next; return scope;
        }

        public static Request BeforeVmAllocation(ulong va, int size, bool tracked, bool evaluatedBranch)
        {
            if (!Armed || Volatile.Read(ref _firstAttempt) != 0) return default;
            Request request = new(Interlocked.Increment(ref _attempt), va, size, tracked, evaluatedBranch);
            if (request.Texture.Id != 0 && Interlocked.CompareExchange(ref _refreshUsed, 1, 0) == 0)
            {
                GcSnapshot newer = CaptureSnapshot();
                if (newer.Available)
                {
                    _snapshots[1] = newer; // Never modified again after release publication.
                    Volatile.Write(ref _publishedSnapshot, 1);
                }
            }
            request.SnapshotSlot = Volatile.Read(ref _publishedSnapshot);
            return request;
        }

        public static void RecordVmOom(in Request request, OutOfMemoryException original)
        {
            try
            {
                if (request.Attempt == 0 || Interlocked.CompareExchange(ref _firstAttempt, request.Attempt, 0) != 0) return;
                _first = request; _failedAttempt = request.Attempt;
#if TEXTURE_WITNESS_CPU_TEST
                OriginalExceptionForTest = original;
#endif
                WriteRecord(0, 1, default);
            }
            catch (Exception) { Volatile.Write(ref _unavailable, 1); }
        }

        public static void RecordUnwinding(in Scope scope)
        {
            try
            {
                if (_failedAttempt == 0 || _failedAttempt != _first.Attempt ||
                    Environment.CurrentManagedThreadId != _first.Thread) return;
                if (scope.Kind == 1 && scope.Current.Id == _first.Physical.Id &&
                    Interlocked.CompareExchange(ref _physicalWritten, 1, 0) == 0)
                    WriteRecord(1, 2, _first.Physical);
                if (scope.Kind == 2 && scope.Current.Id == _first.Texture.Id &&
                    Interlocked.CompareExchange(ref _textureWritten, 1, 0) == 0)
                    WriteRecord(2, 3, _first.Texture);
            }
            catch (Exception) { Volatile.Write(ref _unavailable, 1); }
        }

        private static void WriteRecord(int slot, int kind, in Frame frame)
        {
            // Exactly one winning thread writes fixed scalar slots; no formatting, enumeration or GC query.
            Span<byte> row = _record;
            row.Clear();
            int at = 0;
            void Long(long value) { BinaryPrimitives.WriteInt64LittleEndian(_record.AsSpan(at, 8), value); at += 8; }
            Long(52); Long(kind); Long(_first.Attempt); Long(_first.Thread); Long(_first.Monotonic);
            Long(unchecked((long)_first.Va)); Long(_first.Size); Long(_first.Tracked ? 1 : 0);
            Long(_first.EvaluatedContiguousAndMapped ? 1 : 0);
            Long(frame.Id); Long(unchecked((long)frame.Address)); Long(unchecked((long)frame.RangeSize));
            Long(unchecked((long)frame.SubrangeSize)); Long(unchecked((long)frame.OuterCopySize));
            Long(frame.RangeCount); Long(frame.Segment); Long(frame.CastSize);
            Long(frame.Width); Long(frame.Height); Long(frame.DepthOrLayers); Long(frame.Stride);
            Long(frame.Format); Long(frame.Levels); Long(frame.TotalSize);
            GcSnapshot snapshot = _snapshots[_first.SnapshotSlot];
            Long(snapshot.Available ? 1 : 0); Long(snapshot.CapturedMonotonic); Long(snapshot.Index);
            Long(_first.SnapshotSlot); Long(snapshot.HeapSizeBytes); Long(snapshot.CommittedBytes);
            Long(snapshot.FragmentedBytes); Long(snapshot.AvailableBytes); Long(snapshot.LohSizeAfterBytes);
            Long(snapshot.LohFragmentedAfterBytes); Long(snapshot.EstimatedTotalMemoryBytes);
            Long(snapshot.Gen0); Long(snapshot.Gen1); Long(snapshot.Gen2);
            // Slot is committed last. A zero marker means unavailable/partial; readers never infer a complete record.
            _sink.Position = slot * RecordBytes; _sink.Write(row); _sink.Flush(true);
            row[RecordBytes - 1] = 0x52;
            _sink.Position = slot * RecordBytes + RecordBytes - 1;
            _sink.Write(row.Slice(RecordBytes - 1, 1)); _sink.Flush(true);
        }

        public static void Close()
        {
            Volatile.Write(ref _armed, 0);
            try { _sink?.Dispose(); } catch (Exception) { Volatile.Write(ref _unavailable, 1); }
        }

#if TEXTURE_WITNESS_CPU_TEST
        public static bool FailSnapshotForTest;
        public static int SnapshotQueryAttemptsForTest;
        public static OutOfMemoryException OriginalExceptionForTest;
        public static void RefuseSinkForTest() => _sink.Dispose();
        public static long PhysicalScopeForTest => _physical.Id;
        public static long TextureScopeForTest => _texture.Id;
#endif
    }
}
