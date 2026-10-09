using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;

namespace Ryujinx.Graphics.Metal
{
    internal readonly record struct DepthStencilSlice(int Layer, int Level, int Width, int Height, int Offset, int RowBytes, int Bytes);

    internal static class DepthStencilTransferLayout
    {
        // Offsets refer to interleaved D32FS8 host staging (8 bytes per pixel),
        // even when the guest uses one of the packed 4-byte D24 formats.
        internal static List<DepthStencilSlice> Create(TextureCreateInfo info, int layer, int level,
            int layers, int levels, int availableBytes, int offset, int stride = 0)
        {
            if (info.Target == Target.Texture3D)
                throw new NotSupportedException("Metal combined depth/stencil transfers do not support 3D textures.");
            if (layer < 0 || level < 0 || layers <= 0 || levels <= 0 ||
                (long)layer + layers > info.GetLayers() || (long)level + levels > info.Levels ||
                availableBytes < 0 || offset < 0 || stride < 0)
                throw new ArgumentOutOfRangeException(nameof(layer), "Invalid depth/stencil transfer range.");
            int scale = info.BytesPerPixel == 4 ? 2 : info.BytesPerPixel == 8 ? 1 :
                throw new NotSupportedException("Expected a packed D24 or D32FS8 guest texture.");
            var result = new List<DepthStencilSlice>();
            int used = 0;
            checked
            {
                for (int mip = level; mip < level + levels; mip++)
                {
                    int width = Math.Max(1, info.Width >> mip);
                    int height = Math.Max(1, info.Height >> mip);
                    int rowBytes = (stride == 0 ? info.GetMipStride(mip) : stride) * scale;
                    if (rowBytes < width * 8 || rowBytes % 8 != 0)
                        throw new ArgumentException("Packed depth/stencil row stride does not cover whole texels.", nameof(stride));
                    int bytes = rowBytes * height;
                    for (int arrayLayer = layer; arrayLayer < layer + layers; arrayLayer++)
                    {
                        if (bytes > availableBytes - used)
                            throw new ArgumentException("Depth/stencil staging is too small for the requested subresources.", nameof(availableBytes));
                        result.Add(new DepthStencilSlice(arrayLayer, mip, width, height, offset + used, rowBytes, bytes));
                        used += bytes;
                    }
                }
            }
            return result;
        }
    }
}
