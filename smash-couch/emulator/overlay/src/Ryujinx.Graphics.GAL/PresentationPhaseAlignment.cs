using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.GAL;

// Private 120 Hz experiment. No drawable ownership, frame queue, or input state.
public static class PresentationPhaseAlignment
{
    public static readonly bool Enabled = OperatingSystem.IsMacOS() && FramePresentationTiming.IsEnabled &&
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
}
