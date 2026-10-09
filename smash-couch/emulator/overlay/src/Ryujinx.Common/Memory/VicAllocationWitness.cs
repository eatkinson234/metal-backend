using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Common.Memory
{
    /// <summary>Default-off, one preallocated VIC allocation-failure companion record.</summary>
    public static class VicAllocationWitness
    {
        public const int RecordBytes = 512;
        public const byte CompleteMarker = 0x56;
        public enum Caller { Unknown, Nv12Source, VicDestination, UnsupportedSource }
        //0 setup/off,1 armed,2 sole writer owns descriptor,3 Close requested,4 record finished.
        private static int _state, _winner, _unavailable, _sampling, _sampleVersion;
        private static int _descriptor = -1;
        private static long _attempt, _nextSample;
        private static byte[] _record;
        private static Snapshot _sample;
        public static bool Armed => Volatile.Read(ref _state) == 1;
        public static bool WriteUnavailable => Volatile.Read(ref _unavailable) != 0;

        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dup")]
        private static extern int Duplicate(int descriptor);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pwrite")]
        private static unsafe extern nint PositionalWrite(int descriptor, byte* bytes, nuint count, long offset);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fsync")]
        private static extern int FlushDescriptor(int descriptor);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close")]
        private static extern int CloseNative(int descriptor);

        internal struct Snapshot
        {
            internal long QueryMonotonic, Index, HeapSize, Committed, Fragmented, AvailableBytes;
            internal long LohSize, LohFragmented, EstimatedTotal;
            internal int Gen0, Gen1, Gen2;
            internal bool Available;
        }

        public readonly struct Request
        {
            internal readonly long Attempt, Monotonic, Bytes;
            internal readonly int Thread, Width, Height, Elements, ElementBytes, FrameFormat, SampleVersion;
            internal readonly Caller Site;
            internal readonly Snapshot Sample;
            internal Request(long attempt, int width, int height, int elements, int elementBytes,
                Caller site, int frameFormat, Snapshot sample, int sampleVersion)
            {
                Attempt = attempt; Monotonic = Stopwatch.GetTimestamp();
                Thread = Environment.CurrentManagedThreadId;
                Width = width; Height = height; Elements = elements; ElementBytes = elementBytes;
                Bytes = (long)elements * elementBytes; Site = site; FrameFormat = frameFormat;
                Sample = sample; SampleVersion = sampleVersion;
            }
        }

        public static void InitializeFromEnvironment()
        {
            string flag = Environment.GetEnvironmentVariable("SMASH_COUCH_VIC_ALLOCATION_WITNESS");
            if (flag == null) return;
            if (flag != "1") throw new InvalidOperationException("VIC allocation witness flag must be1 or absent.");
            string root = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
            if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("VIC allocation witness requires private data.");
            InitializePrivateDirectory(root);
        }

        public static void InitializePrivateDirectory(string privateRoot)
        {
            if (Volatile.Read(ref _state) != 0 || _descriptor >= 0) throw new InvalidOperationException("VIC allocation witness cannot rearm.");
            if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Private VIC witness requires macOS.");
            string root = Path.GetFullPath(privateRoot);
            if (root != privateRoot || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
                throw new InvalidOperationException("Canonical existing private root required.");
            for (DirectoryInfo entry = new(root); entry != null; entry = entry.Parent)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Private witness ancestry must not be linked.");
            string directory = Path.Combine(root, "vic-allocation-witness-r1");
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Existing canonical VIC witness directory required.");
            try
            {
                _record = new byte[RecordBytes];
                _sample = CaptureSnapshot();
                _sampleVersion = 2;
                _nextSample = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema = "vic-allocation-witness-r1-v1", recordBytes = RecordBytes,
                    maximumRecords = 1, completeMarker = CompleteMarker,
                    stopwatchFrequency = Stopwatch.Frequency,
                    sites = new { unknown = 0, nv12Source = 1, vicDestination = 2, unsupportedSource = 3 },
                    sampleScope = "Cached healthy query at initialization and at most1Hz on existing self-memory sampler. GC index/heap/commit/fragment/LOH describe the last completedGC only when Index>0. Query time is not completedGC time or instantaneous free space. No query at allocation or after OOM.",
                    originalVmWitnessUnchanged = true,
                    requestedBytes = "Original minimum element count times actual element size; not a rounded pool bucket."
                });
                using (FileStream init = new(Path.Combine(directory, "initialization.json"), FileMode.CreateNew,
                    FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
                { init.Write(metadata); init.Flush(true); }
                using (FileStream sink = new(Path.Combine(directory, "first-oom.bin"), new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read,
                    BufferSize = 1, Options = FileOptions.WriteThrough, PreallocationSize = RecordBytes }))
                {
                    sink.SetLength(RecordBytes);
                    int owned = Duplicate(sink.SafeFileHandle.DangerousGetHandle().ToInt32());
                    if (owned < 0) throw new IOException("VIC witness descriptor reservation refused.");
                    int warmClose = Duplicate(owned);
                    if (warmClose < 0) { CloseNative(owned); throw new IOException("VIC witness close preparation refused."); }
                    CloseNative(warmClose);
                    CloseDescriptor(); // JIT the empty ownership path before arming.
                    _descriptor = owned; // Independent dup ownership; FileStream may now dispose its handle.
                }
                // Warm the exact scalar, bufferless write, marker and flush path while healthy.
                Request warm = new(0, 0, 0, 0, 8, Caller.Unknown, -1, _sample, _sampleVersion);
                WriteRecord(in warm, Stopwatch.GetTimestamp());
                _record.AsSpan().Clear();
                if (!WritePreparedBuffer(RecordBytes, 0) || !FlushPrepared())
                    throw new IOException("VIC witness prepared write path refused.");
                RecordOom(in warm, null); // JIT the disabled gate without consuming the winner.
                if (WriteUnavailable) throw new IOException("VIC witness warm record refused.");
                Volatile.Write(ref _state, 1);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
            }
            catch
            {
                Volatile.Write(ref _state, 3);
                CloseDescriptor(); _record = null;
                throw;
            }
        }

        private static Snapshot CaptureSnapshot()
        {
            try
            {
#if VIC_WITNESS_CPU_TEST
                Interlocked.Increment(ref SnapshotQueriesForTest);
                if (FailSnapshotForTest) return default;
#endif
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                Snapshot value = new()
                {
                    Available = true, QueryMonotonic = Stopwatch.GetTimestamp(), Index = info.Index,
                    HeapSize = info.HeapSizeBytes, Committed = info.TotalCommittedBytes,
                    Fragmented = info.FragmentedBytes, AvailableBytes = info.TotalAvailableMemoryBytes,
                    EstimatedTotal = GC.GetTotalMemory(false),
                    Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2)
                };
                if (info.GenerationInfo.Length > 3)
                {
                    value.LohSize = info.GenerationInfo[3].SizeAfterBytes;
                    value.LohFragmented = info.GenerationInfo[3].FragmentationAfterBytes;
                }
                return value;
            }
            catch (Exception) { return default; }
        }

        /// <summary>Called only by the existing healthy sampler; never from an allocation or failure path.</summary>
        public static void SampleHealthy()
        {
            if (!Armed || Volatile.Read(ref _winner) != 0) return;
            long now = Stopwatch.GetTimestamp();
            if (now < Volatile.Read(ref _nextSample) || Interlocked.CompareExchange(ref _sampling, 1, 0) != 0) return;
            try
            {
                if (!Armed || Volatile.Read(ref _winner) != 0 || now < Volatile.Read(ref _nextSample)) return;
                Volatile.Write(ref _nextSample, now + Stopwatch.Frequency);
                Snapshot next = CaptureSnapshot();
                if (!next.Available || Volatile.Read(ref _winner) != 0) return;
                Interlocked.Increment(ref _sampleVersion); // Odd while publishing; readers never wait.
                _sample = next;
                Interlocked.Increment(ref _sampleVersion);
            }
            finally { Volatile.Write(ref _sampling, 0); }
        }

        public static Request BeforeAllocation(int width, int height, int elements, int elementBytes,
            Caller site, int frameFormat)
        {
            if (!Armed || Volatile.Read(ref _winner) != 0) return default;
            int before = Volatile.Read(ref _sampleVersion);
            Snapshot sample = _sample;
            Thread.MemoryBarrier(); // Complete the multiword copy before validating its publication version.
            int after = Volatile.Read(ref _sampleVersion);
            if ((before & 1) != 0 || before != after) { sample = default; after = 0; }
            return new Request(Interlocked.Increment(ref _attempt), width, height, elements, elementBytes,
                site, frameFormat, sample, after);
        }

        public static void RecordOom(in Request request, OutOfMemoryException original)
        {
            try
            {
                if (request.Attempt == 0 || Interlocked.CompareExchange(ref _state, 2, 1) != 1) return;
                Volatile.Write(ref _winner, 1);
#if VIC_WITNESS_CPU_TEST
                OriginalExceptionForTest = original;
                if (PauseWriterForTest)
                {
                    Volatile.Write(ref WriterEnteredForTest, 1);
                    while (Volatile.Read(ref ReleaseWriterForTest) == 0) Thread.Yield();
                }
#endif
                try { WriteRecord(in request, Stopwatch.GetTimestamp()); }
                finally
                {
                    // Close can request shutdown but cannot close/reuse the active writer's descriptor.
                    if (Interlocked.CompareExchange(ref _state, 4, 2) == 3) CloseDescriptor();
                }
            }
            catch (Exception) { Volatile.Write(ref _unavailable, 1); }
        }

        private static void Put(int index, long value) =>
            BinaryPrimitives.WriteInt64LittleEndian(_record.AsSpan(index * 8, 8), value);

        private static void WriteRecord(in Request request, long failureMonotonic)
        {
            // Only the first winning thread reaches here after pressure. All storage/path setup is complete.
            _record.AsSpan().Clear();
            Put(0, 1); Put(1, 1); Put(2, request.Attempt); Put(3, request.Thread);
            Put(4, request.Monotonic); Put(5, failureMonotonic); Put(6, request.Width); Put(7, request.Height);
            Put(8, request.Elements); Put(9, request.ElementBytes); Put(10, request.Bytes);
            Put(11, (int)request.Site); Put(12, request.FrameFormat);
            Snapshot sample = request.Sample;
            Put(13, sample.Available ? 1 : 0); Put(14, sample.QueryMonotonic); Put(15, sample.Index);
            Put(16, sample.HeapSize); Put(17, sample.Committed); Put(18, sample.Fragmented);
            Put(19, sample.AvailableBytes); Put(20, sample.LohSize); Put(21, sample.LohFragmented);
            Put(22, sample.EstimatedTotal); Put(23, sample.Gen0); Put(24, sample.Gen1); Put(25, sample.Gen2);
            Put(26, request.SampleVersion);
            if (!WritePreparedBuffer(RecordBytes, 0) || !FlushPrepared())
            { Volatile.Write(ref _unavailable, 1); return; }
            _record[RecordBytes - 1] = CompleteMarker;
            if (!WritePreparedBuffer(1, RecordBytes - 1) || !FlushPrepared())
                Volatile.Write(ref _unavailable, 1);
        }

        private static unsafe bool WritePreparedBuffer(int count, int offset)
        {
            // Raw return codes only: no errno formatting, exception construction or retry after OOM.
            fixed (byte* bytes = _record)
            {
#if VIC_WITNESS_CPU_TEST
                int call = Interlocked.Increment(ref WriteCallsForTest);
                if (ShortFirstWriteForTest && call == 1)
                    return PositionalWrite(_descriptor, bytes + offset, (nuint)(count - 1), offset) == count;
                if (RefuseMarkerForTest && call == 2)
                    return PositionalWrite(-1, bytes + offset, (nuint)count, offset) == count;
#endif
                return PositionalWrite(_descriptor, bytes + offset, (nuint)count, offset) == count;
            }
        }

        private static bool FlushPrepared()
        {
#if VIC_WITNESS_CPU_TEST
            Interlocked.Increment(ref FlushCallsForTest);
            if (RefuseFlushForTest) return FlushDescriptor(-1) == 0;
#endif
            return FlushDescriptor(_descriptor) == 0;
        }

        private static void CloseDescriptor()
        {
            int descriptor = Interlocked.Exchange(ref _descriptor, -1);
            if (descriptor >= 0)
            {
#if VIC_WITNESS_CPU_TEST
                Interlocked.Increment(ref DescriptorCloseCallsForTest);
#endif
                CloseNative(descriptor);
            }
        }

        public static void Close()
        {
            int previous = Interlocked.Exchange(ref _state, 3);
            if (previous != 2 && previous != 3) CloseDescriptor();
        }

#if VIC_WITNESS_CPU_TEST
        public static int SnapshotQueriesForTest;
        public static bool FailSnapshotForTest;
        public static OutOfMemoryException OriginalExceptionForTest;
        public static void RefuseSinkForTest() => CloseDescriptor();
        public static bool ShortFirstWriteForTest, RefuseMarkerForTest, RefuseFlushForTest, PauseWriterForTest;
        public static int WriteCallsForTest, FlushCallsForTest, DescriptorCloseCallsForTest;
        public static int WriterEnteredForTest, ReleaseWriterForTest;
        public static int DescriptorForTest => Volatile.Read(ref _descriptor);
#endif
    }
}
