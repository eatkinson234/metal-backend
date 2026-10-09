using Ryujinx.HLE.HOS.Services.Nv.Types;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl
{
    enum NvHostWaitPreparation
    {
        Queued,
        CpuFallback,
    }

    sealed class NvCpuWaitReservation
    {
        public readonly NvFence Fence;
        public readonly ulong Generation;
        private int _cancelled;

        public NvCpuWaitReservation(NvFence fence, ulong generation)
        {
            Fence = fence;
            Generation = generation;
        }

        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
        public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
    }
}
