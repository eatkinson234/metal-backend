using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.GAL;

// Private 120 Hz experiment. No drawable ownership, frame queue, or input state.
// The experiment (Enabled) runs only under legacy pacing (DisplayPacing.Legacy). The default pacing uses the
// display lock below instead, fed by the Metal present path rather than the frame-phase recorder.
public static class PresentationPhaseAlignment
{
    public static readonly bool Enabled = OperatingSystem.IsMacOS() && FramePresentationTiming.IsEnabled &&
        DisplayPacing.Legacy &&
        Environment.GetEnvironmentVariable("SMASH_COUCH_PHASE_ALIGN") == "1" &&
        Environment.GetEnvironmentVariable("SMASH_COUCH_PHASE_ALIGN_REFRESH_HZ") == "120";

    public static readonly bool OpportunityTraceEnabled = FramePresentationTiming.IsEnabled &&
        Environment.GetEnvironmentVariable("SMASH_COUCH_PHASE_OPPORTUNITY_TRACE") == "1";

    // No diagnostic storage is allocated when the separate opt-in is off.
    public static readonly PresentationPhaseOpportunityTrace OpportunityTrace = OpportunityTraceEnabled ? new(8192) : null;

    private sealed record Feedback(long PresentedTicks, long ObservedTicks, ulong Sequence);
    private static Feedback _latest;
    private static long _feedbackCount, _adjustments, _totalTicks, _maximumAbsTicks;

    public static void Publish(long presentedTicks, long observedTicks) => Publish(presentedTicks, observedTicks, 0);

    public static void Publish(long presentedTicks, long observedTicks, ulong sequence)
    {
        if (!Enabled || presentedTicks <= 0 || observedTicks < presentedTicks) return;
        Feedback prior = Volatile.Read(ref _latest);
        if (prior != null && (presentedTicks <= prior.PresentedTicks || observedTicks < prior.ObservedTicks)) return;
        Volatile.Write(ref _latest, new Feedback(presentedTicks, observedTicks, sequence));
        Interlocked.Increment(ref _feedbackCount);
    }

    public static void Invalidate() => Volatile.Write(ref _latest, null);

    public static long GetCorrection(long nominalDeadline, long now)
    {
        if (!Enabled) return 0;
        Feedback feedback = Volatile.Read(ref _latest);
        if (feedback == null) return 0;
        long correction = CalculateCorrection(nominalDeadline, now, feedback.PresentedTicks,
            feedback.ObservedTicks, Stopwatch.Frequency);
        RecordCorrection(correction);
        return correction;
    }

    // The immutable snapshot below is the one CalculateCorrection actually consumes.
    // In particular, there is no second feedback read for telemetry.
    public static long GetCorrection(long nominalDeadline, long now, out PresentationPhaseDecision decision)
    {
        if (!Enabled)
        {
            decision = PresentationPhaseDecision.Skipped(PhaseDecisionReason.Disabled);
            return 0;
        }
        Feedback feedback = Volatile.Read(ref _latest);
        if (feedback == null)
        {
            decision = new(PhaseDecisionReason.MissingFeedback, true, null, null, null, 0, null, null);
            return 0;
        }
        decision = EvaluateCorrection(nominalDeadline, now, feedback.PresentedTicks,
            feedback.ObservedTicks, Stopwatch.Frequency, feedback.Sequence);
        RecordCorrection(decision.CorrectionTicks);
        return decision.CorrectionTicks;
    }

    private static void RecordCorrection(long correction)
    {
        if (correction != 0)
        {
            Interlocked.Increment(ref _adjustments);
            Interlocked.Add(ref _totalTicks, correction);
            long magnitude = Math.Abs(correction);
            if (magnitude > Interlocked.Read(ref _maximumAbsTicks)) Interlocked.Exchange(ref _maximumAbsTicks, magnitude);
        }
    }

    // Called only by the diagnostic composer branch, at the original post-Compose,
    // post-remainder opportunity. These are the original correction gates, including
    // the phase opt-in; ComposeCalled and SwapIntervalDelay are observations, not gates.
    public static long ProcessOpportunity(long tickSample, long nominalDeadline, long now,
        long ticksPerFrame, long tickRemainder, int swapInterval, int swapIntervalDelay,
        bool composeCalled, bool vSyncModeSwitch, long targetVSyncInterval,
        out PresentationPhaseDecision decision)
    {
        bool phaseEnabled = Enabled;
        long correction = 0;
        if (!phaseEnabled) decision = PresentationPhaseDecision.Skipped(PhaseDecisionReason.Disabled);
        else if (swapInterval != 1) decision = PresentationPhaseDecision.Skipped(PhaseDecisionReason.SwapInterval);
        else if (!vSyncModeSwitch) decision = PresentationPhaseDecision.Skipped(PhaseDecisionReason.VSyncMode);
        else if (targetVSyncInterval != 60) decision = PresentationPhaseDecision.Skipped(PhaseDecisionReason.TargetVSyncInterval);
        else correction = GetCorrection(nominalDeadline, now, out decision);

        if (OpportunityTraceEnabled)
        {
            PresentationPhaseOpportunity opportunity = new(0, tickSample, nominalDeadline, now,
                ticksPerFrame, tickRemainder, swapInterval, swapIntervalDelay, composeCalled,
                phaseEnabled, vSyncModeSwitch, targetVSyncInterval, decision);
            OpportunityTrace.TryEnqueue(in opportunity);
        }
        return correction;
    }

    // CPU-testable diagnostic classification. The returned correction comes from
    // the unchanged arithmetic below; the phase is defined only after its guards.
    public static PresentationPhaseDecision EvaluateCorrection(long nominalDeadline, long now,
        long presentedTicks, long observedTicks, long frequency, ulong feedbackSequence = 0)
    {
        long correction = CalculateCorrection(nominalDeadline, now, presentedTicks, observedTicks, frequency);
        PhaseDecisionReason reason;
        if (frequency < 10000) reason = PhaseDecisionReason.InvalidFrequency;
        else if (presentedTicks <= 0 || observedTicks < presentedTicks) reason = PhaseDecisionReason.InvalidFeedback;
        else if (now < observedTicks) reason = PhaseDecisionReason.FutureFeedback;
        else if (nominalDeadline <= now || nominalDeadline - now > frequency / 30) reason = PhaseDecisionReason.InvalidDeadline;
        else if (now - presentedTicks > frequency / 4) reason = PhaseDecisionReason.StaleFeedback;
        else
        {
            double refresh = frequency / 120.0;
            double lead = frequency * 0.012;
            double phase = ((nominalDeadline - presentedTicks) + lead) % refresh;
            if (phase < 0) phase += refresh;
            if (phase > refresh / 2) phase -= refresh;
            return new(correction == 0 ? PhaseDecisionReason.Deadband : PhaseDecisionReason.Applied,
                true, feedbackSequence, presentedTicks, observedTicks, correction, phase, -phase);
        }
        return new(reason, true, feedbackSequence, presentedTicks, observedTicks, correction, null, null);
    }

    // Positive means a later next composer tick. No correction exceeds 0.10 ms.
    public static long CalculateCorrection(long nominalDeadline, long now, long presentedTicks,
        long observedTicks, long frequency)
    {
        if (frequency < 10000 || presentedTicks <= 0 || observedTicks < presentedTicks || now < observedTicks ||
            nominalDeadline <= now || nominalDeadline - now > frequency / 30 ||
            now - presentedTicks > frequency / 4) return 0;
        double refresh = frequency / 120.0;
        double lead = frequency * 0.012;
        double phase = ((nominalDeadline - presentedTicks) + lead) % refresh;
        if (phase < 0) phase += refresh;
        if (phase > refresh / 2) phase -= refresh;
        double error = -phase;
        long limit = frequency / 10000;
        if (Math.Abs(error) <= limit) return 0;
        return (long)Math.Clamp(error, -limit, limit);
    }

    public static (long FeedbackCount, long Adjustments, long TotalTicks, long MaximumAbsTicks) Statistics =>
        (Interlocked.Read(ref _feedbackCount), Interlocked.Read(ref _adjustments),
         Interlocked.Read(ref _totalTicks), Interlocked.Read(ref _maximumAbsTicks));

    // Display lock (default pacing). The Metal present path publishes the refresh grid it verified from
    // presented-time feedback: the newest on-glass time and the measured refresh period, in Stopwatch
    // ticks, only while that grid is fresh and 60 or 120 Hz, with the lead to use. The composer then aims
    // its 60 Hz tick that lead before a refresh, so the frame is committed just in time for it. The lead
    // starts at DisplayLockLeadSeconds (tick to commit ~0.2 ms, GPU p99.9 ~1.55 ms, latch ~1.5 ms,
    // residual tick jitter) and the present path lengthens it when frames still miss their refresh.
    // Without published feedback the timer runs free exactly as before.
    public const double DisplayLockLeadSeconds = 0.0045;

    private sealed record DisplayGrid(long AnchorTicks, double PeriodTicks, long ObservedTicks, double LeadTicks);
    private static DisplayGrid _displayGrid;
    private static long _lockTicks, _lockAdjustments, _lockTotalTicks, _lockMaximumAbsTicks;

    public static void PublishDisplayGrid(long anchorTicks, double periodTicks, long observedTicks) =>
        PublishDisplayGrid(anchorTicks, periodTicks, observedTicks, Stopwatch.Frequency * DisplayLockLeadSeconds);

    public static void PublishDisplayGrid(long anchorTicks, double periodTicks, long observedTicks, double leadTicks)
    {
        if (DisplayPacing.Legacy || anchorTicks <= 0 || observedTicks < anchorTicks || !(periodTicks > 0)) return;
        Volatile.Write(ref _displayGrid, new DisplayGrid(anchorTicks, periodTicks, observedTicks, leadTicks));
    }

    public static void InvalidateDisplayGrid() => Volatile.Write(ref _displayGrid, null);

    // Composer thread only.
    public static long GetDisplayLockCorrection(long nominalDeadline, long now)
    {
        DisplayGrid grid = Volatile.Read(ref _displayGrid);
        if (grid == null) return 0;
        long correction = CalculateDisplayLockCorrection(nominalDeadline, now, grid.AnchorTicks, grid.ObservedTicks,
            grid.PeriodTicks, Stopwatch.Frequency, grid.LeadTicks);
        _lockTicks++;
        if (correction != 0)
        {
            _lockAdjustments++;
            _lockTotalTicks += correction;
            _lockMaximumAbsTicks = Math.Max(_lockMaximumAbsTicks, Math.Abs(correction));
        }
        return correction;
    }

    public static long CalculateDisplayLockCorrection(long nominalDeadline, long now, long anchorTicks,
        long observedTicks, double periodTicks, long frequency) =>
        CalculateDisplayLockCorrection(nominalDeadline, now, anchorTicks, observedTicks, periodTicks, frequency,
            frequency * DisplayLockLeadSeconds);

    // Positive means a later next composer tick. Same slew limit (0.10 ms per tick), deadband and
    // staleness guard (250 ms) as CalculateCorrection, but with the measured refresh period and the
    // display-lock lead. Zero for stale, future or invalid feedback, a missed or distant deadline, any
    // refresh rate that does not fit one 60 Hz frame into exactly one or two refreshes (e.g. 144 Hz), and
    // a lead that is not positive and within one 60 Hz frame.
    public static long CalculateDisplayLockCorrection(long nominalDeadline, long now, long anchorTicks,
        long observedTicks, double periodTicks, long frequency, double leadTicks)
    {
        if (frequency < 10000 || anchorTicks <= 0 || observedTicks < anchorTicks || now < observedTicks ||
            nominalDeadline <= now || nominalDeadline - now > frequency / 30 ||
            now - anchorTicks > frequency / 4 || !IsLockablePeriod(periodTicks, frequency) ||
            !(leadTicks > 0) || leadTicks > frequency / 60.0) return 0;
        double lead = leadTicks;
        double phase = ((nominalDeadline - anchorTicks) + lead) % periodTicks;
        if (phase < 0) phase += periodTicks;
        if (phase > periodTicks / 2) phase -= periodTicks;
        double error = -phase;
        long limit = frequency / 10000;
        if (Math.Abs(error) <= limit) return 0;
        return (long)Math.Clamp(error, -limit, limit);
    }

    // One emulated 60 Hz frame must span exactly one or two refreshes: 60 or 120 Hz (59.94 and
    // 119.88 Hz pass; the guest then follows the display clock, 0.1% slow).
    public static bool IsLockablePeriod(double periodTicks, long frequency)
    {
        if (!(periodTicks > 0) || double.IsInfinity(periodTicks) || frequency < 10000) return false;
        double frame = frequency / 60.0;
        double refreshes = Math.Round(frame / periodTicks);
        return (refreshes == 1 || refreshes == 2) && Math.Abs(refreshes * periodTicks - frame) <= frame * 0.005;
    }

    // Composer-thread counters; read for logging only.
    public static (long Ticks, long Adjustments, long TotalTicks, long MaximumAbsTicks) DisplayLockStatistics =>
        (Volatile.Read(ref _lockTicks), Volatile.Read(ref _lockAdjustments),
         Volatile.Read(ref _lockTotalTicks), Volatile.Read(ref _lockMaximumAbsTicks));
}
