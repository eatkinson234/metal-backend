using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Metal;

// New opt-in is independent of phase alignment. The existing frame-phase recorder
// must also be enabled. No native ownership API is loaded/used by the OFF path.
internal static class PresentBufferDiagnostic
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("SMASH_COUCH_PRESENT_BUFFER_DIAGNOSTIC") == "1";
    private static long _nextInstance; // process-scoped across every pool/renderer
    internal static ulong NewInstance()
    {
        for (;;)
        {
            long previous = Volatile.Read(ref _nextInstance);
            if (previous == long.MaxValue) return 0; // permanent explicit identity refusal, never wrap/reuse
            if (Interlocked.CompareExchange(ref _nextInstance, previous + 1, previous) == previous)
                return (ulong)(previous + 1);
        }
    }
}

internal readonly record struct PresentBufferRegistration(FramePhaseRecorder Recorder, ulong Instance,
    ulong Sequence, ulong DrawableId, ulong BufferPointer, int PoolIndex, int RegisterStatus, int PresentedRegisterStatus)
{
    internal bool Admitted => Recorder != null;
    internal long BeginCommit() => Admitted ? Stopwatch.GetTimestamp() : 0;
    internal void EndCommit(long before, bool returned) => Recorder?.CommitPresentBuffer(this, before, Stopwatch.GetTimestamp(), returned);
}
