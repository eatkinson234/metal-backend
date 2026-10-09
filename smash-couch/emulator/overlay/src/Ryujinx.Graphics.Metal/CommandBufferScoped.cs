using SharpMetal.Metal;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    readonly struct CommandBufferScoped : IDisposable
    {
        private readonly CommandBufferPool _pool;
        public MTLCommandBuffer CommandBuffer { get; }
        public CommandBufferEncoder Encoders { get; }
        public int CommandBufferIndex { get; }
        internal ulong DiagnosticInstance { get; }

        public CommandBufferScoped(CommandBufferPool pool, MTLCommandBuffer commandBuffer, CommandBufferEncoder encoders, int commandBufferIndex, ulong diagnosticInstance = 0)
        {
            _pool = pool;
            CommandBuffer = commandBuffer;
            Encoders = encoders;
            CommandBufferIndex = commandBufferIndex;
            DiagnosticInstance = diagnosticInstance;
        }

        internal PresentBufferRegistration ObservePresent(FramePhaseRecorder recorder, nint drawable, long sequence) =>
            _pool.ObservePresent(this, recorder, drawable, sequence);

        public void AddDependant(IAuto dependant)
        {
            _pool.AddDependant(CommandBufferIndex, dependant);
        }

        public void AddWaitable(MultiFenceHolder waitable)
        {
            _pool.AddWaitable(CommandBufferIndex, waitable);
        }

        public FenceHolder GetFence()
        {
            return _pool.GetFence(CommandBufferIndex);
        }

        public void Dispose()
        {
            _pool?.Return(this);
        }
    }
}
