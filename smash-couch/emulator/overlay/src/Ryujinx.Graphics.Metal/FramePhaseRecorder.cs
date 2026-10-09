using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Ryujinx.Graphics.Metal;

// Records phases without changing drawable ownership or native present policy.
// The separate exact .43 experiment can consume calibrated presentation timestamps.
// No reference to a drawable, texture, renderer or command buffer is kept.
internal sealed unsafe class FramePhaseRecorder : IDisposable
{
    private const int QueueLimit = 8192;
    private const string OpportunityCaptureScope =
        "Process-local due composer opportunities admitted before first recorder Dispose Close; calls observing Close excluded";
    private readonly FramePhaseNative _api;
    private readonly nint _observer;
    private readonly PresentBufferNative _ownershipApi;
    private readonly nint _ownershipObserver;
    private readonly ConcurrentQueue<OwnershipRecord> _ownershipRecords = new();
    private int _ownershipQueueDepth, _commitLeases;
    private long _ownershipAttempts, _ownershipRefusals, _ownershipQueueDrops, _exportedRegistrations, _exportedCommits;
    private long _exportedScheduled, _exportedCompleted;
    private long _ownershipCloseBefore, _ownershipCloseAfter;
    private readonly record struct OwnershipRecord(int Kind, PresentBufferRegistration Registration, long Before, long After, bool Returned);
    private readonly object _registrationGate = new();
    private readonly ConcurrentQueue<Submission> _submissions = new();
    private readonly Thread _writer;
    private readonly StreamWriter _output;
    private int _closing, _stop, _queueDepth;
    private long _attempts, _registrationErrors, _submissionDrops, _exportedSubmissions, _exportedCallbacks;
    private long _exportedOpportunities;
    private long _opportunityCloseBeforeTicks, _opportunityCloseAfterTicks;
    private string _writerError;
    private double _clockOffsetLow = double.NegativeInfinity;
    private double _clockOffsetHigh = double.PositiveInfinity;
    private bool _clockValid;
    private long _lastClockTicks;


    internal readonly record struct Submission(FramePresentationTiming Timing, long BackendBegin,
        long DrawableAcquired, long BeforePresent, long AfterCommit, int RegisterStatus);

    internal static FramePhaseRecorder CreateIfEnabled()
    {
        if (!FramePresentationTiming.IsEnabled) return null;
        try
        {
            string directory = ValidateDirectory(Environment.GetEnvironmentVariable("SMASH_COUCH_FRAME_PHASE_DIR"));
            return new FramePhaseRecorder(directory,
                Path.Combine(AppContext.BaseDirectory, "libSmashFramePhase.dylib"));
        }
        catch (Exception ex)
        {
            Report("initialization refused: " + ex);
            return null;
        }
    }

    internal FramePhaseRecorder(string directory, string library)
    {
        _api = new FramePhaseNative(library);
        _observer = _api.Create();
        if (_observer == 0) throw new InvalidOperationException("Cannot allocate frame phase observer");
        _ownershipApi = _api.Ownership;
        _ownershipObserver = _ownershipApi == null ? 0 : _ownershipApi.Create();
        if (_ownershipApi != null && _ownershipObserver == 0)
        {
            _api.Close(_observer);
            nint failed = _observer; _api.TryDestroy(&failed);
            throw new InvalidOperationException("Cannot allocate present-buffer v1 observer");
        }
        try
        {
            _output = new StreamWriter(new FileStream(Path.Combine(directory, "frame-phase.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536), bufferSize: 65536);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "MetalFramePhaseWriter" };
            _writer.Start();
        }
        catch
        {
            _output?.Dispose();
            _api.Close(_observer);
            nint owned = _observer;
            _api.TryDestroy(&owned); // no registration has occurred yet
            if (_ownershipApi != null) { nint owner = _ownershipObserver; _ownershipApi.TryDestroy(&owner); }
            throw;
        }
    }

    private int RegisterDrawable(nint drawable, long sequence)
    {
        Interlocked.Increment(ref _attempts);
        int status = _api.Register(_observer, drawable, (ulong)sequence);
        if (status != 0) Interlocked.Increment(ref _registrationErrors);
        return status;
    }

    internal int Register(nint drawable, long sequence)
    {
        lock (_registrationGate)
        {
            if (_closing != 0) return 1;
            return RegisterDrawable(drawable, sequence);
        }
    }

    internal PresentBufferRegistration RegisterPresentBuffer(CommandBufferScoped cbs, nint drawable, long sequence)
    {
        lock (_registrationGate)
        {
            if (_closing != 0 || _ownershipApi == null) return new(null, cbs.DiagnosticInstance, (ulong)sequence, 0,
                (ulong)cbs.CommandBuffer.NativePtr, cbs.CommandBufferIndex, 1, 1);
            Interlocked.Increment(ref _ownershipAttempts);
            Interlocked.Increment(ref _commitLeases); // admission survives Close until actual Commit report retires
            ulong drawableId = 0;
            long before = Stopwatch.GetTimestamp();
            int status = _ownershipApi.Register(_ownershipObserver, cbs.CommandBuffer.NativePtr, drawable,
                cbs.DiagnosticInstance, (ulong)sequence, &drawableId);
            int presentedStatus = RegisterDrawable(drawable, sequence); // same admission/Close gate
            long after = Stopwatch.GetTimestamp();
            if (status != 0) Interlocked.Increment(ref _ownershipRefusals);
            var registration = new PresentBufferRegistration(this, cbs.DiagnosticInstance, (ulong)sequence, drawableId,
                (ulong)cbs.CommandBuffer.NativePtr, cbs.CommandBufferIndex, status, presentedStatus);
            QueueOwnership(new(1, registration, before, after, false));
            return registration;
        }
    }

    internal PresentBufferRegistration RefusePresentBuffer(CommandBufferScoped cbs, nint drawable, long sequence, int status)
    {
        lock (_registrationGate)
        {
            Interlocked.Increment(ref _ownershipAttempts);
            Interlocked.Increment(ref _ownershipRefusals);
            int presentedStatus = _closing == 0 ? RegisterDrawable(drawable, sequence) : 1;
            var registration = new PresentBufferRegistration(null, cbs.DiagnosticInstance, (ulong)sequence, 0,
                (ulong)cbs.CommandBuffer.NativePtr, cbs.CommandBufferIndex, status, presentedStatus);
            long now = Stopwatch.GetTimestamp();
            if (_closing == 0) QueueOwnership(new(1, registration, now, now, false));
            else Interlocked.Increment(ref _ownershipQueueDrops);
            return registration;
        }
    }

    internal void CommitPresentBuffer(PresentBufferRegistration registration, long before, long after, bool returned)
    {
        lock (_registrationGate)
        {
            // Already admitted reports remain valid after Close. No native dereference here.
            QueueOwnership(new(2, registration, before, after, returned));
            Interlocked.Decrement(ref _commitLeases);
        }
    }

    private void QueueOwnership(OwnershipRecord record)
    {
        if (Interlocked.Increment(ref _ownershipQueueDepth) > QueueLimit)
        {
            Interlocked.Decrement(ref _ownershipQueueDepth);
            Interlocked.Increment(ref _ownershipQueueDrops);
            return;
        }
        _ownershipRecords.Enqueue(record);
    }

    internal void Submit(Submission item)
    {
        lock (_registrationGate)
        {
            if (_closing != 0)
            {
                Interlocked.Increment(ref _submissionDrops);
                return;
            }
            if (Interlocked.Increment(ref _queueDepth) > QueueLimit)
            {
                Interlocked.Decrement(ref _queueDepth);
                Interlocked.Increment(ref _submissionDrops);
                return;
            }
            _submissions.Enqueue(item);
        }
    }

    public void Dispose()
    {
        lock (_registrationGate)
        {
            if (_closing != 0) return;
            Volatile.Write(ref _closing, 1);
            if (PresentationPhaseAlignment.OpportunityTraceEnabled)
            {
                // This is the first actual Close. Keep this same-clock bracket;
                // the redundant writer-finally Close must never replace it.
                _opportunityCloseBeforeTicks = Stopwatch.GetTimestamp();
                PresentationPhaseAlignment.OpportunityTrace.Close();
                _opportunityCloseAfterTicks = Stopwatch.GetTimestamp();
            }
            PresentationPhaseAlignment.Invalidate();
            _api.Close(_observer);
            if (_ownershipApi != null)
            {
                _ownershipCloseBefore = Stopwatch.GetTimestamp();
                _ownershipApi.Close(_ownershipObserver);
                _ownershipCloseAfter = Stopwatch.GetTimestamp();
            }
            Volatile.Write(ref _stop, 1);
        }
        // No native access from this thread after requesting stop. Only the
        // writer drains/destroys. On timeout both library and context stay live.
        if (!_writer.Join(2500)) Report("writer shutdown incomplete; retaining callback context until process exit");
    }

    private void WriteLoop()
    {
        FramePhaseEvent* events = stackalloc FramePhaseEvent[128];
        PresentBufferEvent* owners = stackalloc PresentBufferEvent[128];
        long stopAt = 0, nextClock = 0;
        bool completed = false;
        try
        {
            Row(new FramePhaseHeader("header", "smash-frame-phase-v1", Stopwatch.Frequency,
                Environment.ProcessId, DateTime.UtcNow, 80, 56, "UNPRESENTED_OR_SKIPPED", false));
            if (_ownershipApi != null)
                Row(new PresentBufferHeader("present_buffer_header", "smash-present-buffer-v1", 1, 136, 160, 4096, QueueLimit,
                    "SMASH_COUCH_PRESENT_BUFFER_DIAGNOSTIC=1", "process-scoped monotonic native-buffer instance per rent; pointer is supplemental only",
                    "one presentation per rented instance; duplicate refuses; ordinary PresentDrawable unchanged",
                    "GPU host seconds relative to system mach time; unmapped; callback arrival is not exact schedule/completion/latch", false));
            if (PresentationPhaseAlignment.OpportunityTraceEnabled)
            {
                Row(new FramePhaseOpportunityHeader("phase_opportunity_header", "smash-phase-opportunity-v1",
                    true, PresentationPhaseAlignment.Enabled, PresentationPhaseAlignment.OpportunityTrace.Capacity,
                    "frame-phase presented Sequence tagged at original Publish",
                    "Stopwatch ticks observed by MetalFramePhaseWriter; not native callback arrival",
                    "post-Compose-conditional and post-tick-remainder; ComposeCalled and swap delay are observations",
                    OpportunityCaptureScope));
            }
            while (true)
            {
                DrainOpportunities();
                DrainOwnership(owners);
                while (_submissions.TryDequeue(out Submission item))
                {
                    Interlocked.Decrement(ref _queueDepth);
                    Row(new FramePhaseSubmissionRow("submission", item));
                    _exportedSubmissions++;
                }
                nuint count;
                do
                {
                    count = _api.Drain(_observer, events, 128);
                    for (nuint i = 0; i < count; i++)
                    {
                        WriteEvent(events[i]);
                    }
                } while (count == 128);
                long now = Stopwatch.GetTimestamp();
                if (now >= nextClock)
                {
                    FramePhaseClock clock = default;
                    long before = Stopwatch.GetTimestamp();
                    int status = _api.ReadClock(&clock);
                    long after = Stopwatch.GetTimestamp();
                    Row(new FramePhaseClockRow("clock", before, after, status, clock));
                    CalibratePhaseClock(before, after, status, clock);
                    _output.Flush();
                    nextClock = now + Stopwatch.Frequency;
                }
                if (Volatile.Read(ref _stop) != 0)
                {
                    if (stopAt == 0) stopAt = now + Stopwatch.Frequency * 2;
                    if (_api.Pending(_observer) == 0 && _submissions.IsEmpty && OpportunitiesCompleted() && OwnershipPendingZero() &&
                        Volatile.Read(ref _commitLeases) == 0 && _ownershipRecords.IsEmpty)
                    {
                        // Both native rings need a final drain after the acquire of pending=0.
                        // A last PCB callback may publish between the earlier drain and this observation.
                        DrainOwnership(owners);
                        // One more drain after observing pending=0 establishes
                        // the release/acquire boundary for the final callback.
                        if (_api.Drain(_observer, events, 1) == 0) { completed = true; break; }
                        // Preserve this record by writing it on the same consumer.
                        WriteEvent(events[0]);
                    }
                    if (now >= stopAt) break;
                }
                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            _writerError = ex.ToString();
            Report("writer failed: " + ex);
        }
        finally
        {
            // A writer IO failure does not permit destruction while the render
            // thread is still registering callbacks. Preserve state until close.
            while (Volatile.Read(ref _stop) == 0 && _writerError != null) Thread.Sleep(20);
            PresentationPhaseAlignment.Invalidate();
            PresentationPhaseAlignment.OpportunityTrace?.Close();
            ulong pending = _api.Pending(_observer);
            var stats = new FramePhaseFooter("footer", completed, _writerError,
                Interlocked.Read(ref _attempts), Interlocked.Read(ref _registrationErrors),
                _api.Registered(_observer), _api.Callbacks(_observer), pending,
                _api.Mismatches(_observer), _api.ZeroTimes(_observer), _api.Dropped(_observer),
                _api.Rejected(_observer), Interlocked.Read(ref _submissionDrops), Volatile.Read(ref _queueDepth),
                _exportedSubmissions, _exportedCallbacks);
            PresentBufferStatistics ownerStats = default;
            if (_ownershipApi != null) _ownershipApi.ReadStats(_ownershipObserver, &ownerStats);
            nint ownerContext = _ownershipObserver;
            int ownerDestroy = _ownershipApi == null ? -1 : Volatile.Read(ref _commitLeases) != 0 ? 3 :
                _ownershipApi.TryDestroy(&ownerContext); // never destroy an admitted outstanding Commit lease
            nint owned = _observer;
            // Busy or undrained is intentionally retained, never forced free.
            int destroyStatus = _api.TryDestroy(&owned);
            try
            {
                var alignment = PresentationPhaseAlignment.Statistics;
                Row(new FramePhaseAlignmentRow("phase_alignment", PresentationPhaseAlignment.Enabled,
                    alignment.FeedbackCount, alignment.Adjustments, alignment.TotalTicks, alignment.MaximumAbsTicks));
                if (PresentationPhaseAlignment.OpportunityTraceEnabled)
                {
                    var opportunities = PresentationPhaseAlignment.OpportunityTrace.Statistics;
                    long closeBefore = Interlocked.Read(ref _opportunityCloseBeforeTicks);
                    long closeAfter = Interlocked.Read(ref _opportunityCloseAfterTicks);
                    bool captureBracketValid = closeBefore > 0 && closeAfter >= closeBefore;
                    Row(new FramePhaseOpportunityFooter("phase_opportunity_footer", opportunities,
                        completed && opportunities.Completed && _writerError == null && captureBracketValid,
                        completed && opportunities.Completed && _writerError == null &&
                            opportunities.Dropped == 0 && opportunities.Drained == _exportedOpportunities && captureBracketValid,
                        _exportedOpportunities, _writerError, OpportunityCaptureScope,
                        closeBefore == 0 ? null : closeBefore, closeAfter == 0 ? null : closeAfter, captureBracketValid));
                }
                if (_ownershipApi != null)
                {
                    bool ownerComplete = completed && ownerDestroy == 0 && _writerError == null && Volatile.Read(ref _commitLeases) == 0;
                    Row(new PresentBufferFooter("present_buffer_footer", ownerComplete,
                        ownerComplete && _ownershipRefusals == 0 && _ownershipQueueDrops == 0 &&
                        ownerStats.ScheduledDrops == 0 && ownerStats.CompletedDrops == 0 && ownerStats.IdentityFailures == 0 &&
                        ownerStats.RegistrationErrors == 0 && ownerStats.DuplicateCallbacks == 0 && ownerStats.CallbackExceptions == 0 &&
                        ownerStats.ScheduledDrained == (ulong)_exportedScheduled && ownerStats.CompletedDrained == (ulong)_exportedCompleted &&
                        _exportedRegistrations == _ownershipAttempts && ownerStats.Registered == (ulong)_exportedCommits &&
                        ownerStats.ScheduledRegistered == ownerStats.Registered && ownerStats.CompletedRegistered == ownerStats.Registered &&
                        ownerStats.ScheduledCallbacks == ownerStats.Registered && ownerStats.CompletedCallbacks == ownerStats.Registered,
                        _writerError, _ownershipAttempts, _ownershipRefusals, _ownershipQueueDrops, _ownershipQueueDepth, _commitLeases,
                        _exportedRegistrations, _exportedCommits, _exportedScheduled, _exportedCompleted, ownerStats,
                        _ownershipCloseBefore, _ownershipCloseAfter));
                    Row(new PresentBufferLifetime("present_buffer_lifetime", ownerDestroy, ownerContext != 0));
                }
                Row(stats); Row(new FramePhaseLifetimeRow("lifetime", destroyStatus, owned != 0)); _output.Dispose();
            }
            catch (Exception ex) { Report("footer write failed: " + ex); }
        }
    }

    private bool OwnershipPendingZero()
    {
        if (_ownershipApi == null) return true;
        PresentBufferStatistics stats = default;
        return _ownershipApi.ReadStats(_ownershipObserver, &stats) == 0 && stats.ScheduledPending == 0 &&
            stats.CompletedPending == 0 && stats.ActiveRegistrations == 0;
    }

    private void DrainOwnership(PresentBufferEvent* events)
    {
        if (_ownershipApi == null) return;
        while (_ownershipRecords.TryDequeue(out OwnershipRecord record))
        {
            Interlocked.Decrement(ref _ownershipQueueDepth);
            var r = record.Registration;
            if (record.Kind == 1)
            {
                Row(new PresentBufferRegistrationRow("present_buffer_registration", r.Instance, r.Sequence, r.DrawableId,
                    r.BufferPointer, r.PoolIndex, r.RegisterStatus, r.PresentedRegisterStatus, record.Before, record.After));
                _exportedRegistrations++;
            }
            else
            {
                Row(new PresentBufferCommitRow("present_buffer_commit", r.Instance, r.Sequence, r.DrawableId,
                    r.BufferPointer, r.PoolIndex, record.Before, record.After, record.Returned));
                _exportedCommits++;
            }
        }
        nuint count;
        do
        {
            count = _ownershipApi.Drain(_ownershipObserver, events, 128);
            for (nuint i = 0; i < count; i++)
            {
                PresentBufferEvent e = events[i];
                Row(new PresentBufferCallbackRow("present_buffer_callback", e.Instance, e.Sequence, e.DrawableId,
                    e.ExpectedBufferPointer, e.CallbackBufferPointer, e.Kind, e.ObservedStatus, e.GpuValidity, e.ErrorPresent, e.ErrorCode,
                    double.IsFinite(e.GpuStartTime) ? e.GpuStartTime : null, BitConverter.DoubleToInt64Bits(e.GpuStartTime),
                    double.IsFinite(e.GpuEndTime) ? e.GpuEndTime : null, BitConverter.DoubleToInt64Bits(e.GpuEndTime),
                    e.AbsoluteBefore, e.ContinuousBefore, double.IsFinite(e.CaBefore) ? e.CaBefore : null,
                    e.ContinuousAfter, e.AbsoluteAfter, double.IsFinite(e.CaAfter) ? e.CaAfter : null, e.Numer, e.Denom));
                if (e.Kind == 1) _exportedScheduled++; else if (e.Kind == 2) _exportedCompleted++;
            }
        } while (count == 128);
    }

    private void CalibratePhaseClock(long before, long after, int status, FramePhaseClock clock)
    {
        if (!PresentationPhaseAlignment.Enabled) return;
        if (status != 0 || before <= 0 || after < before || after - before > Stopwatch.Frequency / 1000 ||
            !double.IsFinite(clock.CaBefore) || !double.IsFinite(clock.CaAfter) ||
            clock.CaBefore <= 0 || clock.CaAfter < clock.CaBefore)
        {
            _clockValid = false;
            PresentationPhaseAlignment.Invalidate();
            return;
        }
        // Same bracket/intersection used by the reviewed offline phase analysis.
        _clockOffsetLow = Math.Max(_clockOffsetLow, clock.CaBefore - (double)after / Stopwatch.Frequency - 1e-6);
        _clockOffsetHigh = Math.Min(_clockOffsetHigh, clock.CaAfter - (double)before / Stopwatch.Frequency + 1e-6);
        _clockValid = _clockOffsetLow <= _clockOffsetHigh && _clockOffsetHigh - _clockOffsetLow <= 0.000020;
        if (!_clockValid) PresentationPhaseAlignment.Invalidate();
        _lastClockTicks = after;
    }

    private void WriteEvent(FramePhaseEvent e)
    {
        long now = Stopwatch.GetTimestamp();
        if (PresentationPhaseAlignment.Enabled && Volatile.Read(ref _stop) == 0 &&
            _clockValid && now >= _lastClockTicks &&
            now - _lastClockTicks <= Stopwatch.Frequency * 2 &&
            e.ExpectedDrawableId == e.CallbackDrawableId && double.IsFinite(e.PresentedTime) && e.PresentedTime > 0)
        {
            double mapped = (e.PresentedTime - (_clockOffsetLow + _clockOffsetHigh) / 2) * Stopwatch.Frequency;
            if (double.IsFinite(mapped) && mapped > 0 && mapped <= now)
                PresentationPhaseAlignment.Publish((long)mapped, now, e.Sequence);
        }
        Row(new FramePhasePresentedRow("presented", e.Sequence, e.ExpectedDrawableId, e.CallbackDrawableId,
            double.IsFinite(e.PresentedTime) ? (double?)e.PresentedTime : null,
            BitConverter.DoubleToInt64Bits(e.PresentedTime),
            !double.IsFinite(e.PresentedTime) || e.PresentedTime < 0 ? "INVALID" :
                e.PresentedTime == 0 ? "UNPRESENTED_OR_SKIPPED" : "PRESENTED",
            e.AbsoluteBefore, e.ContinuousBefore, e.CaBefore, e.ContinuousAfter, e.AbsoluteAfter, e.CaAfter));
        _exportedCallbacks++;
    }

    private void DrainOpportunities()
    {
        if (!PresentationPhaseAlignment.OpportunityTraceEnabled) return;
        // Bound each drain pass as well as storage; the native event drain and
        // clock checks retain a chance to run while the composer is producing.
        for (int i = 0; i < PresentationPhaseAlignment.OpportunityTrace.Capacity &&
            PresentationPhaseAlignment.OpportunityTrace.TryDequeue(out PresentationPhaseOpportunity opportunity); i++)
        {
            Row(new FramePhaseOpportunityRow("phase_opportunity", opportunity.OpportunitySequence,
                opportunity.TickSample, opportunity.NominalDeadline, opportunity.Now, opportunity.TicksPerFrame,
                opportunity.TickRemainder, opportunity.SwapInterval, opportunity.SwapIntervalDelay,
                opportunity.ComposeCalled, opportunity.PhaseEnabled, opportunity.VSyncModeSwitch,
                opportunity.TargetVSyncInterval, opportunity.Decision.CorrectionCalled,
                opportunity.Decision.Reason.ToString(), opportunity.Decision.FeedbackSequence,
                opportunity.Decision.PresentedTicks, opportunity.Decision.WriterObservedTicks,
                opportunity.Decision.CorrectionTicks, opportunity.Decision.WrappedPhaseTicks,
                opportunity.Decision.SignedPhaseErrorTicks));
            _exportedOpportunities++;
        }
    }

    private static bool OpportunitiesCompleted() => !PresentationPhaseAlignment.OpportunityTraceEnabled ||
        PresentationPhaseAlignment.OpportunityTrace.Statistics.Completed;

    private void Row<T>(T item) => _output.WriteLine(JsonSerializer.Serialize(item, (JsonTypeInfo<T>)FramePhaseJsonContext.Default.GetTypeInfo(typeof(T))));
    private static void Report(string message)
    {
        try { Console.Error.WriteLine("[FramePhase] " + message); } catch { }
    }

    internal static string ValidateDirectory(string directory)
    {
        string root = Environment.GetEnvironmentVariable("SMASH_COUCH_PRIVATE_DATA");
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(directory) ||
            !Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(directory) ||
            Path.GetFullPath(root) != root || Path.GetFullPath(directory) != directory ||
            !directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Frame phase output must be a normalized private-data descendant");
        for (string cursor = directory; cursor != null; cursor = Path.GetDirectoryName(cursor))
        {
            FileAttributes a = File.GetAttributes(cursor);
            if ((a & FileAttributes.ReparsePoint) != 0 || (a & FileAttributes.Directory) == 0)
                throw new ArgumentException("Frame phase path contains a symlink or non-directory ancestor");
        }
        if (Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Frame phase directory must be preexisting and empty");
        return directory;
    }
}
