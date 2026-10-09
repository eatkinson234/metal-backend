using Ryujinx.Common.Configuration;
using System;

namespace Ryujinx.Graphics.GAL
{
    public interface IWindow
    {
        void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback);

        void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback, FramePresentationTiming timing)
        {
            Present(texture, crop, swapBuffersCallback);
        }

        void SetSize(int width, int height);

        void ChangeVSyncMode(VSyncMode vSyncMode);

        void SetAntiAliasing(AntiAliasing antialiasing);
        void SetScalingFilter(ScalingFilter type);
        void SetScalingFilterLevel(float level);
        void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled);
    }
}
