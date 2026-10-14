using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    // When the guest frame's own GPU fence signaled. Written once: at QueueBuffer when the fence had already
    // signaled (or there is none), otherwise by the syncpoint callback on the GPU thread.
    sealed class FencePhaseStamp
    {
        public long QueuedTicks;
        public long ReadyTicks;
        public bool ReadyAtQueue;
    }

    // Opt-in diagnostic (SMASH_COUCH_FENCE_PHASE=1) for the ~48 ms guest queue residence (QueueBuffer to composer
    // acquire, depth 3 in the frame-phase runs): splits each frame's residence into its GPU work still running
    // (QueueBuffer to its fence signaling) and a finished frame waiting (fence to acquire), and logs one summary
    // line every 10 s. Changes no behavior; off, no frame is stamped and no fence callback is registered.
    static class FencePhase
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("SMASH_COUCH_FENCE_PHASE") == "1";

        private const int Capacity = 1024;
        private static readonly double[] _residence = new double[Capacity];
        private static readonly double[] _gpu = new double[Capacity];
        private static readonly double[] _idle = new double[Capacity];
        private static readonly double[] _depth = new double[Capacity];
        private static int _count, _readyAtQueue, _notReadyAtAcquire, _unstamped;
        private static long _windowStart;

        // QueueBuffer, under the queue lock, after item.Fence is set.
        public static void OnQueued(GpuContext gpu, BufferItem item)
        {
            FencePhaseStamp stamp = new() { QueuedTicks = Stopwatch.GetTimestamp() };
            if (item.Fence.FenceCount == 0)
            {
                stamp.ReadyTicks = stamp.QueuedTicks;
            }
            else
            {
                // Runs at once when the syncpoint has already been reached.
                item.Fence.RegisterCallback(gpu, _ => Volatile.Write(ref stamp.ReadyTicks, Stopwatch.GetTimestamp()));
            }
            stamp.ReadyAtQueue = Volatile.Read(ref stamp.ReadyTicks) != 0;
            item.FencePhase = stamp;
        }

        // Composer thread, after a successful acquire.
        public static void OnAcquired(BufferItem item)
        {
            long acquired = Stopwatch.GetTimestamp();
            if (_windowStart == 0) _windowStart = acquired;
            FencePhaseStamp stamp = item.FencePhase;
            if (stamp == null)
            {
                _unstamped++;
            }
            else if (_count < Capacity)
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                long ready = Volatile.Read(ref stamp.ReadyTicks);
                double residence = (acquired - stamp.QueuedTicks) * ms;
                _residence[_count] = residence;
                if (ready != 0 && ready <= acquired)
                {
                    _gpu[_count] = (ready - stamp.QueuedTicks) * ms;
                    _idle[_count] = (acquired - ready) * ms;
                }
                else
                {
                    // Still rendering when acquired: the whole residence was GPU work.
                    _gpu[_count] = residence;
                    _idle[_count] = 0;
                    _notReadyAtAcquire++;
                }
                _depth[_count] = item.HostQueueDepth;
                if (stamp.ReadyAtQueue) _readyAtQueue++;
                _count++;
            }

            if (acquired - _windowStart >= Stopwatch.Frequency * 10)
            {
                Log();
                _windowStart = acquired;
                _count = _readyAtQueue = _notReadyAtAcquire = _unstamped = 0;
            }
        }

        private static void Log()
        {
            if (_count == 0) return;
            string P(double[] values)
            {
                double[] sorted = values.AsSpan(0, _count).ToArray();
                Array.Sort(sorted);
                double At(double p) => sorted[Math.Min(_count - 1, (int)(p * _count))];
                return string.Create(CultureInfo.InvariantCulture, $"{At(0.5):F2}/{At(0.9):F2}/{At(0.99):F2}/{sorted[^1]:F2}");
            }
            Logger.Info?.PrintMsg(LogClass.SurfaceFlinger, string.Create(CultureInfo.InvariantCulture,
                $"[FencePhase] frames={_count};readyAtQueue={_readyAtQueue};notReadyAtAcquire={_notReadyAtAcquire};" +
                $"unstamped={_unstamped};residenceMs(p50/p90/p99/max)={P(_residence)};gpuMs={P(_gpu)};idleMs={P(_idle)};" +
                $"depth={P(_depth)}"));
        }
    }
}
