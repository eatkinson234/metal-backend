using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Graphics.GAL;

/// <summary>
/// Run-local timing carried with one guest presentation frame. Tick values use
/// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> units and share the
/// same process-local Stopwatch clock domain.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 40)]
public readonly record struct FramePresentationTiming(
    long Sequence,
    long EnqueuedTicks,
    long DequeuedTicks,
    long QueueWaitBeginTicks,
    long QueueWaitEndTicks)
{
    /// <summary>
    /// Whether passive frame-phase propagation is enabled for this process.
    /// Read once from the exact opt-in environment value during type initialization.
    /// </summary>
    public static bool IsEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("SMASH_COUCH_FRAME_PHASE"), "1", StringComparison.Ordinal);
}
