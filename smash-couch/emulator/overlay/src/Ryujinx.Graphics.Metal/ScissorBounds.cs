using SharpMetal.Metal;
using System;

namespace Ryujinx.Graphics.Metal
{
    internal static class ScissorBounds
    {
        // EncoderState stores the GAL's signed int rectangles in Metal's
        // unsigned fields. Recover signed values before intersecting; casting a
        // negative origin directly to Metal would instead produce a huge origin.
        internal static MTLScissorRect Clip(MTLScissorRect rectangle, ulong width, ulong height)
        {
            long x = unchecked((long)rectangle.x);
            long y = unchecked((long)rectangle.y);
            long w = unchecked((long)rectangle.width);
            long h = unchecked((long)rectangle.height);
            if (w <= 0 || h <= 0 || width == 0 || height == 0)
                return default;

            long boundX = checked((long)width);
            long boundY = checked((long)height);
            long left = Math.Clamp(x, 0, boundX);
            long top = Math.Clamp(y, 0, boundY);
            // Saturation also makes this helper safe for large unsigned-native
            // rectangles, though ordinary guest coordinates are signed int32.
            long right = Math.Clamp(x > long.MaxValue - w ? long.MaxValue : x + w, 0, boundX);
            long bottom = Math.Clamp(y > long.MaxValue - h ? long.MaxValue : y + h, 0, boundY);
            if (right <= left || bottom <= top)
                return default;
            return new MTLScissorRect
            {
                x = (ulong)left,
                y = (ulong)top,
                width = (ulong)(right - left),
                height = (ulong)(bottom - top),
            };
        }
    }
}
