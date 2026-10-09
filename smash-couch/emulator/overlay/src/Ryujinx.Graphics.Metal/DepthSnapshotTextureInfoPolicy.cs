using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.Metal
{
    // Keep the guest attachment allocation shape exact while allowing only the
    // sampled-view swizzles used by the existing fixture and Metal depth pool.
    internal static class DepthSnapshotTextureInfoPolicy
    {
        internal static bool SameAllocationShape(TextureCreateInfo attachment, TextureCreateInfo sampled)
        {
            return attachment.Width == sampled.Width &&
                   attachment.Height == sampled.Height &&
                   attachment.Depth == sampled.Depth &&
                   attachment.Levels == sampled.Levels &&
                   attachment.Samples == sampled.Samples &&
                   attachment.BlockWidth == sampled.BlockWidth &&
                   attachment.BlockHeight == sampled.BlockHeight &&
                   attachment.BytesPerPixel == sampled.BytesPerPixel &&
                   attachment.Format == sampled.Format &&
                   attachment.DepthStencilMode == sampled.DepthStencilMode &&
                   attachment.Target == sampled.Target;
        }

        internal static bool HasIdentitySwizzle(TextureCreateInfo info) =>
            info.SwizzleR == SwizzleComponent.Red &&
            info.SwizzleG == SwizzleComponent.Green &&
            info.SwizzleB == SwizzleComponent.Blue &&
            info.SwizzleA == SwizzleComponent.Alpha;

        internal static bool HasCanonicalDepthSwizzle(TextureCreateInfo info) =>
            info.DepthStencilMode == DepthStencilMode.Depth &&
            info.SwizzleR == SwizzleComponent.Red &&
            info.SwizzleG == SwizzleComponent.Red &&
            info.SwizzleB == SwizzleComponent.Red &&
            info.SwizzleA == SwizzleComponent.One;

        internal static bool HasAdmittedSampleSwizzle(TextureCreateInfo info) =>
            HasIdentitySwizzle(info) || HasCanonicalDepthSwizzle(info);
    }
}
