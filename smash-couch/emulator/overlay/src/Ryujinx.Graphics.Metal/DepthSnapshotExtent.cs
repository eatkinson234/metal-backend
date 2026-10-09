using SharpMetal.Metal;
using System;

namespace Ryujinx.Graphics.Metal
{
    // The snapshot may use any actual positive guest extent up to the existing
    // conservative bound. All byte counts are checked before this value can be
    // used for native allocation or a depth/stencil blit.
    internal readonly struct DepthSnapshotExtent
    {
        private const MTLStorageMode MemorylessStorage = (MTLStorageMode)3;
        private const MTLHazardTrackingMode TrackedHazards = (MTLHazardTrackingMode)2;
        internal const int MaximumWidth = 1920;
        internal const int MaximumHeight = 1080;
        internal const int NativeDepthStencilBytesPerPixel = 8;

        internal int Width { get; }
        internal int Height { get; }
        internal long PixelCount { get; }
        internal long GuestBytes { get; }
        internal long NativeBytes { get; }

        private DepthSnapshotExtent(int width, int height, long pixelCount,
            long guestBytes, long nativeBytes)
        {
            Width = width;
            Height = height;
            PixelCount = pixelCount;
            GuestBytes = guestBytes;
            NativeBytes = nativeBytes;
        }

        internal static bool TryCreate(int width, int height, int guestBytesPerPixel,
            out DepthSnapshotExtent extent)
        {
            extent = default;
            if (width <= 0 || height <= 0 || guestBytesPerPixel <= 0)
                return false;

            long pixels;
            long guestBytes;
            long nativeBytes;
            try
            {
                pixels = checked((long)width * height);
                guestBytes = checked(pixels * guestBytesPerPixel);
                nativeBytes = checked(pixels * NativeDepthStencilBytesPerPixel);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (width > MaximumWidth || height > MaximumHeight)
                return false;

            extent = new(width, height, pixels, guestBytes, nativeBytes);
            return true;
        }

        internal bool MatchesNativeExtent(ulong width, ulong height) =>
            width == (ulong)Width && height == (ulong)Height;

        internal bool MatchesNativeDepthTexture(MTLTexture texture, MTLPixelFormat pixelFormat) =>
            texture.NativePtr != IntPtr.Zero && texture.PixelFormat == pixelFormat &&
            texture.TextureType == MTLTextureType.Type2D &&
            MatchesNativeExtent(texture.Width, texture.Height) && texture.Depth == 1 &&
            texture.SampleCount == 1 && texture.MipmapLevelCount == 1 && texture.ArrayLength == 1 &&
            texture.StorageMode != MemorylessStorage && texture.HazardTrackingMode == TrackedHazards;
    }
}
