using Ryujinx.Graphics.Metal.State;
using SharpMetal.Metal;
using System;

namespace Ryujinx.Graphics.Metal
{
    internal static class DepthStencilAttachmentState
    {
        // The guest state persists across render-target changes. Apply attachment
        // constraints to a value copy so a later combined target restores it.
        public static DepthStencilUid Effective(DepthStencilUid requested, MTLPixelFormat format)
        {
            (bool depth, bool stencil) = format switch
            {
                MTLPixelFormat.Invalid => (false, false),
                MTLPixelFormat.Depth16Unorm or MTLPixelFormat.Depth32Float => (true, false),
                MTLPixelFormat.Stencil8 => (false, true),
                MTLPixelFormat.Depth24UnormStencil8 or MTLPixelFormat.Depth32FloatStencil8 => (true, true),
                _ => throw new NotSupportedException($"Unsupported depth/stencil attachment format: {format}"),
            };

            if (!stencil)
            {
                requested.StencilTestEnabled = false;
            }

            if (!depth)
            {
                requested.DepthCompareFunction = MTLCompareFunction.Always;
                requested.DepthWriteEnabled = false;
            }

            return requested;
        }
    }
}
