using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Device;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Gpu.Synchronization
{
    /// <summary>
    /// GPU synchronization manager.
    /// </summary>
    public class SynchronizationManager : ISynchronizationManager
    {
        /// <summary>
        /// The maximum number of syncpoints supported by the GM20B.
        /// </summary>
        public const int MaxHardwareSyncpoints = 192;

        /// <summary>
        /// Array containing all hardware syncpoints.
        /// </summary>
        private readonly Syncpoint[] _syncpoints;

        public SynchronizationManager()
        {
            _syncpoints = new Syncpoint[MaxHardwareSyncpoints];

            for (uint i = 0; i < _syncpoints.Length; i++)
            {
                _syncpoints[i] = new Syncpoint(i);
            }
        }

        /// <inheritdoc/>
        public uint IncrementSyncpoint(uint id)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].Increment();
        }

        /// <inheritdoc/>
        public uint GetSyncpointValue(uint id)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].Value;
        }

        /// <summary>
        /// Register a new callback on a syncpoint with a given id at a target threshold.
        /// The callback will be called once the threshold is reached and will automatically be unregistered.
        /// </summary>
        /// <param name="id">The id of the syncpoint</param>
        /// <param name="threshold">The target threshold</param>
        /// <param name="callback">The callback to call when the threshold is reached</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when id >= MaxHardwareSyncpoints</exception>
        /// <returns>The created SyncpointWaiterHandle object or null if already past threshold</returns>
        public SyncpointWaiterHandle RegisterCallbackOnSyncpoint(uint id, uint threshold, Action<SyncpointWaiterHandle> callback)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            return _syncpoints[id].RegisterCallback(threshold, callback);
        }

        /// <summary>
        /// Unregister a callback on a given syncpoint.
        /// </summary>
        /// <param name="id">The id of the syncpoint</param>
        /// <param name="waiterInformation">The waiter information to unregister</param>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when id >= MaxHardwareSyncpoints</exception>
        public void UnregisterCallback(uint id, SyncpointWaiterHandle waiterInformation)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            _syncpoints[id].UnregisterCallback(waiterInformation);
        }

        /// <inheritdoc/>
        public bool WaitOnSyncpoint(uint id, uint threshold, TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);

            // TODO: Remove this when GPU channel scheduling will be implemented.
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                timeout = TimeSpan.FromSeconds(1);
            }

            // A claimed callback can outlive unregister. Its completion object
            // must remain safe after this wait returns; do not capture a disposed handle.
            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SyncpointWaiterHandle info = _syncpoints[id].RegisterCallback(threshold, _ => completion.TrySetResult(true));

            if (info == null)
            {
                return false;
            }

            bool signaled;
            try
            {
                signaled = completion.Task.Wait(timeout);
            }
            finally
            {
                _syncpoints[id].UnregisterCallback(info);
            }

            if (!signaled)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Wait on syncpoint {id} for threshold {threshold} took more than {timeout.TotalMilliseconds}ms, resuming execution...");

            }

            return !signaled;
        }

        /// <summary>
        /// Wait for the exact CPU fallback fence without reinterpreting the
        /// legacy GPU scheduling timeout. The caller must hold no signal/cancel locks.
        /// </summary>
        public CpuSyncpointWaitOutcome WaitForCpuSyncpoint(uint id, uint threshold, Func<bool> cancellationRequested)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, (uint)MaxHardwareSyncpoints);
            ArgumentNullException.ThrowIfNull(cancellationRequested);

            if (cancellationRequested())
            {
                return CpuSyncpointWaitOutcome.Cancelled;
            }

            TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            SyncpointWaiterHandle info = _syncpoints[id].RegisterCpuCallback(threshold, _ => completion.TrySetResult(true));
            Stopwatch elapsed = Stopwatch.StartNew();
            bool slowReported = false;
            try
            {
                while (true)
                {
                    // Cancellation wins when observed alongside completion.
                    if (cancellationRequested())
                    {
                        return CpuSyncpointWaitOutcome.Cancelled;
                    }

                    if (completion.Task.IsCompleted)
                    {
                        return CpuSyncpointWaitOutcome.Completed;
                    }

                    if (!slowReported && elapsed.ElapsedMilliseconds >= 1000)
                    {
                        slowReported = true;
                        Logger.Warning?.Print(LogClass.Gpu, $"CPU fallback awaiting syncpoint {id} threshold {threshold}; GPU completion pending for {elapsed.ElapsedMilliseconds}ms.");
                    }

                    completion.Task.Wait(25);
                }
            }
            finally
            {
                if (info != null)
                {
                    _syncpoints[id].UnregisterCallback(info);
                }
            }
        }
    }
}
