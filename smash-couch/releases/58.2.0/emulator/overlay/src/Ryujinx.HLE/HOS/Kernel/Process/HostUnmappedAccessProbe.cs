using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.Memory;
using System;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Process
{
    /// <summary>
    /// Rig-only check (SMASH_COUCH_PROBE_HOST_UNMAPPED=1) of how host-thread accesses to unmapped guest memory finish
    /// (KProcess.InvalidAccessHandler, then HvMemoryManager reading or writing page by page). Thirty seconds after the
    /// application starts, a host thread reads across both edges of the first code mapping and outside the address
    /// space, writes to the page below the process address space (which the kernel never maps), and logs the outcome.
    /// </summary>
    static class HostUnmappedAccessProbe
    {
        private const ulong PageSize = KPageTableBase.PageSize;

        private static int _started;

        public static void StartIfRequested(KProcess process)
        {
            if (Environment.GetEnvironmentVariable("SMASH_COUCH_PROBE_HOST_UNMAPPED") != "1" || Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            new Thread(() => Run(process)) { Name = "HostUnmappedAccessProbe", IsBackground = true }.Start();
        }

        private static void Run(KProcess process)
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));

            if (process.State is ProcessState.Crashed or ProcessState.Exiting or ProcessState.Exited)
            {
                return;
            }

            try
            {
                IVirtualMemoryManager memory = process.CpuMemory;
                KPageTableBase table = process.MemoryManager;

                ulong first = FindEdge(memory, table.CodeRegionStart, table.CodeRegionEnd, rising: true);
                ulong last = first == 0 ? 0 : FindEdge(memory, first, table.CodeRegionEnd, rising: false);

                string rising = "no-edge", falling = "no-edge", writes = "n/a";

                if (first != 0)
                {
                    byte[] page = memory.GetSpan(first, (int)PageSize).ToArray();
                    ReadOnlySpan<byte> read = memory.GetSpan(first - PageSize, (int)(2 * PageSize));

                    rising = IsZero(read[..(int)PageSize]) && read[(int)PageSize..].SequenceEqual(page) ? "ok" : "MISMATCH";
                }

                // Writes go below the process address space, which the kernel never maps, so they cannot reach game
                // memory. Both must be dropped without an exception.
                ulong never = table.AddrSpaceStart - PageSize;

                if (table.AddrSpaceStart >= PageSize && !memory.IsMapped(never))
                {
                    memory.Write(never, new byte[16]);
                    ((IWritableBlock)memory).WriteUntracked(never, new byte[16]);

                    writes = memory.IsMapped(never) ? "MAPPED" : "ok";
                }

                if (last != 0)
                {
                    byte[] page = memory.GetSpan(last, (int)PageSize).ToArray();
                    ReadOnlySpan<byte> read = memory.GetSpan(last, (int)(2 * PageSize));

                    falling = read[..(int)PageSize].SequenceEqual(page) && IsZero(read[(int)PageSize..]) ? "ok" : "MISMATCH";
                }

                string outside = IsZero(memory.GetSpan(1UL << 48, (int)PageSize)) ? "ok" : "MISMATCH";

                Logger.Info?.Print(LogClass.Cpu, $"HostUnmappedAccessProbe: rising={rising} falling={falling} outside={outside} writes={writes} first=0x{first:X} last=0x{last:X} written=0x{never:X}");
            }
            catch (Exception e)
            {
                Logger.Error?.Print(LogClass.Cpu, $"HostUnmappedAccessProbe: FAILED {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>
        /// Rising: the first mapped page whose previous page is unmapped. Falling: the first mapped page whose next page
        /// is unmapped. Returns 0 when the region has none.
        /// </summary>
        private static ulong FindEdge(IVirtualMemoryManager memory, ulong start, ulong end, bool rising)
        {
            for (ulong va = start & ~(PageSize - 1); va < end; va += PageSize)
            {
                if (memory.IsMapped(va) && !memory.IsMapped(rising ? va - PageSize : va + PageSize))
                {
                    return va;
                }
            }

            return 0;
        }

        private static bool IsZero(ReadOnlySpan<byte> data)
        {
            return data.IndexOfAnyExcept((byte)0) < 0;
        }
    }
}
