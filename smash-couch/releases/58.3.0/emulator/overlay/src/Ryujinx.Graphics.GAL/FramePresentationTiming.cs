using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.GAL;

/// <summary>
/// Run-local timing carried with one guest presentation frame. Tick values use
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> units and share the
/// same process-local Stopwatch clock domain.
/// GuestQueuedTicks is when the guest queued the buffer (BufferQueueProducer.QueueBuffer),
/// ComposerAcquiredTicks when the composer tick acquired it, and QueueDepthAtAcquire how many
/// buffers were queued at that acquire, including this one. Zero when not recorded.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
public readonly record struct FramePresentationTiming(
    long Sequence,
    long EnqueuedTicks,
    long DequeuedTicks,
    long QueueWaitBeginTicks,
    long QueueWaitEndTicks,
    long GuestQueuedTicks = 0,
    long ComposerAcquiredTicks = 0,
    long QueueDepthAtAcquire = 0)
{
    /// <summary>
    /// Whether passive frame-phase propagation is enabled for this process.
    /// Read once from the exact opt-in environment value during type initialization.
    /// </summary>
    public static bool IsEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("SMASH_COUCH_FRAME_PHASE"), "1", StringComparison.Ordinal);
}
