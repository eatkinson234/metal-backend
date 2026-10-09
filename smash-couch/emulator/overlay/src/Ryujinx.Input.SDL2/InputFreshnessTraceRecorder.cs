using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Ryujinx.Input.SDL2;

/// <summary>
/// Preallocated, bounded sink for completed input-to-primary-ring observations.
/// Snapshot and export allocate only when called outside the input path.
/// </summary>
internal sealed class InputFreshnessTraceRecorder : INpadInputFreshnessWriteObserver
{
    internal const int DefaultCapacity = 65_536;
    internal const int DefaultDrainTimeoutMilliseconds = 100;
    private const string ObservationWindowVariable = "SMASH_COUCH_INPUT_TRACE_OBSERVATION_WINDOW";
    private const string ObservationWindowMode = "join-p1-45s";
    private const ulong ObservationWindowNanoseconds = 45_000_000_000;
    private readonly bool _observationWindowEnabled;
    private readonly object _observationWindowGate;
    private bool _observationArmed;
    private ulong _observationStart;
    private ulong _observationCutoff;
    private ulong _observationLastObserved;
    private ulong? _observationLastIncluded;
    private ulong? _observationEnd;
    private ulong _observationNumerator;
    private ulong _observationDenominator;
    private string _observationEndReason = "unarmed";
    private long _prearmCallbackCount;
    private long _postWindowCallbackCount;

    private readonly NpadInputFreshnessTraceEvent[] _records;
    private readonly Func<ulong> _nowTicks;
    private long _reserved;
    private int _recorded;
    private int _overflowCount;
    private int _errorCount;
    private int _failed;
    private int _activeWriters;
    private int _closed;

    internal InputFreshnessTraceRecorder(Func<ulong> nowTicks, int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _nowTicks = nowTicks ?? throw new ArgumentNullException(nameof(nowTicks));
        _records = new NpadInputFreshnessTraceEvent[capacity];
        _observationWindowEnabled = string.Equals(
            Environment.GetEnvironmentVariable(ObservationWindowVariable), ObservationWindowMode, StringComparison.Ordinal);
        if (_observationWindowEnabled) _observationWindowGate = new object();
    }

    internal bool IsComplete => Volatile.Read(ref _failed) == 0;
    internal int Capacity => _records.Length;
    internal int RecordCount => Volatile.Read(ref _recorded);
    internal long ReservedCount => Interlocked.Read(ref _reserved);
    internal int OverflowCount => Volatile.Read(ref _overflowCount);
    internal int ErrorCount => Volatile.Read(ref _errorCount);

    public void RecordPrimaryRingWrite(
        in NpadInputFreshnessProvenance provenance,
        PlayerIndex playerIndex,
        NpadInputFreshnessRingKind ringKind,
        ulong samplingNumber,
        ControllerKeys buttons,
        JoystickPosition leftStick,
        JoystickPosition rightStick)
    {
        if (Volatile.Read(ref _failed) != 0 || Volatile.Read(ref _closed) != 0) return;

        Interlocked.Increment(ref _activeWriters);
        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Decrement(ref _activeWriters);
            return;
        }

        try
        {
            if (Volatile.Read(ref _failed) != 0) return;

            // This is intentionally the first clock operation after the HLE callback.
            ulong ringWriteMachTicks = _nowTicks();
            if (_observationWindowEnabled)
            {
                lock (_observationWindowGate)
                {
                    if (Volatile.Read(ref _failed) != 0) return;
                    if (!IncludeObservation(provenance, playerIndex, ringWriteMachTicks)) return;
                    if (StoreRecord(provenance, ringWriteMachTicks, playerIndex, ringKind,
                        samplingNumber, buttons, leftStick, rightStick))
                    {
                        _observationLastIncluded = ringWriteMachTicks;
                    }
                }
            }
            else
            {
                StoreRecord(provenance, ringWriteMachTicks, playerIndex, ringKind,
                    samplingNumber, buttons, leftStick, rightStick);
            }
        }
        catch
        {
            MarkIncomplete();
        }
        finally
        {
            Interlocked.Decrement(ref _activeWriters);
        }
    }

    private bool StoreRecord(in NpadInputFreshnessProvenance provenance, ulong ringWriteMachTicks,
        PlayerIndex playerIndex, NpadInputFreshnessRingKind ringKind, ulong samplingNumber,
        ControllerKeys buttons, JoystickPosition leftStick, JoystickPosition rightStick)
    {
        long index = Interlocked.Increment(ref _reserved) - 1;
        if ((ulong)index >= (ulong)_records.Length)
        {
            Interlocked.Increment(ref _overflowCount);
            Interlocked.Exchange(ref _failed, 1);
            return false;
        }
        if (Volatile.Read(ref _failed) != 0) return false;
        _records[index] = new NpadInputFreshnessTraceEvent(provenance, ringWriteMachTicks,
            playerIndex, ringKind, samplingNumber, buttons, leftStick, rightStick);
        Interlocked.Increment(ref _recorded);
        return true;
    }

    private bool IncludeObservation(in NpadInputFreshnessProvenance provenance, PlayerIndex playerIndex, ulong now)
    {
        if (!_observationArmed)
        {
            if (provenance.ReadStatus != NpadInputFreshnessReadStatus.FreshEnabled ||
                provenance.LogicalSlot != 0 || playerIndex != PlayerIndex.Player1)
            {
                _prearmCallbackCount++;
                return false;
            }
            if (provenance.TimebaseNumerator == 0 || provenance.TimebaseDenominator == 0)
                throw new InvalidOperationException("Observation timebase is missing");
            UInt128 interval = (UInt128)ObservationWindowNanoseconds * provenance.TimebaseDenominator / provenance.TimebaseNumerator;
            if (interval == 0 || interval > ulong.MaxValue || interval > ulong.MaxValue - now)
                throw new InvalidOperationException("Observation cutoff is not representable");
            _observationStart = now;
            _observationCutoff = now + (ulong)interval;
            _observationNumerator = provenance.TimebaseNumerator;
            _observationDenominator = provenance.TimebaseDenominator;
            _observationLastObserved = now;
            _observationArmed = true;
        }
        if (_observationEndReason == "window_elapsed" || now >= _observationCutoff)
        {
            _observationEnd = _observationCutoff;
            _observationEndReason = "window_elapsed";
            _postWindowCallbackCount++;
            return false;
        }
        if (now < _observationLastObserved || provenance.TimebaseNumerator != _observationNumerator ||
            provenance.TimebaseDenominator != _observationDenominator || provenance.ReadStatus == NpadInputFreshnessReadStatus.NotTraced)
            throw new InvalidOperationException("Observation callback clock/provenance is inconsistent");
        _observationLastObserved = now;
        return true;
    }

    private void FinishObservationWindow()
    {
        if (!_observationArmed || _observationEndReason == "window_elapsed") return;
        ulong now = _nowTicks();
        if (now < _observationLastObserved) throw new InvalidOperationException("Observation freeze clock regressed");
        _observationEnd = Math.Min(now, _observationCutoff);
        _observationEndReason = now >= _observationCutoff ? "window_elapsed" : "export_before_window_end";
    }

    public void MarkIncomplete()
    {
        Interlocked.Increment(ref _errorCount);
        Interlocked.Exchange(ref _failed, 1);
    }

    private bool TryFreezeAndCopyRecords(int timeoutMilliseconds, out NpadInputFreshnessTraceEvent[] records)
    {
        Interlocked.Exchange(ref _closed, 1);
        records = Array.Empty<NpadInputFreshnessTraceEvent>();
        long started = Stopwatch.GetTimestamp();
        long timeoutTicks = (long)((UInt128)(uint)Math.Max(0, timeoutMilliseconds) * (ulong)Stopwatch.Frequency / 1_000);

        while (Volatile.Read(ref _activeWriters) != 0)
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            if (elapsedTicks >= timeoutTicks)
            {
                MarkIncomplete();
                return false;
            }

            Thread.Sleep(1);
        }

        if (_observationWindowEnabled)
        {
            try { FinishObservationWindow(); }
            catch { MarkIncomplete(); }
        }
        records = CopyFrozenRecords();
        return true;
    }

    private NpadInputFreshnessTraceEvent[] CopyFrozenRecords()
    {
        if (Volatile.Read(ref _closed) == 0 || Volatile.Read(ref _activeWriters) != 0)
        {
            throw new InvalidOperationException("Input freshness trace must be frozen before copying");
        }

        int count = Math.Min(Volatile.Read(ref _recorded), _records.Length);
        NpadInputFreshnessTraceEvent[] result = new NpadInputFreshnessTraceEvent[count];
        int written = 0;
        long limit = Math.Min(Volatile.Read(ref _reserved), _records.Length);

        for (int i = 0; i < limit && written < count; i++)
        {
            NpadInputFreshnessTraceEvent record = _records[i];
            if (record.Provenance.ReadStatus != NpadInputFreshnessReadStatus.NotTraced)
            {
                result[written++] = record;
            }
        }

        if (written == result.Length) return result;
        Array.Resize(ref result, written);
        return result;
    }

    internal bool TryExportToNewFile(string path)
    {
        InputFreshnessTraceExportResult result = ExportToNewFile(
            path,
            DefaultDrainTimeoutMilliseconds,
            new FileInputFreshnessTraceArtifactPublisher(),
            CancellationToken.None,
            requireCompleteCoverage: true);

        return result.Status == InputFreshnessTraceFinalizationStatus.Completed;
    }

    internal InputFreshnessTraceExportResult TryFinalizeToNewFile(
        string path,
        int drainTimeoutMilliseconds,
        IInputFreshnessTraceArtifactPublisher publisher,
        CancellationToken cancellationToken)
    {
        return ExportToNewFile(path, drainTimeoutMilliseconds, publisher, cancellationToken, requireCompleteCoverage: true);
    }

    private InputFreshnessTraceExportResult ExportToNewFile(
        string path,
        int drainTimeoutMilliseconds,
        IInputFreshnessTraceArtifactPublisher publisher,
        CancellationToken cancellationToken,
        bool requireCompleteCoverage)
    {
        NpadInputFreshnessTraceEvent[] records;
        if (!TryFreezeAndCopyRecords(drainTimeoutMilliseconds, out records))
        {
            return new InputFreshnessTraceExportResult(
                InputFreshnessTraceFinalizationStatus.DrainTimeout,
                snapshotCaptured: false,
                recordCount: 0,
                error: "active-writer-drain-timeout");
        }

        if (requireCompleteCoverage && !IsComplete)
        {
            return new InputFreshnessTraceExportResult(
                InputFreshnessTraceFinalizationStatus.ExportFailed,
                snapshotCaptured: true,
                recordCount: records.Length,
                error: "trace-coverage-incomplete");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] serialized;
            if (_observationWindowEnabled)
            {
                serialized = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "smash-couch-input-freshness-trace-v1",
                trace_complete = IsComplete,
                coverage_end = "export_freeze",
                writers_stopped_before_snapshot = true,
                capacity = Capacity,
                record_count = records.Length,
                overflow_count = OverflowCount,
                error_count = ErrorCount,
                records,
                observation_window = new
                {
                    mode = ObservationWindowMode,
                    scope = "armed_callbacks_only_not_whole_launch",
                    arm_trigger = "first_fresh_enabled_logical_slot0_player1_callback",
                    clock_domain = "darwin_mach_continuous_ticks",
                    max_duration_ns = ObservationWindowNanoseconds,
                    armed = _observationArmed,
                    start_mach_ticks = _observationArmed ? (ulong?)_observationStart : null,
                    end_mach_ticks = _observationEnd,
                    last_included_mach_ticks = _observationLastIncluded,
                    timebase_numerator = _observationArmed ? (ulong?)_observationNumerator : null,
                    timebase_denominator = _observationArmed ? (ulong?)_observationDenominator : null,
                    end_reason = _observationEndReason,
                    prearm_callback_count = _prearmCallbackCount,
                    post_window_callback_count = _postWindowCallbackCount,
                },
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                IncludeFields = true,
                Converters = { new JsonStringEnumConverter() },
            });
            }
            else
            {
                serialized = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "smash-couch-input-freshness-trace-v1",
                trace_complete = IsComplete,
                coverage_end = "export_freeze",
                writers_stopped_before_snapshot = true,
                capacity = Capacity,
                record_count = records.Length,
                overflow_count = OverflowCount,
                error_count = ErrorCount,
                records,
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                IncludeFields = true,
                Converters = { new JsonStringEnumConverter() },
            });
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!publisher.TryPublish(path, serialized, cancellationToken, out string error))
            {
                MarkIncomplete();
                return new InputFreshnessTraceExportResult(
                    InputFreshnessTraceFinalizationStatus.ExportFailed,
                    snapshotCaptured: true,
                    recordCount: records.Length,
                    error ?? "artifact-publication-failed");
            }

            return new InputFreshnessTraceExportResult(
                InputFreshnessTraceFinalizationStatus.Completed,
                snapshotCaptured: true,
                recordCount: records.Length,
                error: null);
        }
        catch (OperationCanceledException)
        {
            MarkIncomplete();
            return new InputFreshnessTraceExportResult(
                InputFreshnessTraceFinalizationStatus.ExportFailed,
                snapshotCaptured: true,
                recordCount: records.Length,
                error: "finalization-canceled");
        }
        catch
        {
            MarkIncomplete();
            return new InputFreshnessTraceExportResult(
                InputFreshnessTraceFinalizationStatus.ExportFailed,
                snapshotCaptured: true,
                recordCount: records.Length,
                error: "serialization-or-publication-failed");
        }
    }
}
