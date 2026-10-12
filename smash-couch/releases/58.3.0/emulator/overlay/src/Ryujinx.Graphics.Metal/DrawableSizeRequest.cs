using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Hands a drawable size from the UI thread (resize, full screen, display change) to the render thread, which
    /// applies it at the next present. Width and height travel in one 64-bit word, so the render thread can never
    /// take the width of one request with the height of another, and a request is never half consumed.
    /// </summary>
    sealed class DrawableSizeRequest
    {
        private long _pending;

        public void Request(int width, int height)
        {
            Interlocked.Exchange(ref _pending, Pack(width, height));
        }

        /// <summary>Takes the latest request, if any; a request with a zero or negative side is dropped.</summary>
        public bool TryTake(out int width, out int height)
        {
            long packed = Interlocked.Exchange(ref _pending, 0);

            width = (int)(packed >> 32);
            height = (int)(uint)packed;

            return width > 0 && height > 0;
        }

        internal static long Pack(int width, int height) => ((long)width << 32) | (uint)height;
    }
}
