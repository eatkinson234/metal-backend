using Ryujinx.HLE.HOS.Services.SurfaceFlinger.Types;
using System;

namespace Ryujinx.HLE.HOS.Services.SurfaceFlinger
{
    class BufferItem : ICloneable
    {
        public AndroidStrongPointer<GraphicBuffer> GraphicBuffer;
        public AndroidFence Fence;
        public Rect Crop;
        public NativeWindowTransform Transform;
        public NativeWindowScalingMode ScalingMode;
        public long Timestamp;
        public bool IsAutoTimestamp;
        public int SwapInterval;
        public ulong FrameNumber;
        public int Slot;
        public bool IsDroppable;
        public bool AcquireCalled;
        public bool TransformToDisplayInverse;

        // Host-side diagnostics (frame-phase only): Stopwatch time of QueueBuffer and the queue depth seen at acquire.
        public long HostQueuedTicks;
        public int HostQueueDepth;
        // FencePhase diagnostic only (SMASH_COUCH_FENCE_PHASE=1).
        public FencePhaseStamp FencePhase;

        public BufferItem()
        {
            GraphicBuffer = new AndroidStrongPointer<GraphicBuffer>();
            Transform = NativeWindowTransform.None;
            ScalingMode = NativeWindowScalingMode.Freeze;
            Timestamp = 0;
            IsAutoTimestamp = false;
            FrameNumber = 0;
            Slot = BufferSlotArray.InvalidBufferSlot;
            IsDroppable = false;
            AcquireCalled = false;
            TransformToDisplayInverse = false;
            SwapInterval = 1;
            Fence = AndroidFence.NoFence;

            Crop = new Rect();
            Crop.MakeInvalid();
        }

        public object Clone()
        {
            BufferItem item = new()
            {
                Transform = Transform,
                ScalingMode = ScalingMode,
                IsAutoTimestamp = IsAutoTimestamp,
                FrameNumber = FrameNumber,
                Slot = Slot,
                IsDroppable = IsDroppable,
                AcquireCalled = AcquireCalled,
                TransformToDisplayInverse = TransformToDisplayInverse,
                SwapInterval = SwapInterval,
                Fence = Fence,
                Crop = Crop,
                HostQueuedTicks = HostQueuedTicks,
                HostQueueDepth = HostQueueDepth,
                FencePhase = FencePhase,
            };

            item.GraphicBuffer.Set(GraphicBuffer);

            return item;
        }
    }
}
