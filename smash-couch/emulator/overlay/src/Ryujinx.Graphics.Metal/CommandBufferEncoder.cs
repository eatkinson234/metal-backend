using Ryujinx.Graphics.Metal;
using SharpMetal.Metal;
using SharpMetal.Foundation;
using SharpMetal.ObjectiveCCore;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

interface IEncoderFactory
{
    MTLRenderCommandEncoder CreateRenderCommandEncoder();
    MTLComputeCommandEncoder CreateComputeCommandEncoder();
}

/// <summary>
/// Tracks active encoder object for a command buffer.
/// </summary>
[SupportedOSPlatform("macos")]
class CommandBufferEncoder
{
    public EncoderType CurrentEncoderType { get; private set; } = EncoderType.None;

    public MTLBlitCommandEncoder BlitEncoder => new(CurrentEncoder.Value);

    public MTLComputeCommandEncoder ComputeEncoder => new(CurrentEncoder.Value);

    public MTLRenderCommandEncoder RenderEncoder => new(CurrentEncoder.Value);

    internal MTLCommandEncoder? CurrentEncoder { get; private set; }

    private MTLCommandBuffer _commandBuffer;
    private IEncoderFactory _encoderFactory;

    public void Initialize(MTLCommandBuffer commandBuffer, IEncoderFactory encoderFactory)
    {
        _commandBuffer = commandBuffer;
        _encoderFactory = encoderFactory;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLRenderCommandEncoder EnsureRenderEncoder()
    {
        if (CurrentEncoderType != EncoderType.Render)
        {
            return BeginRenderPass();
        }

        return RenderEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLBlitCommandEncoder EnsureBlitEncoder()
    {
        if (CurrentEncoderType != EncoderType.Blit)
        {
            return BeginBlitPass();
        }

        return BlitEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MTLComputeCommandEncoder EnsureComputeEncoder()
    {
        if (CurrentEncoderType != EncoderType.Compute)
        {
            return BeginComputePass();
        }

        return ComputeEncoder;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRenderEncoder(out MTLRenderCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Render)
        {
            encoder = default;
            return false;
        }

        encoder = RenderEncoder;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetBlitEncoder(out MTLBlitCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Blit)
        {
            encoder = default;
            return false;
        }

        encoder = BlitEncoder;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComputeEncoder(out MTLComputeCommandEncoder encoder)
    {
        if (CurrentEncoderType != EncoderType.Compute)
        {
            encoder = default;
            return false;
        }

        encoder = ComputeEncoder;
        return true;
    }

    public void EndCurrentPass()
    {
        if (CurrentEncoder != null)
        {
            using NSAutoreleasePool pool = new();
            MTLCommandEncoder ownedEncoder = CurrentEncoder.Value;
            try
            {
                switch (CurrentEncoderType)
                {
                    case EncoderType.Blit: BlitEncoder.EndEncoding(); break;
                    case EncoderType.Compute: ComputeEncoder.EndEncoding(); break;
                    case EncoderType.Render: RenderEncoder.EndEncoding(); break;
                    default: throw new InvalidOperationException();
                }
            }
            finally
            {
                CurrentEncoder = null;
                CurrentEncoderType = EncoderType.None;
                // Balances the explicit retain at creation, never a borrowed copy.
                ownedEncoder.Dispose();
            }
        }
    }

    private static void RetainEncoder(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Metal did not create an encoder.");
        ObjectiveC.IntPtr_objc_msgSend(pointer, "retain");
    }

    private MTLRenderCommandEncoder BeginRenderPass()
    {
        using NSAutoreleasePool pool = new();
        EndCurrentPass();

        MTLRenderCommandEncoder renderCommandEncoder = _encoderFactory.CreateRenderCommandEncoder();

        RetainEncoder(renderCommandEncoder.NativePtr);
        CurrentEncoder = renderCommandEncoder;
        CurrentEncoderType = EncoderType.Render;

        return renderCommandEncoder;
    }

    private MTLBlitCommandEncoder BeginBlitPass()
    {
        using NSAutoreleasePool pool = new();
        EndCurrentPass();

        using MTLBlitPassDescriptor descriptor = new();
        MTLBlitCommandEncoder blitCommandEncoder = _commandBuffer.BlitCommandEncoder(descriptor);

        RetainEncoder(blitCommandEncoder.NativePtr);
        CurrentEncoder = blitCommandEncoder;
        CurrentEncoderType = EncoderType.Blit;
        return blitCommandEncoder;
    }

    private MTLComputeCommandEncoder BeginComputePass()
    {
        using NSAutoreleasePool pool = new();
        EndCurrentPass();

        MTLComputeCommandEncoder computeCommandEncoder = _encoderFactory.CreateComputeCommandEncoder();

        RetainEncoder(computeCommandEncoder.NativePtr);
        CurrentEncoder = computeCommandEncoder;
        CurrentEncoderType = EncoderType.Compute;
        return computeCommandEncoder;
    }
}
