using SharpMetal.Metal;
using System;
using System.Buffers.Binary;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal;

/// <summary>Explicit, versioned descriptor bytes; no native handles or struct padding.</summary>
[SupportedOSPlatform("macos")]
internal static class PipelineRecipeCodec
{
    internal const string CompatibilityVersion = "native41-full-color-descriptors-v1";
    internal const int PayloadSize = 605;

    internal static byte[] Encode(in PipelineUid key)
    {
        byte[] bytes = new byte[PayloadSize];
        int offset = 0;
        void U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value); offset += 8; }
        U64(key.Id0); U64(key.Id1);
        for (int i = 0; i < 32; i++) U64(key.VertexAttributes[i].Id0);
        for (int i = 0; i < 33; i++) U64(key.VertexBindings[i].Id0);
        for (int i = 0; i < 8; i++) U64(key.ColorBlendState[i].Id0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), key.AttachmentIntegerFormatMask);
        bytes[offset + 4] = key.LogicOpsAllowed ? (byte)1 : (byte)0;
        return bytes;
    }

    internal static bool TryDecode(byte[] payload, out PipelineState state)
    {
        ReadOnlySpan<byte> bytes = payload;
        state = default;
        if (bytes.Length != PayloadSize || bytes[^1] > 1) return false;
        PipelineUid key = default;
        int offset = 0;
        key.Id0 = Read(bytes, ref offset); key.Id1 = Read(bytes, ref offset);
        for (int i = 0; i < 32; i++) key.VertexAttributes[i].Id0 = Read(bytes, ref offset);
        for (int i = 0; i < 33; i++) key.VertexBindings[i].Id0 = Read(bytes, ref offset);
        for (int i = 0; i < 8; i++) key.ColorBlendState[i].Id0 = Read(bytes, ref offset);
        key.AttachmentIntegerFormatMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        key.LogicOpsAllowed = bytes[^1] != 0;
        PipelineState decoded = new() { Internal = key };
        // Current renderer ABI has no tessellation and always seeds sample count
        // one. Unknown/reserved state must not be submitted to Metal by a cache.
        if ((key.Id0 & ((1UL << 39) | (15UL << 44))) != 0 ||
            decoded.StagesCount > Constants.MaxShaderStages ||
            decoded.VertexAttributeDescriptionsCount > Constants.MaxVertexAttributes ||
            decoded.VertexBindingDescriptionsCount > Constants.ZeroBufferIndex + 1 ||
            decoded.ColorBlendAttachmentStateCount > Constants.MaxColorAttachments ||
            decoded.SamplesCount != 1 || decoded.PatchControlPoints != 0 ||
            !Enum.IsDefined(decoded.LogicOp) || !DepthFormat(decoded.DepthStencilFormat)) return false;

        for (int i = 0; i < decoded.VertexAttributeDescriptionsCount; i++)
        {
            VertexInputAttributeUid a = key.VertexAttributes[i];
            if (!Enum.IsDefined(a.Format) || a.Offset > int.MaxValue ||
                a.BufferIndex > Constants.ZeroBufferIndex ||
                (a.Format != MTLVertexFormat.Invalid && a.BufferIndex >= decoded.VertexBindingDescriptionsCount)) return false;
        }
        for (int i = 0; i < decoded.VertexBindingDescriptionsCount; i++)
        {
            VertexInputLayoutUid b = key.VertexBindings[i];
            if (b.Stride > int.MaxValue) return false;
            if (b.StepFunction == MTLVertexStepFunction.Constant)
            {
                if (b.StepRate != 0) return false;
            }
            else if (b.StepFunction == MTLVertexStepFunction.PerVertex)
            {
                if (b.StepRate != 1 || b.Stride == 0) return false;
            }
            else if (b.StepFunction == MTLVertexStepFunction.PerInstance)
            {
                if (b.StepRate == 0 || b.Stride == 0) return false;
            }
            else return false;
        }
        for (int i = 0; i < Constants.MaxColorAttachments; i++)
        {
            ColorBlendStateUid c = key.ColorBlendState[i];
            if ((c.Id0 & (7UL << 60)) != 0 || !ColorFormat(c.PixelFormat) ||
                !Enum.IsDefined(c.SourceRGBBlendFactor) || !Enum.IsDefined(c.DestinationRGBBlendFactor) ||
                !Enum.IsDefined(c.SourceAlphaBlendFactor) || !Enum.IsDefined(c.DestinationAlphaBlendFactor) ||
                !Enum.IsDefined(c.RgbBlendOperation) || !Enum.IsDefined(c.AlphaBlendOperation)) return false;
        }
        state = decoded;
        return true;
    }

    private static ulong Read(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8)); offset += 8; return value;
    }

    private static bool DepthFormat(MTLPixelFormat format) => format is MTLPixelFormat.Invalid
        or MTLPixelFormat.Depth16Unorm or MTLPixelFormat.Depth32Float or MTLPixelFormat.Stencil8
        or MTLPixelFormat.Depth24UnormStencil8 or MTLPixelFormat.Depth32FloatStencil8;

    private static bool ColorFormat(MTLPixelFormat format)
    {
        if (!Enum.IsDefined(format)) return false;
        // Sample-only compressed/YUV/depth formats are not render-target recipes.
        // Named uncompressed color formats and Invalid (unbound) remain eligible.
        string name = format.ToString();
        return !name.StartsWith("Depth", StringComparison.Ordinal) &&
            !name.Contains("Stencil", StringComparison.Ordinal) &&
            !name.StartsWith("BC", StringComparison.Ordinal) &&
            !name.StartsWith("ASTC", StringComparison.Ordinal) &&
            !name.StartsWith("PVRTC", StringComparison.Ordinal) &&
            !name.StartsWith("EAC", StringComparison.Ordinal) &&
            !name.StartsWith("ETC", StringComparison.Ordinal) && !name.Contains("422", StringComparison.Ordinal);
    }
}
