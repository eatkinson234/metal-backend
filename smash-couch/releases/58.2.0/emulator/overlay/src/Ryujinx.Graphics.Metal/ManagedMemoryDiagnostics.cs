using Ryujinx.Common.Logging;
using System;
using System.Globalization;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    // Managed heap figures for long sessions. The launcher caps the managed
    // heap (DOTNET_GCHeapHardLimit) separately from the sampled process
    // footprint, and a managed OutOfMemoryException ends the game, so the
    // footprint alone cannot show how close a session is to that cap. Logged
    // with the periodic summaries: what the last GC saw, per generation, plus
    // the allocator's running totals. Diagnostics only; never throws.
    static class ManagedMemoryDiagnostics
    {
        private const long MiB = 1024 * 1024;
        private static readonly string[] _generationNames = ["gen0", "gen1", "gen2", "loh", "poh"];

        internal static string Summary()
        {
            GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.Any);
            StringBuilder text = new("ManagedMemory: ");
            text.Append("heapMiB=").Append(info.HeapSizeBytes / MiB);
            text.Append(";fragmentedMiB=").Append(info.FragmentedBytes / MiB);
            text.Append(";committedMiB=").Append(info.TotalCommittedBytes / MiB);
            text.Append(";limitMiB=").Append(info.TotalAvailableMemoryBytes / MiB);
            text.Append(";loadMiB=").Append(info.MemoryLoadBytes / MiB);
            text.Append(";highLoadMiB=").Append(info.HighMemoryLoadThresholdBytes / MiB);
            text.Append(";promotedMiB=").Append(info.PromotedBytes / MiB);
            text.Append(";pinned=").Append(info.PinnedObjectsCount);
            ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
            for (int index = 0; index < generations.Length && index < _generationNames.Length; index++)
            {
                text.Append(';').Append(_generationNames[index]).Append("MiB=").Append(generations[index].SizeAfterBytes / MiB);
            }
            text.Append(";liveMiB=").Append(GC.GetTotalMemory(false) / MiB);
            text.Append(";allocatedMiB=").Append(GC.GetTotalAllocatedBytes(false) / MiB);
            text.Append(";gcs=").Append(GC.CollectionCount(0)).Append('/').Append(GC.CollectionCount(1)).Append('/').Append(GC.CollectionCount(2));
            text.Append(";lastGc=").Append(info.Index).Append(":gen").Append(info.Generation).Append(info.Compacted ? ":compacted" : ":swept");
            // Pause time: the running total since start (per-interval deltas come from consecutive lines), the last
            // collection's pauses, and the last full blocking collection, which is the one that can stall a frame.
            text.Append(";gcPauseMs=").Append((long)GC.GetTotalPauseDuration().TotalMilliseconds);
            text.Append(";lastPauseMs=").Append(PauseMilliseconds(info).ToString("F1", CultureInfo.InvariantCulture));
            GCMemoryInfo blocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            text.Append(";lastBlockingGc=").Append(blocking.Index).Append(':')
                .Append(PauseMilliseconds(blocking).ToString("F1", CultureInfo.InvariantCulture)).Append("ms");
            return text.ToString();
        }

        private static double PauseMilliseconds(GCMemoryInfo info)
        {
            double total = 0;
            foreach (TimeSpan pause in info.PauseDurations)
            {
                total += pause.TotalMilliseconds;
            }

            return total;
        }

        internal static void Log(string reason)
        {
            try
            {
                Logger.Info?.Print(LogClass.Gpu, $"{Summary()};reason={reason}");
            }
            catch (Exception)
            {
                // A diagnostics failure must not reach the caller.
            }
        }
    }
}
