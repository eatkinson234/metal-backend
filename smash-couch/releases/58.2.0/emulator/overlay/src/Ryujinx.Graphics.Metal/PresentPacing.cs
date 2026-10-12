using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    // Present pacing for variable-refresh displays. Guest frames reach the host with about
    // +/-1.5 ms of alternating jitter; presented as soon as ready, a display with 4.167 ms
    // timing granularity shows them for 12.5/20.8 ms alternately. Policies, only while VSync is Switch:
    //  - Schedule (default 10 ms; SMASH_COUCH_PRESENT_SCHEDULE_LEAD_MS=7..20 overrides, 0 disables):
    //    present each frame at an exact time, the previous target plus 1/60 s, normally that lead
    //    after commit. Measured overnight: 8 and 12 ms both gave 100% 16.667 ms cadence; 10 keeps
    //    4 ms of jitter margin above the resync threshold. A frame too late for
    //    its slot resynchronizes at the lead (one long interval, no latency ratchet); a lead that
    //    has grown is shed one 4.167 ms step per frame.
    //  - SMASH_COUCH_PRESENT_MIN_MS (1..33, only when the schedule is disabled):
    //    presentDrawable:afterMinimumDuration:.
    // SharpMetal's wrappers pass the CFTimeInterval in an integer register, so the selectors are
    // sent directly with the double in d0. Stopwatch and CACurrentMediaTime share mach time.
    [SupportedOSPlatform("macos")]
    static partial class PresentPacing
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const double Period = 1.0 / 60.0;
        private const double Step = 1.0 / 240.0;
        private const double MinimumLead = 0.006;

        private const string DefaultScheduleLeadMs = "10";

        public static readonly double ScheduleLead = Parse(Environment.GetEnvironmentVariable("SMASH_COUCH_PRESENT_SCHEDULE_LEAD_MS") ?? DefaultScheduleLeadMs, 7, 20);
        public static readonly double MinimumSeconds = ScheduleLead > 0 ? 0 : Parse(Environment.GetEnvironmentVariable("SMASH_COUCH_PRESENT_MIN_MS"), 1, 33);

        public static volatile bool VSyncSwitch;

        private static readonly IntPtr _atTime = ScheduleLead > 0 ? sel_registerName("presentDrawable:atTime:") : IntPtr.Zero;
        private static readonly IntPtr _afterMinimumDuration = MinimumSeconds > 0 ? sel_registerName("presentDrawable:afterMinimumDuration:") : IntPtr.Zero;

        // Render-thread only.
        private static double _lastTarget;
        public static long Resyncs, Sheds, Scheduled;

        [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string name);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial void SendDrawableDouble(IntPtr commandBuffer, IntPtr selector, IntPtr drawable, double seconds);

        public static bool Active => (ScheduleLead > 0 || MinimumSeconds > 0) && VSyncSwitch;

        private static double Parse(string value, double lowMs, double highMs)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms >= lowMs && ms <= highMs
                ? ms / 1000.0
                : 0;
        }

        public static void Present(IntPtr commandBuffer, IntPtr drawable)
        {
            if (ScheduleLead <= 0)
            {
                SendDrawableDouble(commandBuffer, _afterMinimumDuration, drawable, MinimumSeconds);
                return;
            }

            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            double target = _lastTarget + Period;
            if (_lastTarget == 0 || target < now + MinimumLead)
            {
                target = now + ScheduleLead;
                Resyncs++;
            }
            else if (target > now + ScheduleLead + Step * 1.5)
            {
                target = Math.Max(now + ScheduleLead, target - Step);
                Sheds++;
            }

            _lastTarget = target;
            Scheduled++;
            SendDrawableDouble(commandBuffer, _atTime, drawable, target);
        }

        public static void Reset() => _lastTarget = 0;
    }
}
