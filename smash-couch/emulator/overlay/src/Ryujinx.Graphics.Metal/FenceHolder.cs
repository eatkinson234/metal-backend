using SharpMetal.Metal;
using SharpMetal.Foundation;
using SharpMetal.ObjectiveCCore;
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    class FenceHolder : IDisposable
    {
        private MTLCommandBuffer _fence;
        private int _referenceCount;
        private bool _disposed;

        public FenceHolder(MTLCommandBuffer fence)
        {
            if (fence.NativePtr == IntPtr.Zero)
            {
                throw new ArgumentException("Cannot own a null command buffer.", nameof(fence));
            }
            // The queue factory returns an autoreleased buffer. One native retain
            // follows the entire managed fence lifetime, including external waits.
            ObjectiveC.IntPtr_objc_msgSend(fence.NativePtr, "retain");
            _fence = fence;
            _referenceCount = 1;
        }

        public MTLCommandBuffer GetUnsafe()
        {
            return _fence;
        }

        public bool TryGet(out MTLCommandBuffer fence)
        {
            int lastValue;
            do
            {
                lastValue = _referenceCount;

                if (lastValue == 0)
                {
                    fence = default;
                    return false;
                }
            } while (Interlocked.CompareExchange(ref _referenceCount, lastValue + 1, lastValue) != lastValue);

            fence = _fence;
            return true;
        }

        public MTLCommandBuffer Get()
        {
            Interlocked.Increment(ref _referenceCount);
            return _fence;
        }

        public void Put()
        {
            if (Interlocked.Decrement(ref _referenceCount) == 0)
            {
                using NSAutoreleasePool pool = new();
                _fence.Dispose();
                _fence = default;
            }
        }

        public void Wait()
        {
            using var timing = NativeTimingDiagnostics.Measure(NativeTimingKind.FenceWait);
            _fence.WaitUntilCompleted();
        }

        public bool IsSignaled()
        {
            return _fence.Status == MTLCommandBufferStatus.Completed;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Put();
                _disposed = true;
            }
        }
    }
}
