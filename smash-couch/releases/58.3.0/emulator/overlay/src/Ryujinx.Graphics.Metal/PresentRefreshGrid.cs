using Ryujinx.Graphics.GAL;
using System;
using System.Numerics;

namespace Ryujinx.Graphics.Metal
{
    // Refresh-anchored present decisions: pure arithmetic, no native calls (CPU-tested by
    // tests/display-pacing). Times are host seconds; Stopwatch, CACurrentMediaTime and
    // MTLDrawable.presentedTime share mach time. Render thread only.
    //
    // Grid: the display's refresh times, anchored on the newest presentedTime of our own drawables, with
    // the period read from the screen (refined from presented times over about a second). Only fixed
    // 60 or 120 Hz grids are used; anything else (144 Hz, variable refresh) leaves the CPU schedule.
    // Each frame aims at the refresh k after the previous frame's (k = 2 at 120 Hz, 1 at 60 Hz), so the
    // cadence never changes. Its commit margin m (chosen refresh minus now) stays between HoldMargin and
    // one refresh plus ShedMargin:
    //  - m < -period/2 (a skipped or stalled frame: no guest frame for a tick, GC pause, catch-up): the
    //    chain is broken, not tight; re-anchor on the first refresh at least HoldMargin away. Not a
    //    disturbance for the shed backoff, so hitchy play does not park on the later refresh.
    //  - m < MissMargin, or on two of the last four frames m < HoldMargin (GPU p99.9 ~1.55 ms + latch
    //    ~1.5 ms) or the frame was shown after its (near) refresh despite a normal margin: hold one
    //    refresh later. One hold per drift crossing. A single late commit (1-3 ms short) keeps its
    //    refresh: it usually still makes it, and if not the next frame is back on cadence.
    //  - every m of the last ShedAfterFrames frames minus one refresh >= ShedMargin: all of them would
    //    have made the refresh before, so take it (one short interval). ShedMargin > HoldMargin is the
    //    hysteresis: a shed can never set up the next hold, and large tick jitter just keeps the later
    //    refresh. When a hold follows a shed within 30 s (frequent late commits, e.g. loading), the
    //    clean run needed grows 4x, up to 32 s, so the extra refresh absorbs late commits (up to ~9 ms)
    //    instead of costing a hold and a shed each; 60 s without a hold or an absorbed late commit
    //    restores ShedAfterFrames.
    //  - m > one frame + ShedMargin (composer catch-up burst): one refresh earlier at once. At 60 Hz
    //    that drops the older frame, so only for a real catch-up (commits under half a frame apart);
    //    after a hold the clean run above sheds instead, with its backoff.
    // The present target sits half a refresh plus the ~1.5 ms compositor latch before the chosen
    // refresh: the middle of the window of targets that show the frame on exactly that refresh.
    // Composer lock lead (published with the grid): 4.5 ms, so a locked commit is ~4.3 ms before the
    // near refresh and the present is effectively immediate (target already past); the frame makes it
    // only if GPU completion plus the compositor latch fits. That latch is unmeasured for such presents,
    // so the lead adapts (LeadStep, up to MaximumLockLead; back to 4.5 ms on a display change or
    // transition). Near-refresh frames shown late despite a normal margin raise it one step when two of
    // them force a hold or three come within LateWindow; so do TightFramesForRaise frames in a row on
    // the later refresh with too little spare to shed (a commit steadily later than the lead allows).
    // Self-check: presented times off the grid, frames shown earlier than chosen, and near-refresh
    // frames shown late at the maximum lead are anomalies; three within 5 s disable the grid for 30 s
    // (for the session after the third time), which also leaves the composer free-running.
    internal sealed class PresentRefreshGrid
    {
        public const double FramePeriod = 1.0 / 60.0;
        public const double HoldMargin = 0.003;
        public const double MissMargin = 0.001;
        public const double ShedMargin = 0.00325;
        public const double LatchAllowance = 0.0015;
        public const double StaleAfter = 0.25;
        public const int ShedAfterFrames = 32;
        public const double LeadStep = 0.0005;
        public const double MaximumLockLead = 0.0075;
        public const int TightFramesForRaise = 90;
        private const int MaximumShedAfterFrames = 1920;
        private const double BackoffWithin = 30.0;
        private const double BackoffReset = 60.0;
        private const double LateWindow = 10.0;
        private const double LeadSettle = 0.5;
        private const int ConsistentNeeded = 3;
        private const int History = 64;
        private const double MarginBinWidth = 0.00025;
        private const double MarginLow = -0.010;

        private struct Choice
        {
            public long Sequence;
            public long Epoch;
            public long Window;
            public double Refresh;
            public double Margin;
        }

        private readonly Choice[] _history = new Choice[History];
        private readonly double[] _anomalies = new double[3];

        private double _nominal, _period;
        private int _refreshesPerFrame;
        private bool _compatible;

        private double _anchor, _baseTime;
        private int _consistent;
        private long _epoch;

        private double _lastRefresh, _lastChoose, _lastMargin;
        private int _cleanFrames, _shortMargins, _lateMargins, _pendingLates, _tightFrames, _shedAfter = ShedAfterFrames;
        private long _window;
        private double _lastShed = double.NegativeInfinity, _lastDisturbance = double.NegativeInfinity;

        private double _lockLead = PresentationPhaseAlignment.DisplayLockLeadSeconds;
        private double _lastRaise = double.NegativeInfinity;
        private readonly double[] _lateEvidence = { double.NegativeInfinity, double.NegativeInfinity };

        private readonly int[] _marginBins = new int[160];
        private int _marginCount;

        private double _disabledUntil;
        private int _anomalyCount;

        public long Frames, Entries, Holds, Reanchors, Sheds, BurstSheds, LeadRaises;
        public long OnTarget, Late, Early, OffGrid, Skipped, Invalid, Disables;

        public double Period => _period;
        public double Anchor => _anchor;
        public int RefreshesPerFrame => _refreshesPerFrame;
        public int ShedAfter => _shedAfter;
        public double LockLead => _lockLead;
        public bool Compatible => _compatible;
        public bool DisabledForSession => double.IsPositiveInfinity(_disabledUntil);

        // A fixed-rate screen: the AppKit refresh range is a single value and the update granularity is that
        // refresh (or not reported). Variable-refresh and finer-granularity screens keep the CPU schedule.
        public static bool IsFixedRefresh(double minimumInterval, double maximumInterval, double granularity)
        {
            if (!(minimumInterval > 0) || !(maximumInterval > 0) || double.IsInfinity(maximumInterval)) return false;
            if (Math.Abs(maximumInterval - minimumInterval) > minimumInterval * 0.005) return false;
            return !(granularity > 0) || Math.Abs(granularity - minimumInterval) <= minimumInterval * 0.01;
        }

        // Same rule as the composer lock: one 60 Hz frame spans exactly one or two refreshes.
        public static bool IsCompatible(double period, out int refreshesPerFrame)
        {
            refreshesPerFrame = 0;
            if (!PresentationPhaseAlignment.IsLockablePeriod(period * 1e9, 1_000_000_000)) return false;
            refreshesPerFrame = (int)Math.Round(FramePeriod / period);
            return true;
        }

        // Screen period and whether it is a fixed-rate screen. A change drops all evidence.
        public void SetDisplay(double period, bool fixedRefresh)
        {
            bool compatible = fixedRefresh && IsCompatible(period, out _);
            if (compatible == _compatible && (!compatible || Math.Abs(period - _nominal) <= _nominal * 0.001)) return;
            _compatible = compatible;
            _nominal = compatible ? period : 0;
            _refreshesPerFrame = 0;
            if (compatible) IsCompatible(period, out _refreshesPerFrame);
            InvalidateEvidence();
        }

        public void InvalidateEvidence()
        {
            _anchor = 0;
            _baseTime = 0;
            _consistent = 0;
            _period = _nominal;
            _epoch++;
            // A new screen or presentation path (full screen vs composited) has its own latch: re-learn.
            _lockLead = PresentationPhaseAlignment.DisplayLockLeadSeconds;
            _lastRaise = double.NegativeInfinity;
            Array.Fill(_lateEvidence, double.NegativeInfinity);
            ResetDecisions();
        }

        // Keep the CPU schedule for a while (window resize, full-screen transition) and re-verify the grid.
        public void HoldOff(double now, double seconds)
        {
            if (!DisabledForSession) _disabledUntil = Math.Max(_disabledUntil, now + seconds);
            InvalidateEvidence();
        }

        public void ResetDecisions()
        {
            _lastRefresh = 0;
            ResetWindow();
        }

        private void ResetWindow()
        {
            _cleanFrames = 0;
            _shortMargins = 0;
            _lateMargins = 0;
            _pendingLates = 0;
            _tightFrames = 0;
            _window++;
        }

        public bool IsUsable(double now) => _compatible && _anchor > 0 && _consistent >= ConsistentNeeded &&
            now - _anchor <= StaleAfter && now >= _disabledUntil;

        // One presented-handler record of our own drawable. presented == 0: never shown (replaced or skipped).
        public void OnPresented(long sequence, double presented, double now)
        {
            if (!double.IsFinite(presented) || presented < 0 || presented > now + 0.001 || (presented > 0 && presented < now - 2.0))
            {
                Invalid++;
                return;
            }
            if (presented == 0)
            {
                Skipped++;
                return;
            }
            if (!_compatible || DisabledForSession) return;

            if (_anchor > 0)
            {
                double offset = presented - _anchor;
                double residual = offset - Math.Round(offset / _period) * _period;
                if (Math.Abs(residual) > _period / 8)
                {
                    // Not on the grid we aimed at (moved display, mode change, variable timing):
                    // restart from this sample and stop judging frames chosen on the old grid.
                    OffGrid++;
                    _epoch++;
                    _anchor = presented;
                    _baseTime = presented;
                    _consistent = 1;
                    _period = _nominal;
                    ResetDecisions();
                    Anomaly(now);
                    return;
                }
            }
            else
            {
                _baseTime = presented;
            }

            ref Choice choice = ref _history[(int)(sequence & (History - 1))];
            if (choice.Sequence == sequence && choice.Epoch == _epoch && choice.Refresh > 0)
            {
                double difference = presented - choice.Refresh;
                if (Math.Abs(difference) <= _period / 4) OnTarget++;
                else if (difference > 0)
                {
                    Late++;
                    OnLate(choice.Window, choice.Margin, now);
                }
                else
                {
                    Early++;
                    Anomaly(now);
                }
            }

            _anchor = Math.Max(_anchor, presented);
            _consistent++;

            // Refine the period over 1-1.5 s of on-grid samples; the screen's nominal value stays the bound.
            double span = presented - _baseTime;
            if (span > 1.5) _baseTime = presented;
            else if (span >= 1.0)
            {
                double measured = span / Math.Round(span / _period);
                if (Math.Abs(measured - _nominal) <= _nominal * 0.002) _period = measured;
                _baseTime = presented;
            }
        }

        // A frame shown after the near refresh it was chosen for although its commit margin was normal: GPU
        // completion plus the compositor latch outran the lock lead. (A short margin already explains a late
        // frame and counted when it was chosen; a lead cannot help a frame late for the far refresh.) Within
        // the current decision window it counts like a short margin; three within LateWindow raise the lead.
        // Ignored for the lead for LeadSettle after a raise (frames committed before the composer moved).
        private void OnLate(long window, double margin, double now)
        {
            if (margin < HoldMargin || margin >= _period) return;
            if (window == _window) _pendingLates++;
            if (now - _lastRaise < LeadSettle) return;
            if (_lockLead >= MaximumLockLead - 1e-9)
            {
                // The longest lead does not fit either: the display does not behave as modelled.
                Anomaly(now);
            }
            else if (now - Math.Min(_lateEvidence[0], _lateEvidence[1]) <= LateWindow)
            {
                RaiseLead(now);
            }
            else
            {
                int older = _lateEvidence[0] <= _lateEvidence[1] ? 0 : 1;
                _lateEvidence[older] = now;
            }
        }

        private void RaiseLead(double now)
        {
            if (now - _lastRaise < LeadSettle || _lockLead >= MaximumLockLead - 1e-9) return;
            _lockLead = Math.Min(_lockLead + LeadStep, MaximumLockLead);
            _lastRaise = now;
            Array.Fill(_lateEvidence, double.NegativeInfinity);
            LeadRaises++;
        }

        private void Anomaly(double now)
        {
            _anomalies[_anomalyCount++ % 3] = now;
            if (_anomalyCount >= 3 && now - _anomalies[_anomalyCount % 3] <= 5.0)
            {
                Disables++;
                _anomalyCount = 0;
                HoldOff(now, Disables >= 3 ? double.PositiveInfinity : 30.0);
            }
        }

        private double FirstRefreshAtOrAfter(double time)
        {
            return _anchor + Math.Ceiling((time - _anchor) / _period - 1e-9) * _period;
        }

        // Chooses the refresh for a frame committed now and the presentDrawable:atTime: target for it.
        // previousTarget is the last target sent (either policy), 0 if none. False: use the CPU schedule.
        public bool TryChoose(double now, double previousTarget, out double refresh, out double target)
        {
            refresh = 0;
            target = 0;
            if (!IsUsable(now))
            {
                ResetDecisions();
                return false;
            }

            double period = _period;
            double frame = period * _refreshesPerFrame;
            double earliest = FirstRefreshAtOrAfter(now + HoldMargin);
            if (_lastRefresh > 0)
            {
                // Re-express the previous choice on the current anchor; a jump means a different grid.
                double snapped = _anchor + Math.Round((_lastRefresh - _anchor) / period) * period;
                _lastRefresh = Math.Abs(snapped - _lastRefresh) <= period / 4 ? snapped : 0;
            }
            // A shed may put two frames one refresh apart (k = 2), or on one refresh at 60 Hz (the older is
            // dropped). Compared with a quarter-refresh tolerance: these are sums of the same doubles.
            double earliestShed = _lastRefresh + period * (_refreshesPerFrame - 1) - period / 4;

            double choice;
            if (_lastRefresh <= 0 || now - _lastChoose > StaleAfter)
            {
                choice = earliest;
                if (previousTarget > 0 && now - previousTarget < 0.1)
                {
                    // Not on or before the refresh the previous (CPU-scheduled) frame should reach.
                    choice = Math.Max(choice, FirstRefreshAtOrAfter(previousTarget + LatchAllowance) + period);
                }
                Entries++;
                ResetWindow();
            }
            else
            {
                choice = _lastRefresh + frame;
                double margin = choice - now;
                bool late = _pendingLates > 0;
                _pendingLates = 0;
                if (margin < -period / 2)
                {
                    choice = earliest;
                    Reanchors++;
                    ResetWindow();
                }
                else
                {
                    _shortMargins = ((_shortMargins << 1) | (margin < HoldMargin || late ? 1 : 0)) & 0xF;
                    _lateMargins = ((_lateMargins << 1) | (late ? 1 : 0)) & 0xF;
                    if (margin < MissMargin || BitOperations.PopCount((uint)_shortMargins) >= 2)
                    {
                        // Late frames had margins of at least HoldMargin, where earliest is this same refresh.
                        choice = Math.Max(earliest, choice + period);
                        Holds++;
                        if (BitOperations.PopCount((uint)_lateMargins) >= 2) RaiseLead(now);
                        if (now - _lastShed < BackoffWithin) _shedAfter = Math.Min(_shedAfter * 4, MaximumShedAfterFrames);
                        _lastDisturbance = now;
                        ResetWindow();
                    }
                    else if (margin > frame + ShedMargin && (_refreshesPerFrame > 1 || now - _lastChoose < frame / 2) &&
                        choice - period >= earliestShed)
                    {
                        choice -= period;
                        BurstSheds++;
                        ResetWindow();
                    }
                    else
                    {
                        if (now - _lastDisturbance > BackoffReset) _shedAfter = ShedAfterFrames;
                        double spare = margin - period;
                        if (spare >= ShedMargin) _cleanFrames++;
                        else
                        {
                            // A late commit absorbed by the extra refresh counts as a disturbance too.
                            if (_cleanFrames > 0) _lastDisturbance = now;
                            _cleanFrames = 0;
                        }
                        // On the later refresh without enough spare to shed, frame after frame: the commit
                        // comes steadily later than the lead allows, so lengthen the lead.
                        if (spare >= 0 && spare < ShedMargin)
                        {
                            if (++_tightFrames >= TightFramesForRaise)
                            {
                                _tightFrames = 0;
                                RaiseLead(now);
                            }
                        }
                        else _tightFrames = 0;
                        if (_cleanFrames >= _shedAfter && choice - period >= earliestShed)
                        {
                            choice -= period;
                            Sheds++;
                            _lastShed = now;
                            ResetWindow();
                        }
                    }
                }
            }

            _lastRefresh = choice;
            _lastChoose = now;
            _lastMargin = choice - now;
            CountMargin(_lastMargin);
            refresh = choice;
            target = choice - period / 2 - LatchAllowance;
            Frames++;
            return true;
        }

        private void CountMargin(double margin)
        {
            int bin = (int)Math.Floor((margin - MarginLow) / MarginBinWidth);
            _marginBins[Math.Clamp(bin, 0, _marginBins.Length - 1)]++;
            _marginCount++;
        }

        // Commit-margin percentiles of the anchored frames since the last call, as 0.25 ms bin centres
        // (-10..+30 ms, clamped); NaN when there were none. For the periodic log.
        public (double P1, double P50) TakeMarginPercentiles()
        {
            (double, double) result = (MarginPercentile(0.01), MarginPercentile(0.5));
            Array.Clear(_marginBins);
            _marginCount = 0;
            return result;
        }

        private double MarginPercentile(double p)
        {
            if (_marginCount == 0) return double.NaN;
            long needed = Math.Max(1, (long)Math.Ceiling(p * _marginCount));
            long seen = 0;
            for (int i = 0; i < _marginBins.Length; i++)
            {
                seen += _marginBins[i];
                if (seen >= needed) return MarginLow + (i + 0.5) * MarginBinWidth;
            }
            return MarginLow + (_marginBins.Length - 0.5) * MarginBinWidth;
        }

        // Remembers which refresh a registered drawable was aimed at (0: CPU schedule) for the self-check.
        public void Record(long sequence, double refresh)
        {
            ref Choice choice = ref _history[(int)(sequence & (History - 1))];
            choice.Sequence = sequence;
            choice.Epoch = _epoch;
            choice.Window = _window;
            choice.Refresh = refresh;
            choice.Margin = refresh > 0 ? _lastMargin : 0;
        }
    }
}
