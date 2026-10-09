using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Metal.State;
using Ryujinx.Graphics.Shader;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    internal static class NativeFeedbackDiagnostics
    {
        private const string EvidenceDirectoryVariable = "SMASH_COUCH_FEEDBACK_EVIDENCE_DIR";
        private const int MaximumShaderCodeBytes = 8 * 1024 * 1024;
        private static int _captureClaimed;

        internal static bool Enabled =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EvidenceDirectoryVariable));

        internal static void TryCapture(
            EncoderState state,
            ResourceBindingSegment[][] sets,
            string context,
            RenderFeedbackGuard.FeedbackGuardException guardException)
        {
            if (!Enabled || Interlocked.CompareExchange(ref _captureClaimed, 1, 0) != 0)
            {
                return;
            }

            try
            {
                Capture(state, sets, context, guardException);
            }
            catch (Exception diagnosticException)
            {
                // The original feedback guard exception remains authoritative.
                try
                {
                    string message =
                        $"[CouchNativeFeedback] Evidence capture failed without replacing guard stop: " +
                        $"{diagnosticException.GetType().Name}: {diagnosticException.Message}";
                    Logger.Error?.PrintMsg(LogClass.Gpu, message);
                    Console.Error.WriteLine(message);
                }
                catch
                {
                    // Logging failure also must not replace the guard stop.
                }
            }
        }

        private static void Capture(
            EncoderState state,
            ResourceBindingSegment[][] sets,
            string context,
            RenderFeedbackGuard.FeedbackGuardException guardException)
        {
            string configured = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
            string directory = Path.GetFullPath(configured);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("The feedback evidence directory must already exist.");
            }
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The feedback evidence directory must not be a symbolic link.");
            }
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                throw new IOException("The feedback evidence directory must be fresh and empty.");
            }

            Dictionary<string, object> programReceipt = WriteActiveProgram(directory, state.RenderProgram);
            RenderFeedbackGuard.FailureEvidence evidence = guardException.Evidence;

            MTLPixelFormat effectiveFormat = state.Pipeline.DepthStencilFormat;
            DepthStencilUid requested = state.DepthStencilUid;
            DepthStencilUid effective = DepthStencilAttachmentState.Effective(requested, effectiveFormat);

            Dictionary<string, object> record = new()
            {
                ["schema"] = "smash-couch-native-feedback-diagnostic-v1",
                ["diagnosticVersion"] = "1.3.3-metal.9",
                ["capturedUtc"] = DateTimeOffset.UtcNow,
                ["guardBehavior"] = "same conservative Relation comparison and stop-before-draw throw as .8",
                ["context"] = context,
                ["guardMessage"] = guardException.Message,
                ["failure"] = FailureRecord(evidence),
                ["depthStencil"] = new Dictionary<string, object>
                {
                    ["attachmentFormat"] = effectiveFormat.ToString(),
                    ["requested"] = DepthStencilRecord(requested, state.FrontRefValue, state.BackRefValue),
                    ["effective"] = DepthStencilRecord(effective, state.FrontRefValue, state.BackRefValue),
                },
                ["rasterState"] = new Dictionary<string, object>
                {
                    ["topology"] = state.Topology.ToString(),
                    ["cullMode"] = state.CullMode.ToString(),
                    ["cullBoth"] = state.CullBoth,
                    ["winding"] = state.Winding.ToString(),
                    ["rasterizerDiscardEnabled"] = state.Pipeline.RasterizerDiscardEnable,
                },
                ["renderPassPolicy"] = RenderPassPolicy(state),
                ["activeBindingSegments"] = SegmentRecords(sets),
                ["activeProgram"] = programReceipt,
                ["limits"] = new Dictionary<string, object>
                {
                    ["firstFailureOnly"] = true,
                    ["maximumTotalShaderCodeBytes"] = MaximumShaderCodeBytes,
                    ["nativeObjectsQueriedOnlyByGuard"] = true,
                    ["diagnosticsCanProveExecutedShaderBranch"] = false,
                },
            };

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions(indented: true));
            WriteNew(Path.Combine(directory, "feedback-failure.json"), bytes);
        }

        private static Dictionary<string, object> WriteActiveProgram(string directory, Program program)
        {
            if (program == null)
            {
                return new Dictionary<string, object>
                {
                    ["present"] = false,
                    ["reason"] = "No active render program was available.",
                };
            }

            List<Dictionary<string, object>> shaders = [];
            int remaining = MaximumShaderCodeBytes;
            Program.FeedbackShaderSource[] sources = program.FeedbackShaderSources ?? [];
            for (int index = 0; index < sources.Length; index++)
            {
                Program.FeedbackShaderSource source = sources[index];
                byte[] fullCode = Encoding.UTF8.GetBytes(source.Code ?? string.Empty);
                int writeCount = Math.Min(remaining, fullCode.Length);
                bool truncated = writeCount != fullCode.Length;
                string fileName = $"active-shader-{index:D2}-{source.Stage}.metal";
                WriteNew(Path.Combine(directory, fileName), fullCode.AsSpan(0, writeCount));
                remaining -= writeCount;
                shaders.Add(new Dictionary<string, object>
                {
                    ["stage"] = source.Stage.ToString(),
                    ["entryPoint"] = source.Stage switch
                    {
                        ShaderStage.Vertex => "vertexMain",
                        ShaderStage.Fragment => "fragmentMain",
                        ShaderStage.Compute => "kernelMain",
                        _ => "unsupported",
                    },
                    ["file"] = fileName,
                    ["originalUtf8Bytes"] = fullCode.Length,
                    ["writtenUtf8Bytes"] = writeCount,
                    ["truncated"] = truncated,
                    ["fullCodeSha256"] = Sha256(fullCode),
                });
            }

            List<Dictionary<string, object>> usages = [];
            foreach (Program.FeedbackResourceUsage usage in program.FeedbackResourceUsages ?? [])
            {
                usages.Add(new Dictionary<string, object>
                {
                    ["set"] = usage.Set,
                    ["binding"] = usage.Binding,
                    ["arrayLength"] = usage.ArrayLength,
                    ["type"] = usage.Type.ToString(),
                    ["stages"] = usage.Stages.ToString(),
                    ["write"] = usage.Write,
                });
            }

            Dictionary<string, object> manifest = new()
            {
                ["schema"] = "smash-couch-native-active-metal-program-v1",
                ["compileOptions"] = new Dictionary<string, object>
                {
                    ["preserveInvariance"] = Program.FeedbackPreserveInvariance,
                    ["languageVersion"] = program.ShaderLanguageVersion.ToString(),
                    ["usesPerVertexTransport"] = program.UsesPerVertexTransport,
                },
                ["shaderSources"] = shaders,
                ["resourceUsages"] = usages,
                ["bindingSegments"] = SegmentRecords(program.BindingSegments),
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions(indented: false));
            string manifestName = "active-program-manifest.json";
            WriteNew(Path.Combine(directory, manifestName), manifestBytes);

            return new Dictionary<string, object>
            {
                ["present"] = true,
                ["manifestFile"] = manifestName,
                ["manifestSha256"] = Sha256(manifestBytes),
                ["shaderCount"] = shaders.Count,
                ["retainedShaderSourceMode"] = "diagnostic opt-in; immutable Stage/Code references only",
                ["totalShaderCodeBytesWritten"] = MaximumShaderCodeBytes - remaining,
                ["anyShaderTruncated"] = shaders.Any(shader => (bool)shader["truncated"]),
            };
        }

        private static Dictionary<string, object> FailureRecord(RenderFeedbackGuard.FailureEvidence evidence)
        {
            if (evidence == null)
            {
                return new Dictionary<string, object>
                {
                    ["detailedAliasEvidenceAvailable"] = false,
                };
            }

            return new Dictionary<string, object>
            {
                ["detailedAliasEvidenceAvailable"] = true,
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
                    ["groupedOffset"] = NullableValue(evidence.Binding.Segment.IsArray ? null : evidence.Binding.GroupedOffset),
                    ["arrayIndex"] = NullableValue(evidence.Binding.ArrayIndex),
                    ["arrayLength"] = NullableValue(evidence.Binding.ArrayLength),
                },
                ["sampledTexture"] = TextureRecord(evidence.SampledTexture, evidence.ShaderIdentity),
                ["matchedAttachment"] = new Dictionary<string, object>
                {
                    ["role"] = evidence.Attachment.Role,
                    ["texture"] = TextureRecord(evidence.Attachment.Texture, evidence.Attachment.Identity),
                    ["loadPolicy"] = evidence.Attachment.LoadPolicy,
                    ["storePolicy"] = evidence.Attachment.StorePolicy,
                    ["resolvePolicy"] = evidence.Attachment.ResolvePolicy,
                },
            };
        }

        private static object NullableValue(int? value) => value.HasValue ? value.Value : null;

        private static Dictionary<string, object> TextureRecord(
            TextureBase texture,
            RenderFeedbackGuard.Identity identity) => new()
        {
            ["valid"] = texture.Valid,
            ["format"] = texture.Info.Format.ToString(),
            ["depthStencilMode"] = texture.Info.DepthStencilMode.ToString(),
            ["mtlFormat"] = texture.MtlFormat.ToString(),
            ["target"] = texture.Info.Target.ToString(),
            ["width"] = texture.Info.Width,
            ["height"] = texture.Info.Height,
            ["depth"] = texture.Info.Depth,
            ["declaredLevels"] = texture.Info.Levels,
            ["declaredLayers"] = texture.Info.GetLayers(),
            ["firstLevel"] = texture.FirstLevel,
            ["firstLayer"] = texture.FirstLayer,
            ["identity"] = IdentityRecord(identity),
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

        private static string Pointer(IntPtr value) => $"0x{(ulong)value:X}";

        private static Dictionary<string, object> DepthStencilRecord(
            DepthStencilUid uid,
            int frontReference,
            int backReference) => new()
        {
            ["depthCompare"] = uid.DepthCompareFunction.ToString(),
            ["depthWriteEnabled"] = uid.DepthWriteEnabled,
            ["stencilTestEnabled"] = uid.StencilTestEnabled,
            ["front"] = StencilRecord(uid.FrontFace, frontReference),
            ["back"] = StencilRecord(uid.BackFace, backReference),
        };

        private static Dictionary<string, object> StencilRecord(StencilUid uid, int reference) => new()
        {
            ["compare"] = uid.StencilCompareFunction.ToString(),
            ["readMask"] = uid.ReadMask,
            ["writeMask"] = uid.WriteMask,
            ["stencilFailureOperation"] = uid.StencilFailureOperation.ToString(),
            ["depthFailureOperation"] = uid.DepthFailureOperation.ToString(),
            ["depthStencilPassOperation"] = uid.DepthStencilPassOperation.ToString(),
            ["reference"] = reference,
        };

        private static List<Dictionary<string, object>> RenderPassPolicy(EncoderState state)
        {
            List<Dictionary<string, object>> result = [];
            for (int index = 0; index < state.RenderTargets.Length; index++)
            {
                Texture target = state.RenderTargets[index];
                if (target != null)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["role"] = $"color[{index}]",
                        ["targetFormat"] = target.MtlFormat.ToString(),
                        ["loadAction"] = state.ClearLoadAction ? "Clear" : "Load",
                        ["storeAction"] = "Store",
                        ["resolveAction"] = "None",
                    });
                }
            }
            if (state.DepthStencil is Texture depthStencil)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["role"] = "depth/stencil",
                    ["targetFormat"] = depthStencil.MtlFormat.ToString(),
                    ["loadAction"] = "Load for every present aspect",
                    ["storeAction"] = "Store for every present aspect",
                    ["resolveAction"] = "None",
                });
            }
            return result;
        }

        private static List<Dictionary<string, object>> SegmentRecords(ResourceBindingSegment[][] sets)
        {
            List<Dictionary<string, object>> result = [];
            if (sets == null)
            {
                return result;
            }
            for (int set = 0; set < sets.Length; set++)
            {
                if (sets[set] == null)
                {
                    continue;
                }
                foreach (ResourceBindingSegment segment in sets[set])
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["set"] = set,
                        ["binding"] = segment.Binding,
                        ["count"] = segment.Count,
                        ["type"] = segment.Type.ToString(),
                        ["stages"] = segment.Stages.ToString(),
                        ["isArray"] = segment.IsArray,
                    });
                }
            }
            return result;
        }

        private static JsonSerializerOptions JsonOptions(bool indented)
        {
            JsonSerializerOptions options = new() { WriteIndented = indented };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }

        private static string Sha256(ReadOnlySpan<byte> bytes) =>
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static void WriteNew(string path, ReadOnlySpan<byte> bytes)
        {
            using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
    }
}
