using Avalonia.Threading;
using Ryujinx.Common.Helper;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal;
using SharpMetal.QuartzCore;
using System;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowMetal : EmbeddedWindow
    {
        private IDisposable _refreshSampler;

        public CAMetalLayer CreateSurface()
        {
            if (OperatingSystem.IsMacOS() && RunningPlatform.IsArm)
            {
                return new CAMetalLayer(MetalLayer);
            }
            
            throw new NotSupportedException($"Cannot create a {nameof(CAMetalLayer)} without being on ARM Mac.");
        }

        public override void OnWindowCreated()
        {
            base.OnWindowCreated();

            // Refresh-anchored presents need the refresh timing of the screen showing the game; AppKit
            // is read here on the UI thread, four times a second (screen moves, mode changes).
            if (OperatingSystem.IsMacOS() && !DisplayPacing.Legacy && _refreshSampler == null)
            {
                _refreshSampler = DispatcherTimer.Run(() =>
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        DisplayRefreshSampler.SampleOnMainThread(NsView);
                    }

                    return true;
                }, TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal);
            }
        }

        protected override void OnWindowDestroying()
        {
            _refreshSampler?.Dispose();
            _refreshSampler = null;

            base.OnWindowDestroying();
        }
    }
}
