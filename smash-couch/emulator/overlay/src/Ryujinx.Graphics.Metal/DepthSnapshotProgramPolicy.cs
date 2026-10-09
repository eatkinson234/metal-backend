using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;

namespace Ryujinx.Graphics.Metal
{
    // Active resource usages drive the Metal argument-buffer layout. Uniform
    // buffer positions do not determine whether a sampled depth texture aliases
    // an attachment. Admit bounded, scalar, read-only uniform layouts and the
    // vertex and fragment scalar texture capacities. Runtime attachment identity,
    // copy and full feedback checks remain mandatory. Do not use ResourceUsage.Equals:
    // its upstream implementation does not compare the Write property.
    internal static class DepthSnapshotProgramPolicy
    {
        internal static bool Allows(ShaderSource[] shaders, ResourceLayout layout)
        {
            if (shaders == null || shaders.Length != 2 ||
                shaders[0].Stage != ShaderStage.Vertex || shaders[1].Stage != ShaderStage.Fragment ||
                shaders[0].Language != TargetLanguage.Msl || shaders[1].Language != TargetLanguage.Msl ||
                string.IsNullOrWhiteSpace(shaders[0].Code) || string.IsNullOrWhiteSpace(shaders[1].Code) ||
                layout.SetUsages == null || layout.SetUsages.Count != 4)
                return false;

            for (int set = 0; set < 4; set++)
                if (layout.SetUsages[set].Usages == null) return false;
            if (layout.SetUsages[0].Usages.Count is < 1 or > Constants.MaxUniformBufferBindings ||
                layout.SetUsages[1].Usages.Count != 0 ||
                layout.SetUsages[2].Usages.Count is < 1 or > 2 * Constants.MaxTexturesPerStage ||
                layout.SetUsages[3].Usages.Count != 0)
                return false;

            int previousBinding = -1;
            foreach (ResourceUsage usage in layout.SetUsages[0].Usages)
            {
                if (usage.Binding <= previousBinding || usage.Binding >= Constants.MaxUniformBufferBindings ||
                    usage.ArrayLength != 1 || usage.Type != ResourceType.UniformBuffer || usage.Write ||
                    ((usage.Stages & ResourceStages.Compute) != 0 && usage.Binding != 0) ||
                    !AllowsUniformStages(usage.Stages)) return false;
                previousBinding = usage.Binding;
            }
            int vertexCount = 0;
            int fragmentCount = 0;
            bool sawFragment = false;
            foreach (ResourceUsage usage in layout.SetUsages[2].Usages)
            {
                if (usage.Stages == ResourceStages.Vertex && !sawFragment)
                {
                    if (vertexCount >= Constants.MaxTexturesPerStage ||
                        !Matches(usage, vertexCount++, ResourceType.TextureAndSampler, ResourceStages.Vertex))
                        return false;
                }
                else if (usage.Stages == ResourceStages.Fragment)
                {
                    sawFragment = true;
                    if (fragmentCount >= Constants.MaxTexturesPerStage ||
                        !Matches(usage, 128 + fragmentCount++, ResourceType.TextureAndSampler, ResourceStages.Fragment))
                        return false;
                }
                else return false;
            }
            return true;
        }

        internal static bool AllowsUniformStages(ResourceStages stages)
        {
            const ResourceStages graphics = ResourceStages.Vertex | ResourceStages.Fragment;
            const ResourceStages allowed = graphics | ResourceStages.Compute;
            // The shared support UBO includes Compute in its stage mask even for
            // a vertex/fragment program. A compute-only or unsupported stage is
            // outside this contract.
            return (stages & graphics) != 0 && (stages & ~allowed) == 0;
        }

        private static bool Matches(ResourceUsage usage, int binding,
            ResourceType type, ResourceStages stages) =>
            usage.Binding == binding && usage.ArrayLength == 1 &&
            usage.Type == type && usage.Stages == stages && !usage.Write;
    }
}
