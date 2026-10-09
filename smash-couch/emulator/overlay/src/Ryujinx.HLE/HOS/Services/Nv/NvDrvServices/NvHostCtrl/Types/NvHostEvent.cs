using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Nv.Types;
using Ryujinx.Horizon.Common;
using System;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostCtrl
{
    class NvHostEvent
    {
        public NvFence Fence;
        public NvHostEventState State;
        public KEvent Event;
        public int EventHandle;

        private readonly uint _eventId;
#pragma warning disable IDE0052 // Remove unread private member
        private readonly NvHostSyncpt _syncpointManager;
#pragma warning restore IDE0052
        private SyncpointWaiterHandle _waiterInformation;

        private NvFence _previousFailingFence;
        private uint _failingCount;
        private ulong _generation;
        private NvCpuWaitReservation _cpuWait;
        private bool _closed;

        public readonly Lock Lock = new();

        /// <summary>
        /// Max failing count until waiting on CPU.
        /// FIXME: This seems enough for most of the cases, reduce if needed.
        /// </summary>
        private const uint FailingCountMax = 2;

        public NvHostEvent(NvHostSyncpt syncpointManager, uint eventId, Horizon system)
        {
            Fence.Id = 0;

            State = NvHostEventState.Available;

            Event = new KEvent(system.KernelContext);

            if (KernelStatic.GetCurrentProcess().HandleTable.GenerateHandle(Event.ReadableEvent, out EventHandle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            _eventId = eventId;

            _syncpointManager = syncpointManager;

            ResetFailingState();
        }

        private void ResetFailingState()
        {
            _previousFailingFence.Id = NvFence.InvalidSyncPointId;
            _previousFailingFence.Value = 0;
            _failingCount = 0;
        }

        private void Signal()
        {
            lock (Lock)
            {
                NvHostEventState oldState = State;

                State = NvHostEventState.Signaling;

                if (oldState == NvHostEventState.Waiting)
                {
                    Event.WritableEvent.Signal();
                }

                State = NvHostEventState.Signaled;
            }
        }

        private void GpuSignaled(SyncpointWaiterHandle waiterInformation, ulong generation)
        {
            lock (Lock)
            {
                // If the signal does not match our current waiter,
                // then it is from a past fence and we should just ignore it.
                if (_closed || generation != _generation || _cpuWait != null ||
                    (waiterInformation != null && waiterInformation != _waiterInformation))
                {
                    return;
                }

                ResetFailingState();

                Signal();
            }
        }

        public void Cancel(GpuContext gpuContext)
        {
            lock (Lock)
            {
                NvHostEventState oldState = State;

                _generation++;
                _cpuWait?.Cancel();
                _cpuWait = null;

                State = NvHostEventState.Cancelling;

                if (oldState == NvHostEventState.Waiting && _waiterInformation != null)
                {
                    gpuContext.Synchronization.UnregisterCallback(Fence.Id, _waiterInformation);
                    _waiterInformation = null;

                    if (_previousFailingFence.Id == Fence.Id && _previousFailingFence.Value == Fence.Value)
                    {
                        _failingCount++;
                    }
                    else
                    {
                        _failingCount = 1;

                        _previousFailingFence = Fence;
                    }
                }

                State = NvHostEventState.Cancelled;

                if (!_closed)
                {
                    Event.WritableEvent.Clear();
                }
            }
        }

        public NvHostWaitPreparation PrepareWait(GpuContext gpuContext, NvFence fence, out NvCpuWaitReservation cpuWait)
        {
            lock (Lock)
            {
                if (_closed)
                {
                    throw new InvalidOperationException("Cannot wait on a closed nvhost event.");
                }

                cpuWait = null;
                _generation++;
                ulong generation = _generation;
                Fence = fence;
                State = NvHostEventState.Waiting;

                // Reserve the exact requested fence here. The caller releases
                // both its event-table lock and this event lock before blocking.
                if (_failingCount == FailingCountMax)
                {
                    Logger.Warning?.Print(LogClass.ServiceNv, "GPU processing thread is too slow, waiting on CPU...");
                    _waiterInformation = null;
                    cpuWait = new NvCpuWaitReservation(fence, generation);
                    _cpuWait = cpuWait;
                    return NvHostWaitPreparation.CpuFallback;
                }
                else
                {
                    _waiterInformation = gpuContext.Synchronization.RegisterCallbackOnSyncpoint(Fence.Id, Fence.Value,
                        waiter => GpuSignaled(waiter, generation));
                    return NvHostWaitPreparation.Queued;
                }
            }
        }

        public CpuSyncpointWaitOutcome FinishCpuWait(NvCpuWaitReservation reservation, CpuSyncpointWaitOutcome outcome)
        {
            lock (Lock)
            {
                if (_closed || !ReferenceEquals(_cpuWait, reservation) ||
                    _generation != reservation.Generation || State != NvHostEventState.Waiting ||
                    Fence.Id != reservation.Fence.Id || Fence.Value != reservation.Fence.Value)
                {
                    return CpuSyncpointWaitOutcome.Cancelled;
                }

                _cpuWait = null;
                if (reservation.IsCancelled || outcome != CpuSyncpointWaitOutcome.Completed)
                {
                    reservation.Cancel();
                    State = NvHostEventState.Cancelled;
                    Event.WritableEvent.Clear();
                    return outcome == CpuSyncpointWaitOutcome.Error ? outcome : CpuSyncpointWaitOutcome.Cancelled;
                }

                ResetFailingState();
                State = NvHostEventState.Signaled;
                return CpuSyncpointWaitOutcome.Completed;
            }
        }

        public string DumpState(GpuContext gpuContext)
        {
            string res = $"\nNvHostEvent {_eventId}:\n";
            res += $"\tState: {State}\n";

            if (State == NvHostEventState.Waiting)
            {
                res += "\tFence:\n";
                res += $"\t\tId            : {Fence.Id}\n";
                res += $"\t\tThreshold     : {Fence.Value}\n";
                res += $"\t\tCurrent Value : {gpuContext.Synchronization.GetSyncpointValue(Fence.Id)}\n";
                res += $"\t\tWaiter Valid  : {_waiterInformation != null}\n";
            }

            return res;
        }

        public void CloseEvent(ServiceCtx context)
        {
            lock (Lock)
            {
                _closed = true;
                _generation++;
                _cpuWait?.Cancel();
                _cpuWait = null;
                _waiterInformation = null;
                State = NvHostEventState.Cancelled;
                if (EventHandle != 0)
                {
                    context.Process.HandleTable.CloseHandle(EventHandle);
                    EventHandle = 0;
                }
            }
        }
    }
}
