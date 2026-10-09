using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // One-shot evidence for a recovered feedback guard stop only. Preparation
    // stores a small in-memory reason per draw; this class performs no I/O until
    // a bounded Main-state depth alias is rejected by the guard. This is optional
    // evidence only; it cannot change rendering admission or suppress the guard.
    internal static class DepthSnapshotAdmissionEvidence
    {
        private const string Variable = "SMASH_COUCH_DEPTH_SNAPSHOT_ADMISSION_EVIDENCE_DIR";
        private static int _claimed;

        internal static void TryCapture(string context, Program program,
            string firstRejectedPredicate, RenderFeedbackGuard.FeedbackGuardException failure)
        {
            string configured = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(configured) || program == null ||
                failure?.Evidence is not RenderFeedbackGuard.FailureEvidence evidence ||
                !IsRecoveredDepthAlias(evidence) ||
                Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
                return;

            try
            {
                string directory = Path.GetFullPath(configured);
                string lab = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Smash Couch", "Lab"));
                string prefix = lab + Path.DirectorySeparatorChar;
                string[] arguments = Environment.GetCommandLineArgs();
                int dataSwitch = Array.IndexOf(arguments, "-r");
                if (dataSwitch < 0 || dataSwitch + 1 >= arguments.Length)
                    throw new IOException("Admission evidence requires the actual private -r data path.");
                string data = Path.GetFullPath(arguments[dataSwitch + 1]);
                if (!directory.StartsWith(prefix, StringComparison.Ordinal) ||
                    !data.StartsWith(prefix, StringComparison.Ordinal) ||
                    !directory.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !Directory.Exists(directory) || !Directory.Exists(lab) || !Directory.Exists(data))
                    throw new IOException("Admission evidence must be a preexisting private Lab descendant.");

                string current = directory;
                while (current != null)
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Admission evidence ancestry contains a symbolic link.");
                    current = Path.GetDirectoryName(current);
                }
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new IOException("Admission evidence directory must be fresh and empty.");

                Texture sampled = evidence.SampledTexture as Texture;
                Texture attachment = evidence.Attachment.Texture;
                if (sampled == null || attachment == null)
                    throw new IOException("Recovered feedback evidence did not contain Metal textures.");

                Dictionary<string, object> record = new()
                {
                    ["schema"] = "smash-couch-native-depth-snapshot-admission-rejection-v1",
                    ["capturedUtc"] = DateTimeOffset.UtcNow,
                    ["scope"] = "first bounded Main-state depth feedback guard rejection; no draw or policy bypass",
                    ["program"] = ProgramRecord(program),
                    ["context"] = context,
                    ["firstRejectedPredicate"] = firstRejectedPredicate ?? "unknown",
                    ["guardMessage"] = failure.Message,
                    ["relation"] = evidence.Relation.ToString(),
                    ["binding"] = new Dictionary<string, object>
                    {
                        ["set"] = evidence.Binding.Set,
                        ["segmentBinding"] = evidence.Binding.Segment.Binding,
                        ["segmentCount"] = evidence.Binding.Segment.Count,
                        ["segmentIsArray"] = evidence.Binding.Segment.IsArray,
                        ["resourceType"] = evidence.Binding.Segment.Type.ToString(),
                        ["stages"] = evidence.Binding.Segment.Stages.ToString(),
                        ["actualBinding"] = evidence.Binding.ActualBinding,
                        ["groupedOffset"] = evidence.Binding.GroupedOffset,
                    },
                    ["sampledTexture"] = new Dictionary<string, object>
                    {
                        ["info"] = TextureInfoRecord(sampled.Info),
                        ["firstLevel"] = sampled.FirstLevel,
                        ["firstLayer"] = sampled.FirstLayer,
                        ["feedbackIdentity"] = IdentityRecord(evidence.ShaderIdentity),
                        ["samplingHandle"] = NativeTextureRecord(sampled.GetHandle()),
                        ["identityHandle"] = NativeTextureRecord(sampled.GetRenderAttachmentHandle()),
                    },
                    ["attachmentTexture"] = new Dictionary<string, object>
                    {
                        ["info"] = TextureInfoRecord(attachment.Info),
                        ["firstLevel"] = attachment.FirstLevel,
                        ["firstLayer"] = attachment.FirstLayer,
                        ["feedbackIdentity"] = IdentityRecord(evidence.Attachment.Identity),
                        ["samplingHandle"] = NativeTextureRecord(attachment.GetHandle()),
                        ["identityHandle"] = NativeTextureRecord(attachment.GetRenderAttachmentHandle()),
                    },
                };

                using FileStream file = new(Path.Combine(directory, "depth-snapshot-admission-rejection.json"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, record, new JsonSerializerOptions { WriteIndented = true });
                file.Flush(flushToDisk: true);
            }
            catch
            {
                // Diagnostic failures cannot replace the conservative guard stop.
            }
        }

        private static bool IsRecoveredDepthAlias(RenderFeedbackGuard.FailureEvidence evidence) =>
            evidence.Relation == RenderFeedbackGuard.Relation.PotentialAlias &&
            evidence.Binding.Set == 2 && evidence.Binding.Segment.Binding == 128 &&
            evidence.Binding.Segment.Count is (2 or 3) && !evidence.Binding.Segment.IsArray &&
            evidence.Binding.Segment.Type == Ryujinx.Graphics.GAL.ResourceType.TextureAndSampler &&
            evidence.Binding.Segment.Stages == Ryujinx.Graphics.GAL.ResourceStages.Fragment &&
            ((evidence.Binding.ActualBinding == 128 + evidence.Binding.Segment.Count - 1 &&
              evidence.Binding.GroupedOffset == evidence.Binding.Segment.Count - 1) ||
             ReadonlyStencil148SnapshotPolicy.MatchesBinding(evidence.Binding)) &&
            evidence.Attachment.Texture != null &&
            evidence.Attachment.Role.StartsWith("depth/stencil:", StringComparison.Ordinal);

        internal static Dictionary<string, object> ProgramRecord(Program program)
        {
            DepthSnapshotResourceCardinalities counts = program.DepthSnapshotResourceCounts;
            ReadonlyStencil148ProgramWitness witness = program.ReadonlyStencil148Witness;
            return new()
            {
                ["actualDepthSnapshotShaderAllowed"] = program.DepthSnapshotShaderAllowed,
                ["resourceSetCount"] = counts.Sets,
                ["resourceSetCardinalities"] = new[] { counts.Uniforms, counts.Storage, counts.Textures, counts.Images },
                ["stencil148OptIn"] = ReadonlyStencil148SnapshotPolicy.Enabled,
                ["stencil148WitnessAllowed"] = witness.Allowed,
                ["publicReadOnlyFamily"] = witness.PublicReadOnlyFamily,
                ["actualFamilyAllowed"] = witness.ActualFamilyAllowed,
                ["exactReviewedSourcePair"] = witness.ExactReviewedPair,
                ["exactOrderedResources"] = witness.ExactOrderedResources,
                ["reviewedTransport"] = witness.ReviewedTransport,
            };
        }

        private static Dictionary<string, object> TextureInfoRecord(Ryujinx.Graphics.GAL.TextureCreateInfo info) => new()
        {
            ["width"] = info.Width,
            ["height"] = info.Height,
            ["depth"] = info.Depth,
            ["levels"] = info.Levels,
            ["samples"] = info.Samples,
            ["blockWidth"] = info.BlockWidth,
            ["blockHeight"] = info.BlockHeight,
            ["bytesPerPixel"] = info.BytesPerPixel,
            ["format"] = info.Format.ToString(),
            ["depthStencilMode"] = info.DepthStencilMode.ToString(),
            ["target"] = info.Target.ToString(),
            ["swizzleR"] = info.SwizzleR.ToString(),
            ["swizzleG"] = info.SwizzleG.ToString(),
            ["swizzleB"] = info.SwizzleB.ToString(),
            ["swizzleA"] = info.SwizzleA.ToString(),
        };

        private static Dictionary<string, object> IdentityRecord(RenderFeedbackGuard.Identity identity) => new()
        {
            ["known"] = identity.Known,
            ["fault"] = identity.Fault,
            ["view"] = Pointer(identity.View),
            ["root"] = Pointer(identity.Root),
            ["buffer"] = Pointer(identity.Buffer),
            ["gpuResourceId"] = identity.GpuId,
            ["level"] = identity.Level,
            ["slice"] = identity.Slice,
            ["levels"] = identity.Levels,
            ["layers"] = identity.Layers,
            ["format"] = identity.Format.ToString(),
            ["type"] = identity.Type.ToString(),
        };

        private static Dictionary<string, object> NativeTextureRecord(MTLTexture texture)
        {
            Dictionary<string, object> record = new()
            {
                ["handle"] = Pointer(texture.NativePtr),
                ["queried"] = false,
            };
            if (texture.NativePtr == IntPtr.Zero) return record;

            try
            {
                record["queried"] = true;
                record["pixelFormat"] = texture.PixelFormat.ToString();
                record["textureType"] = texture.TextureType.ToString();
                record["width"] = texture.Width;
                record["height"] = texture.Height;
                record["depth"] = texture.Depth;
                record["sampleCount"] = texture.SampleCount;
                record["mipmapLevelCount"] = texture.MipmapLevelCount;
                record["arrayLength"] = texture.ArrayLength;
                record["storageMode"] = texture.StorageMode.ToString();
                record["hazardTrackingMode"] = texture.HazardTrackingMode.ToString();
            }
            catch (Exception exception)
            {
                record["queryFailure"] = $"{exception.GetType().Name}: {exception.Message}";
            }
            return record;
        }

        private static string Pointer(IntPtr value) => $"0x{(ulong)value:X}";
    }
}
