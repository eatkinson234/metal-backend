#nullable disable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    internal enum NativeTimingKind
    {
        ShaderLibrary,
        RenderPipeline,
        ComputePipeline,
        NextDrawable,
        FenceWait,
        Present,
        DiagnosticCapture,
        DiagnosticProgram,
        DepthSnapshot,
        CommandCommit,
        SyncWait,
        PresentInterval,
        ProgramWait,
    }

    /// <summary>
    /// CPU wall-clock intervals only. No Metal timestamp query or process probe.
    /// The producer holds no renderer lock and never writes files or JSON.
    /// </summary>
    internal static class NativeTimingDiagnostics
    {
        private const string Variable = "SMASH_COUCH_TIMING_DIR";
        private static readonly object Initialization = new();
        private static Recorder _recorder;
        private static int _initialized;
        private static int _initializationErrorReported;

        internal static void Initialize()
        {
            if (Volatile.Read(ref _initialized) != 0) return;
            lock (Initialization)
            {
                if (_initialized != 0) return;
                try
                {
                    string directory = Environment.GetEnvironmentVariable(Variable);
                    if (directory != null)
                        _recorder = new Recorder(ValidateDirectory(directory));
                }
                catch (Exception error)
                {
                    if (Interlocked.Exchange(ref _initializationErrorReported, 1) == 0)
                        ReportError(error);
                }
                finally
                {
                    Volatile.Write(ref _initialized, 1);
                }
            }
        }

        internal static Scope Measure(NativeTimingKind kind)
        {
            Initialize();
            Recorder recorder = Volatile.Read(ref _recorder);
            return recorder != null && recorder.Active
                ? new Scope(recorder, kind, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId)
                : default;
        }

        // An interval measured by the caller (Stopwatch timestamps) on the current thread.
        internal static void Record(NativeTimingKind kind, long start, long end)
        {
            Initialize();
            Recorder recorder = Volatile.Read(ref _recorder);
            if (recorder == null || !recorder.Active) return;
            try { recorder.Record(kind, start, end, Environment.CurrentManagedThreadId); }
            catch (Exception error) { recorder.Disable(error); }
        }

        internal static void PresentInterval()
        {
            Initialize();
            Recorder recorder = Volatile.Read(ref _recorder);
            if (recorder == null || !recorder.Active) return;
            try { recorder.PresentInterval(Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId); }
            catch (Exception error) { recorder.Disable(error); }
        }

        internal static void Shutdown()
        {
            Recorder recorder = Volatile.Read(ref _recorder);
            if (recorder == null) return;
            try { recorder.Shutdown(); }
            catch (Exception error) { recorder.Disable(error); }
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly Recorder _recorder;
            private readonly NativeTimingKind _kind;
            private readonly long _start;
            private readonly int _thread;

            internal Scope(Recorder recorder, NativeTimingKind kind, long start, int thread)
            {
                _recorder = recorder;
                _kind = kind;
                _start = start;
                _thread = thread;
            }

            public void Dispose()
            {
                if (_recorder == null) return;
                try { _recorder.Record(_kind, _start, Stopwatch.GetTimestamp(), _thread); }
                catch (Exception error) { _recorder.Disable(error); }
            }
        }

        private static string ValidateDirectory(string directory)
        {
            string root = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(directory) ||
                !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(directory))
                throw new ArgumentException("Timing needs absolute private root and output directory");
            string normalizedRoot = Path.GetFullPath(root);
            string normalized = Path.GetFullPath(directory);
            if (normalizedRoot != root || normalized != directory ||
                !normalized.StartsWith(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar) +
                    Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Timing directory must be a normalized private-data descendant");
            for (string cursor = normalized; cursor != null; cursor = Path.GetDirectoryName(cursor))
            {
                FileAttributes attributes = File.GetAttributes(cursor);
                if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                    (attributes & FileAttributes.Directory) == 0)
                    throw new ArgumentException("Timing path contains a symlink or non-directory ancestor");
            }
            if (Directory.EnumerateFileSystemEntries(normalized).Any())
                throw new IOException("Timing output directory must be preexisting and fresh (empty)");
            return normalized;
        }

        private static void ReportError(Exception error)
        {
            try { Console.Error.WriteLine("[NativeTiming] recording disabled: " +
                error.GetType().Name + ": " + error.Message); }
            catch { /* Diagnostics must not replace renderer failures. */ }
        }

        internal readonly record struct DiagnosticSnapshot(
            long[] Counts, long[] TotalTicks, long Accepted, long Exported,
            long DroppedQueue, long DroppedLimit, long DroppedError, long DroppedShutdown,
            long WriterErrors, int QueueDepth, long[] DroppedLimitByKind);

        internal static DiagnosticSnapshot SnapshotForTests() =>
            Volatile.Read(ref _recorder)?.Snapshot() ?? default;

        private readonly struct Event
        {
            internal readonly NativeTimingKind Kind;
            internal readonly long Start;
            internal readonly long End;
            internal readonly int Thread;
            internal Event(NativeTimingKind kind, long start, long end, int thread)
            {
                Kind = kind;
                Start = start;
                End = end;
                Thread = thread;
            }
        }

        internal sealed class Recorder
        {
            private const int QueueLimit = 8192;
            private const long ExportLimit = 100000;
            // Every present is exported regardless of duration, so in a long session they used to fill the whole
            // limit within minutes and later slow program/pipeline waits had no detail left (audit F15). Presents
            // may take at most half; the rest stays for the >= 4 ms events. Limit drops are counted per kind.
            private const long PresentExportLimit = ExportLimit / 2;
            private static readonly long ThresholdTicks = Math.Max(1, Stopwatch.Frequency / 250);
            private readonly Event[] _queue = new Event[QueueLimit];
            private readonly object _gate = new();
            private readonly long[] _counts = new long[Enum.GetValues<NativeTimingKind>().Length];
            private readonly long[] _totalTicks = new long[Enum.GetValues<NativeTimingKind>().Length];
            private readonly long[] _dropLimitByKind = new long[Enum.GetValues<NativeTimingKind>().Length];
            private readonly FileStream _file;
            private readonly Thread _writer;
            private readonly DateTime _startedUtc = DateTime.UtcNow;
            private readonly long _startedTicks = Stopwatch.GetTimestamp();
            private int _head, _tail, _queueCount, _active = 1, _shutdown;
            private long _lastPresent, _accepted, _acceptedPresents, _exported, _dropQueue, _dropLimit;
            private long _dropError, _dropShutdown, _writerErrors;
            private int _errorReported;

            internal bool Active => Volatile.Read(ref _active) != 0;

            internal Recorder(string directory)
            {
                _file = new FileStream(Path.Combine(directory, "native-timing.jsonl"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _writer = new Thread(WriteLoop) { IsBackground = true, Name = "native-timing-writer" };
                try { _writer.Start(); }
                catch
                {
                    _file.Dispose();
                    throw;
                }
            }

            internal void Record(NativeTimingKind kind, long start, long end, int thread)
            {
                long duration = Math.Max(0, end - start);
                int index = (int)kind;
                Interlocked.Increment(ref _counts[index]);
                Interlocked.Add(ref _totalTicks[index], duration);
                if (kind != NativeTimingKind.Present && kind != NativeTimingKind.PresentInterval &&
                    duration < ThresholdTicks) return;
                Enqueue(new Event(kind, start, end, thread));
            }

            internal void PresentInterval(long now, int thread)
            {
                long previous = Interlocked.Exchange(ref _lastPresent, now);
                Record(NativeTimingKind.PresentInterval, previous == 0 ? now : previous, now, thread);
            }

            private void Enqueue(Event item)
            {
                if (!Active)
                {
                    DropStopped();
                    return;
                }
                bool present = item.Kind is NativeTimingKind.Present or NativeTimingKind.PresentInterval;
                lock (_gate)
                {
                    if (_active == 0)
                    {
                        DropStopped();
                    }
                    else if (_accepted >= ExportLimit || (present && _acceptedPresents >= PresentExportLimit))
                    {
                        Interlocked.Increment(ref _dropLimit);
                        _dropLimitByKind[(int)item.Kind]++;
                    }
                    else if (_queueCount == QueueLimit)
                    {
                        Interlocked.Increment(ref _dropQueue);
                    }
                    else
                    {
                        _queue[_tail] = item;
                        _tail = (_tail + 1) % QueueLimit;
                        _queueCount++;
                        _accepted++;
                        if (present) _acceptedPresents++;
                    }
                }
            }

            private void DropStopped()
            {
                if (Interlocked.Read(ref _writerErrors) != 0)
                    Interlocked.Increment(ref _dropError);
                else
                    Interlocked.Increment(ref _dropShutdown);
            }

            private bool TryDequeue(out Event item)
            {
                lock (_gate)
                {
                    if (_queueCount == 0) { item = default; return false; }
                    item = _queue[_head];
                    _head = (_head + 1) % QueueLimit;
                    _queueCount--;
                    return true;
                }
            }

            internal DiagnosticSnapshot Snapshot()
            {
                long[] counts = new long[_counts.Length], totals = new long[_totalTicks.Length];
                for (int i = 0; i < counts.Length; i++)
                {
                    counts[i] = Interlocked.Read(ref _counts[i]);
                    totals[i] = Interlocked.Read(ref _totalTicks[i]);
                }
                lock (_gate)
                    return new DiagnosticSnapshot(counts, totals, _accepted,
                        Interlocked.Read(ref _exported), Interlocked.Read(ref _dropQueue),
                        Interlocked.Read(ref _dropLimit), Interlocked.Read(ref _dropError),
                        Interlocked.Read(ref _dropShutdown), Interlocked.Read(ref _writerErrors),
                        _queueCount, (long[])_dropLimitByKind.Clone());
            }

            private void WriteLoop()
            {
                bool inFlight = false;
                try
                {
                    using StreamWriter output = new(_file, new UTF8Encoding(false));
                    output.WriteLine("{\"record\":\"header\",\"schema\":\"native-timing-v1\",\"utc\":\"" +
                        _startedUtc.ToString("O", CultureInfo.InvariantCulture) +
                        "\",\"stopwatch_frequency\":" + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture) +
                        ",\"start_ticks\":" + _startedTicks.ToString(CultureInfo.InvariantCulture) +
                        ",\"queue_limit\":8192,\"event_limit\":100000,\"present_event_limit\":50000," +
                        "\"meaning\":\"CPU elapsed scopes and Present entry cadence, not GPU completion/display presentation; deadline termination may omit the final second\"," +
                        "\"coherency\":\"periodic aggregates are nontransactional across producer threads; final follows queue drain\"}");
                    output.Flush();
                    long lastSummary = Stopwatch.GetTimestamp();
                    while (true)
                    {
                        int drained = 0;
                        while (drained < 512 && TryDequeue(out Event item))
                        {
                            inFlight = true;
                            long duration = Math.Max(0, item.End - item.Start);
                            output.WriteLine("{\"record\":\"event\",\"kind\":\"" + item.Kind +
                                "\",\"start_ticks\":" + (item.Start - _startedTicks).ToString(CultureInfo.InvariantCulture) +
                                ",\"end_ticks\":" + (item.End - _startedTicks).ToString(CultureInfo.InvariantCulture) +
                                ",\"duration_ticks\":" + duration.ToString(CultureInfo.InvariantCulture) +
                                ",\"managed_thread\":" + item.Thread.ToString(CultureInfo.InvariantCulture) + "}");
                            Interlocked.Increment(ref _exported);
                            inFlight = false;
                            drained++;
                        }
                        long now = Stopwatch.GetTimestamp();
                        bool summarized = false;
                        if (now - lastSummary >= Stopwatch.Frequency || Volatile.Read(ref _shutdown) != 0)
                        {
                            bool final = Volatile.Read(ref _shutdown) != 0 && Snapshot().QueueDepth == 0;
                            WriteSummary(output, final);
                            lastSummary = now;
                            summarized = true;
                        }
                        if (drained != 0 || summarized) output.Flush();
                        if (Volatile.Read(ref _shutdown) != 0 && Snapshot().QueueDepth == 0) break;
                        if (drained == 0) Thread.Sleep(100);
                    }
                }
                catch (Exception error)
                {
                    if (inFlight) Interlocked.Increment(ref _dropError);
                    Disable(error);
                }
            }

            private void WriteSummary(StreamWriter output, bool final)
            {
                DiagnosticSnapshot s = Snapshot();
                StringBuilder line = new(640);
                line.Append("{\"record\":\"summary\",\"final\":").Append(final ? "true" : "false")
                    .Append(",\"elapsed_ticks\":")
                    .Append((Stopwatch.GetTimestamp() - _startedTicks).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"accepted\":").Append(s.Accepted)
                    .Append(",\"exported\":").Append(s.Exported)
                    .Append(",\"dropped_queue\":").Append(s.DroppedQueue)
                    .Append(",\"dropped_limit\":").Append(s.DroppedLimit)
                    .Append(",\"dropped_error\":").Append(s.DroppedError)
                    .Append(",\"dropped_shutdown\":").Append(s.DroppedShutdown)
                    .Append(",\"writer_errors\":").Append(s.WriterErrors)
                    .Append(",\"queue_depth\":").Append(s.QueueDepth)
                    // Process-wide GC pause total and collection counts, so a slow second can be checked for GC.
                    .Append(",\"gc_pause_ms\":").Append(GC.GetTotalPauseDuration().TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                    .Append(",\"gcs\":[").Append(GC.CollectionCount(0)).Append(',').Append(GC.CollectionCount(1))
                    .Append(',').Append(GC.CollectionCount(2)).Append(']')
                    .Append(",\"aggregates\":{");
                for (int i = 0; i < s.Counts.Length; i++)
                {
                    if (i != 0) line.Append(',');
                    line.Append('"').Append((NativeTimingKind)i).Append("\":{\"count\":")
                        .Append(s.Counts[i]).Append(",\"total_ticks\":").Append(s.TotalTicks[i])
                        .Append(",\"dropped_limit\":").Append(s.DroppedLimitByKind[i]).Append('}');
                }
                line.Append("}}");
                output.WriteLine(line);
            }

            internal void Shutdown()
            {
                Interlocked.Exchange(ref _shutdown, 1);
                Volatile.Write(ref _active, 0);
                if (Thread.CurrentThread != _writer) _writer.Join(1000);
            }

            internal void Disable(Exception error)
            {
                Volatile.Write(ref _active, 0);
                Volatile.Write(ref _shutdown, 1);
                Interlocked.Increment(ref _writerErrors);
                lock (_gate)
                {
                    Interlocked.Add(ref _dropError, _queueCount);
                    _queueCount = 0;
                    _head = _tail;
                }
                if (Interlocked.Exchange(ref _errorReported, 1) == 0) ReportError(error);
            }
        }
    }
}
