using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // Default present policy (everything except DisplayPacing.Legacy): aims each Switch-vsync frame
    // at a real display refresh from presented-time feedback (PresentRefreshGrid) and publishes the
    // verified grid to the composer's display lock. Target returns NaN for a frame that keeps the CPU
    // schedule: no fresh fixed 60/120 Hz grid yet, a stale screen sample, a resize or full-screen
    // transition, a screen change, or a disabled grid. Render thread only, except the Request methods.
    [SupportedOSPlatform("macos")]
    internal sealed unsafe class RefreshAnchoredPacing
    {
        private const double SampleStaleAfter = 2.0;
        private const double TransitionHoldOff = 0.5;
        private const double LogInterval = 60.0;
        private const int DrainBatch = 16;

        private readonly PresentedTimeObserver _observer;
        private readonly PresentRefreshGrid _grid = new();
        private DisplayRefreshSampler.Sample _sample;
        private long _sequence, _registerErrors, _cpuFrames;
        private int _resetRequested, _holdOffRequested;
        private bool _anchored;
        private double _nextLog, _lastLog;

        private RefreshAnchoredPacing(PresentedTimeObserver observer)
        {
            _observer = observer;
        }

        internal static RefreshAnchoredPacing TryCreate()
        {
            PresentedTimeObserver observer = PresentedTimeObserver.TryCreate(out string error);
            if (observer == null)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, $"[CouchPacing] presented-time feedback unavailable; CPU present schedule only: {error}");
                return null;
            }
            Logger.Info?.PrintMsg(LogClass.Gpu, "[CouchPacing] refresh-anchored presents and display-locked vsync on fixed 60/120 Hz screens");
            return new RefreshAnchoredPacing(observer);
        }

        // Any thread: VSync mode change or a skipped presentation.
        internal void RequestReset() => Volatile.Write(ref _resetRequested, 1);

        // Any thread: the drawable size changed (resize or full-screen transition).
        internal void RequestHoldOff() => Volatile.Write(ref _holdOffRequested, 1);

        internal double Target(double now, nint drawable, double previousTarget)
        {
            if (Interlocked.Exchange(ref _resetRequested, 0) != 0) _grid.ResetDecisions();
            if (Interlocked.Exchange(ref _holdOffRequested, 0) != 0) _grid.HoldOff(now, TransitionHoldOff);

            UpdateDisplay(now);
            Drain(now);

            bool anchored = _grid.TryChoose(now, previousTarget, out double refresh, out double target);
            long sequence = ++_sequence;
            if (_observer.Register(drawable, sequence) != 0) _registerErrors++;
            _grid.Record(sequence, anchored ? refresh : 0);

            if (anchored)
            {
                PresentationPhaseAlignment.PublishDisplayGrid((long)(_grid.Anchor * Stopwatch.Frequency),
                    _grid.Period * Stopwatch.Frequency, (long)(now * Stopwatch.Frequency), _grid.LockLead * Stopwatch.Frequency);
            }
            else
            {
                PresentationPhaseAlignment.InvalidateDisplayGrid();
                _cpuFrames++;
            }

            if (now >= _nextLog || (anchored != _anchored && now >= _lastLog + 1.0)) Log(now, anchored);
            return anchored ? target : double.NaN;
        }

        private void UpdateDisplay(double now)
        {
            DisplayRefreshSampler.Sample sample = DisplayRefreshSampler.Latest;
            if (sample == null || now - sample.SampledTicks / (double)Stopwatch.Frequency > SampleStaleAfter)
            {
                _grid.SetDisplay(0, false);
                return;
            }
            if (!ReferenceEquals(sample, _sample))
            {
                if (_sample != null && (sample.ScreenNumber != _sample.ScreenNumber || sample.FullScreen != _sample.FullScreen))
                {
                    _grid.HoldOff(now, TransitionHoldOff);
                }
                _sample = sample;
            }
            _grid.SetDisplay(sample.MinimumInterval,
                PresentRefreshGrid.IsFixedRefresh(sample.MinimumInterval, sample.MaximumInterval, sample.Granularity));
        }

        private void Drain(double now)
        {
            FramePhaseEvent* events = stackalloc FramePhaseEvent[DrainBatch];
            for (int round = 0; round < 8; round++)
            {
                int count = _observer.Drain(events, DrainBatch);
                for (int i = 0; i < count; i++)
                {
                    if (events[i].CallbackDrawableId == events[i].ExpectedDrawableId)
                    {
                        _grid.OnPresented((long)events[i].Sequence, events[i].PresentedTime, now);
                    }
                }
                if (count < DrainBatch) break;
            }
        }

        private void Log(double now, bool anchored)
        {
            _anchored = anchored;
            _lastLog = now;
            _nextLog = now + LogInterval;
            PresentRefreshGrid g = _grid;
            var lockStats = PresentationPhaseAlignment.DisplayLockStatistics;
            var margins = g.TakeMarginPercentiles();
            double ms = 1000.0 / Stopwatch.Frequency;
            Logger.Info?.PrintMsg(LogClass.Gpu, string.Create(CultureInfo.InvariantCulture,
                $"[CouchPacing] mode={(anchored ? "anchored" : "cpu")};refreshHz={(g.Period > 0 ? 1 / g.Period : 0):F4};k={g.RefreshesPerFrame};" +
                $"screen={_sample?.ScreenNumber};fullScreen={_sample?.FullScreen};sessionOff={g.DisabledForSession};" +
                $"leadMs={g.LockLead * 1000:F1};leadRaises={g.LeadRaises};nearGiveUps={g.NearGiveUps};marginP1Ms={margins.P1 * 1000:F2};marginP50Ms={margins.P50 * 1000:F2};" +
                $"frames={g.Frames};cpuFrames={_cpuFrames};entries={g.Entries};holds={g.Holds};reanchors={g.Reanchors};" +
                $"sheds={g.Sheds};burstSheds={g.BurstSheds};shedAfter={g.ShedAfter};onTarget={g.OnTarget};late={g.Late};early={g.Early};" +
                $"settledSheds={g.SettledSheds};settledLeadRaises={g.SettledLeadRaises};" +
                $"offGrid={g.OffGrid};skipped={g.Skipped};invalid={g.Invalid};disables={g.Disables};registerErrors={_registerErrors};" +
                $"lockTicks={lockStats.Ticks};lockAdjustments={lockStats.Adjustments};lockTotalMs={lockStats.TotalTicks * ms:F3};" +
                $"lockMaxMs={lockStats.MaximumAbsTicks * ms:F3}"));
        }
    }
}
