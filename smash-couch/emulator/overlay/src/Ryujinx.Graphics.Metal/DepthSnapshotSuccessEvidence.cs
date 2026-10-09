using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Metal.State;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // Opt-in, one-shot, private success marker. It proves only that an admitted
    // native draw command was encoded after the copy and state rebind, not that
    // its GPU execution completed or that game presentation was correct.
    internal static class DepthSnapshotSuccessEvidence
    {
        private const string Variable = "SMASH_COUCH_DEPTH_SNAPSHOT_EVIDENCE_DIR";
        private static int _claimed;
        internal static bool Pending => Volatile.Read(ref _claimed) == 0 &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

        internal static void TryWrite(string context, RenderFeedbackGuard.Identity source,
            RenderFeedbackGuard.Identity snapshot, RenderFeedbackGuard.Identity attachment,
            IntPtr commandBuffer, DepthStencilUid requested, DepthStencilUid effective,
            EncoderType encoder, Program program, RenderFeedbackGuard.BindingEvidence binding,
            bool readonlyStencil148, int frontReference, int backReference)
        {
            string configured = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrWhiteSpace(configured) ||
                Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return;

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
                    throw new IOException("Snapshot evidence requires the actual private -r data path.");
                string data = Path.GetFullPath(arguments[dataSwitch + 1]);
                if (!directory.StartsWith(prefix, StringComparison.Ordinal) ||
                    !data.StartsWith(prefix, StringComparison.Ordinal) ||
                    !directory.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    !Directory.Exists(directory) ||
                    !Directory.Exists(lab) || !Directory.Exists(data))
                    throw new IOException("Snapshot evidence must be a preexisting private Lab child directory.");

                string current = directory;
                while (current != null)
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Snapshot evidence ancestry contains a symbolic link.");
                    current = Path.GetDirectoryName(current);
                }
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new IOException("Snapshot evidence directory must be fresh and empty.");

                Dictionary<string, object> record = new()
                {
                    ["schema"] = "smash-couch-native-depth-snapshot-first-encoded-v1",
                    ["capturedUtc"] = DateTimeOffset.UtcNow,
                    ["scope"] = "first admitted native Draw command encoded; GPU completion and image correctness unproven",
                    ["context"] = context,
                    ["admittedCase"] = readonlyStencil148 ? "readonly-stencil148" : "existing-less-equal-stencil-disabled",
                    ["program"] = DepthSnapshotAdmissionEvidence.ProgramRecord(program),
                    ["binding"] = new Dictionary<string, object>
                    {
                        ["set"] = binding.Set,
                        ["segmentBinding"] = binding.Segment.Binding,
                        ["segmentCount"] = binding.Segment.Count,
                        ["segmentIsArray"] = binding.Segment.IsArray,
                        ["resourceType"] = binding.Segment.Type.ToString(),
                        ["stages"] = binding.Segment.Stages.ToString(),
                        ["actualBinding"] = binding.ActualBinding,
                        ["groupedOffset"] = binding.GroupedOffset,
                    },
                    ["nativeIdentityShapeCopyChecksCompleted"] = true,
                    ["frontReference"] = frontReference,
                    ["backReference"] = backReference,
                    ["frontStencil"] = FaceRecord(requested.FrontFace),
                    ["backStencil"] = FaceRecord(requested.BackFace),
                    ["encoderAfterEncoding"] = encoder.ToString(),
                    ["commandBuffer"] = $"0x{commandBuffer.ToInt64():X}",
                    ["sourceView"] = $"0x{source.View.ToInt64():X}",
                    ["sourceRoot"] = $"0x{source.Root.ToInt64():X}",
                    ["sourceLevel"] = source.Level,
                    ["sourceSlice"] = source.Slice,
                    ["snapshotView"] = $"0x{snapshot.View.ToInt64():X}",
                    ["snapshotRoot"] = $"0x{snapshot.Root.ToInt64():X}",
                    ["snapshotGpuResourceId"] = snapshot.GpuId,
                    ["attachmentView"] = $"0x{attachment.View.ToInt64():X}",
                    ["attachmentRoot"] = $"0x{attachment.Root.ToInt64():X}",
                    ["requestedDepthCompare"] = requested.DepthCompareFunction.ToString(),
                    ["effectiveDepthCompare"] = effective.DepthCompareFunction.ToString(),
                    ["requestedDepthWriteEnabled"] = requested.DepthWriteEnabled,
                    ["effectiveDepthWriteEnabled"] = effective.DepthWriteEnabled,
                    ["requestedStencilTestEnabled"] = requested.StencilTestEnabled,
                    ["effectiveStencilTestEnabled"] = effective.StencilTestEnabled,
                    ["copyOrder"] = "same tracked command buffer: prior render Store, full D32S8 blit, blit End, render Load, Draw encoded",
                };
                using FileStream file = new(Path.Combine(directory, "first-depth-snapshot-encoded.json"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, record);
                file.Flush(flushToDisk: true);
            }
            catch (Exception failure)
            {
                // Diagnostic errors cannot change the draw's rendering result
                // or replace an exception from the actual render path.
                try
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu,
                        $"[CouchNativeDepthSnapshot] first-encoded evidence failed: {failure.GetType().Name}: {failure.Message}");
                }
                catch { }
            }
        }

        private static Dictionary<string, object> FaceRecord(StencilUid face) => new()
        {
            ["compare"] = face.StencilCompareFunction.ToString(),
            ["readMask"] = face.ReadMask,
            ["writeMask"] = face.WriteMask,
            ["stencilFailureOperation"] = face.StencilFailureOperation.ToString(),
            ["depthFailureOperation"] = face.DepthFailureOperation.ToString(),
            ["depthStencilPassOperation"] = face.DepthStencilPassOperation.ToString(),
        };
    }
}
