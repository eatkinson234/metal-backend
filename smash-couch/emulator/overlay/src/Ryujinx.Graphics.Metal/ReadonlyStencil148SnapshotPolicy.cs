using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.State;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using SharpMetal.Metal;
using System;
using System.IO;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    internal readonly record struct DepthSnapshotResourceCardinalities(
        int Sets, int Uniforms, int Storage, int Textures, int Images)
    {
        internal static DepthSnapshotResourceCardinalities Capture(ResourceLayout layout) => new(
            layout.SetUsages?.Count ?? -1,
            Count(layout, 0), Count(layout, 1), Count(layout, 2), Count(layout, 3));

        private static int Count(ResourceLayout layout, int set) =>
            layout.SetUsages != null && set < layout.SetUsages.Count
                ? layout.SetUsages[set].Usages?.Count ?? -1 : -1;
    }

    // Immutable construction result, never a shader classifier. Only the two
    // fully reviewed adapted source strings plus the exact active usages qualify
    // this failure-derived branch. The public resource-family gate alone cannot
    // establish absence of shader depth/stencil outputs or writable operations.
    internal readonly record struct ReadonlyStencil148ProgramWitness(
        bool PublicReadOnlyFamily, bool ActualFamilyAllowed, bool ExactReviewedPair,
        bool ExactOrderedResources, bool ReviewedTransport)
    {
        internal bool Allowed => PublicReadOnlyFamily && ActualFamilyAllowed &&
            ExactReviewedPair && ExactOrderedResources && ReviewedTransport;
    }

    internal static class ReadonlyStencil148SnapshotPolicy
    {
        internal const string Variable = "SMASH_COUCH_READONLY_STENCIL148_SNAPSHOT";
        // Fixed once per process. No live setting or per-draw environment lookup.
        internal static readonly bool Enabled = Environment.GetEnvironmentVariable(Variable) == "1";
        private static readonly Lazy<string> ReviewedVertex = new(() => ReadReviewedSource("ReviewedVertex.metal"));
        private static readonly Lazy<string> ReviewedFragment = new(() => ReadReviewedSource("ReviewedFragment.metal"));

        internal static ReadonlyStencil148ProgramWitness CreateWitness(
            ShaderSource[] shaders, ResourceLayout layout, bool publicReadOnlyFamily,
            bool actualFamilyAllowed, bool usesPerVertexTransport, bool usesPointTransport)
        {
            bool pair = shaders != null && shaders.Length == 2 &&
                shaders[0].Stage == ShaderStage.Vertex && shaders[1].Stage == ShaderStage.Fragment &&
                shaders[0].Language == TargetLanguage.Msl && shaders[1].Language == TargetLanguage.Msl &&
                string.Equals(shaders[0].Code, ReviewedVertex.Value, StringComparison.Ordinal) &&
                string.Equals(shaders[1].Code, ReviewedFragment.Value, StringComparison.Ordinal);
            return new(publicReadOnlyFamily, actualFamilyAllowed, pair,
                MatchesResources(layout), usesPerVertexTransport && !usesPointTransport);
        }

        private static string ReadReviewedSource(string file)
        {
            using Stream stream = typeof(Program).Assembly.GetManifestResourceStream(
                "Ryujinx.Graphics.Metal.Shaders.ReadonlyStencil148." + file)
                ?? throw new InvalidOperationException("Reviewed stencil148 shader resource missing.");
            using StreamReader reader = new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            return reader.ReadToEnd();
        }

        private static bool MatchesResources(ResourceLayout layout)
        {
            if (DepthSnapshotResourceCardinalities.Capture(layout) != new DepthSnapshotResourceCardinalities(4, 8, 0, 2, 0))
                return false;
            int[] bindings = [0, 4, 5, 20, 22, 23, 28, 29];
            for (int index = 0; index < bindings.Length; index++)
            {
                ResourceStages stages = index == 0
                    ? ResourceStages.Compute | ResourceStages.Vertex | ResourceStages.Fragment
                    : index < 3 ? ResourceStages.Vertex : ResourceStages.Fragment;
                if (!Matches(layout.SetUsages[0].Usages[index], bindings[index], ResourceType.UniformBuffer, stages))
                    return false;
            }
            return Matches(layout.SetUsages[2].Usages[0], 128, ResourceType.TextureAndSampler, ResourceStages.Fragment) &&
                Matches(layout.SetUsages[2].Usages[1], 129, ResourceType.TextureAndSampler, ResourceStages.Fragment);
        }

        private static bool Matches(ResourceUsage usage, int binding, ResourceType type, ResourceStages stages) =>
            usage.Binding == binding && usage.ArrayLength == 1 && usage.Type == type &&
            usage.Stages == stages && !usage.Write;

        internal static string RejectState(DepthStencilUid uid, int frontReference, int backReference)
        {
            if (uid.DepthCompareFunction != MTLCompareFunction.Less) return "stencil148-depth-compare-is-not-less";
            if (uid.DepthWriteEnabled) return "depth-write-enabled";
            if (!uid.StencilTestEnabled) return "stencil148-stencil-test-disabled";
            if (!MatchesFace(uid.FrontFace) || !MatchesFace(uid.BackFace)) return "stencil148-face-descriptor-not-reviewed";
            if (frontReference != 0x10 || backReference != 0x10) return "stencil148-reference-not-reviewed";
            return null;
        }

        private static bool MatchesFace(StencilUid face) =>
            face.StencilCompareFunction == MTLCompareFunction.NotEqual &&
            face.ReadMask == 0x10 && face.WriteMask == 0 &&
            face.StencilFailureOperation == MTLStencilOperation.Keep &&
            face.DepthFailureOperation == MTLStencilOperation.Keep &&
            face.DepthStencilPassOperation == MTLStencilOperation.Replace;

        internal static bool MatchesBinding(RenderFeedbackGuard.BindingEvidence binding) =>
            binding.Set == 2 && binding.Segment.Binding == 128 && binding.Segment.Count == 2 &&
            !binding.Segment.IsArray && binding.Segment.Type == ResourceType.TextureAndSampler &&
            binding.Segment.Stages == ResourceStages.Fragment && binding.ActualBinding == 128 &&
            binding.GroupedOffset == 0 && binding.ArrayIndex == null && binding.ArrayLength == null;

        // D24S8 is one four-byte guest texel, converted to an eight-byte native
        // D32FS8 texel. Equal malformed wrapper shapes do not establish that fact.
        internal static bool HasExactGuestTexelShape(TextureCreateInfo info) =>
            info.BlockWidth == 1 && info.BlockHeight == 1 && info.BytesPerPixel == 4;

        // E already rejects memoryless storage and untracked/unknown hazards.
        // THIS branch additionally rejects unknown storage enum values.
        internal static bool HasKnownStorage(MTLTexture texture) =>
            texture.StorageMode is MTLStorageMode.Shared or MTLStorageMode.Managed or MTLStorageMode.Private;
    }
}
