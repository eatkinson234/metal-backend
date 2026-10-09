using Ryujinx.HLE.HOS.Kernel.Threading;
using System;

namespace Ryujinx.HLE.Exceptions
{
    internal enum HleServiceCancellationReason
    {
        ServiceStopping,
        BufferQueueClosed,
    }

    // Lifecycle cancellation belongs only to this exact, actually terminating service thread.
    internal sealed class HleServiceThreadTerminatingException : Exception
    {
        internal KThread ServiceThread { get; }
        internal HleServiceCancellationReason Reason { get; }

        internal HleServiceThreadTerminatingException(KThread thread, HleServiceCancellationReason reason)
            : base($"Service thread {thread.ThreadUid} is terminating ({reason}).")
        {
            ServiceThread = thread;
            Reason = reason;
        }

        internal static void ThrowIfRequested(KThread thread,
            HleServiceCancellationReason reason = HleServiceCancellationReason.ServiceStopping)
        {
            if (thread?.TerminationRequested == true)
            {
                throw new HleServiceThreadTerminatingException(thread, reason);
            }
        }
    }
}
