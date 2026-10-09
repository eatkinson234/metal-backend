using System;
using System.Threading;

namespace Ryujinx.Graphics.GAL;

public enum PhaseDecisionReason
{
    Disabled,
    SwapInterval,
    VSyncMode,
    TargetVSyncInterval,
    MissingFeedback,
    InvalidFrequency,
    InvalidFeedback,
    FutureFeedback,
    InvalidDeadline,
    StaleFeedback,
    Deadband,
    Applied,
}

public readonly record struct PresentationPhaseDecision(PhaseDecisionReason Reason, bool CorrectionCalled,
    ulong? FeedbackSequence, long? PresentedTicks, long? WriterObservedTicks, long CorrectionTicks,
    double? WrappedPhaseTicks, double? SignedPhaseErrorTicks)
{
    public static PresentationPhaseDecision Skipped(PhaseDecisionReason reason) =>
        new(reason, false, null, null, null, 0, null, null);
}

public readonly record struct PresentationPhaseOpportunity(long OpportunitySequence, long TickSample,
    long NominalDeadline, long Now, long TicksPerFrame, long TickRemainder, int SwapInterval,
    int SwapIntervalDelay, bool ComposeCalled, bool PhaseEnabled, bool VSyncModeSwitch,
    long TargetVSyncInterval, PresentationPhaseDecision Decision);

public readonly record struct PhaseOpportunityStatistics(long Attempted, long Enqueued, long Dropped,
    long Drained, long Pending, int ProducersInFlight, bool Closed, bool Completed);

// A fixed struct ring with one background consumer. Producers never wait: a
// concurrent producer or a full ring drops that attempt. No row owns GPU objects.
public sealed class PresentationPhaseOpportunityTrace
{
    private readonly PresentationPhaseOpportunity[] _rows;
    private int _closed, _producerActive, _producersInFlight;
    private long _writePosition, _readPosition;
    private long _attempted, _enqueued, _dropped, _drained;

    public int Capacity => _rows.Length;

    public PresentationPhaseOpportunityTrace(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _rows = new PresentationPhaseOpportunity[capacity];
    }

    public bool TryEnqueue(in PresentationPhaseOpportunity opportunity)
    {
        Interlocked.Increment(ref _producersInFlight);
        try
        {
            // Close ends the capture window. Calls entering after it are excluded
            // from its accounting; already admitted attempts finish or drop.
            if (Volatile.Read(ref _closed) != 0) return false;
            long sequence = Interlocked.Increment(ref _attempted);
            if (Interlocked.CompareExchange(ref _producerActive, 1, 0) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            try
            {
                long write = Volatile.Read(ref _writePosition);
                if (write - Volatile.Read(ref _readPosition) >= _rows.Length)
                {
                    Interlocked.Increment(ref _dropped);
                    return false;
                }
                _rows[(int)(write % _rows.Length)] = opportunity with { OpportunitySequence = sequence };
                // Release publishes the complete immutable value to the consumer.
                Volatile.Write(ref _writePosition, write + 1);
                Interlocked.Increment(ref _enqueued);
                return true;
            }
            finally { Volatile.Write(ref _producerActive, 0); }
        }
        finally { Interlocked.Decrement(ref _producersInFlight); }
    }

    // Exactly one consumer, the existing recorder writer, drains this ring.
    public bool TryDequeue(out PresentationPhaseOpportunity opportunity)
    {
        long read = Volatile.Read(ref _readPosition);
        if (read == Volatile.Read(ref _writePosition))
        {
            opportunity = default;
            return false;
        }
        opportunity = _rows[(int)(read % _rows.Length)];
        Volatile.Write(ref _readPosition, read + 1);
        Interlocked.Increment(ref _drained);
        return true;
    }

    public void Close() => Volatile.Write(ref _closed, 1);

    // At completed shutdown these satisfy Attempted=Enqueued+Dropped and
    // Enqueued=Drained+Pending. Concurrent snapshots may be in progress.
    public PhaseOpportunityStatistics Statistics
    {
        get
        {
            bool closed = Volatile.Read(ref _closed) != 0;
            int producers = Volatile.Read(ref _producersInFlight);
            long attempted = Interlocked.Read(ref _attempted);
            long enqueued = Interlocked.Read(ref _enqueued);
            long dropped = Interlocked.Read(ref _dropped);
            long drained = Interlocked.Read(ref _drained);
            long pending = Volatile.Read(ref _writePosition) - Volatile.Read(ref _readPosition);
            return new(attempted, enqueued, dropped, drained, pending, producers, closed,
                closed && producers == 0 && pending == 0 && attempted == enqueued + dropped && enqueued == drained);
        }
    }
}
