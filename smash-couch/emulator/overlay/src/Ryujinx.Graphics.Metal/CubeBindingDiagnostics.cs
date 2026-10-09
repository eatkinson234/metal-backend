using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ryujinx.Graphics.Metal
{
    // Opt-in, bounded evidence only. It never selects a texture, changes an
    // argument-buffer write, or rejects a draw.
    [SupportedOSPlatform("macos")]
    internal static class CubeBindingDiagnostics
    {
        private const string EvidenceDirectoryVariable = "SMASH_COUCH_CUBE_EVIDENCE_DIR";
        private const int MaximumProgramCount = 512;
        private const int MaximumBindingRecordCount = 4096;
        private const int MaximumShaderSourceBytes = 32 * 1024 * 1024;
        private const int MaximumMetadataBytes = 16 * 1024 * 1024;
        private const int MaximumTotalBytes = 64 * 1024 * 1024;
        private const int LimitReceiptReserve = 4096;
        private static readonly object Gate = new();
        private static readonly HashSet<string> ProgramIdentities = new(StringComparer.Ordinal);
        private static readonly HashSet<string> BindingIdentities = new(StringComparer.Ordinal);
        private static string _directory;
        private static string _pathFailure;
        private static bool _pathChecked;
        private static bool _captureDisabled;
        private static bool _limitReceiptWritten;
        private static int _shaderSourceBytes;
        private static int _metadataBytes;
        private static int _totalBytes;

        internal static bool Enabled => !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(EvidenceDirectoryVariable));

        internal static string RegisterProgram(ShaderSource[] shaders, ResourceLayout layout)
        {
            if (!Enabled || shaders == null || !shaders.Any(shader =>
                    (shader.Stage == ShaderStage.Fragment && ContainsCubeType(shader.Code)) ||
                    (shader.Stage == ShaderStage.Vertex && shader.Code != null &&
                     shader.Code.Contains("outAttr3", StringComparison.Ordinal))))
            {
                return null;
            }

            try
            {
                // Keep the label identity tied to the original constructor inputs,
                // but do not export preloaded programs that never bind at a draw.
                return TryEncodeProgram(shaders, layout, out string identity, out _, out _, out _, out _, out _)
                    ? identity
                    : null;
            }
            catch
            {
                // Identity collection is best-effort and must not write or affect
                // program creation when diagnostic encoding fails.
                return null;
            }
        }

        internal static bool CaptureProgramOnFirstBinding(Program program)
        {
            return program != null && CaptureProgramOnFirstBinding(
                program.CubeDiagnosticIdentity,
                program.FeedbackShaderSources,
                program.FeedbackResourceUsages,
                program.BindingSegments?.Length ?? 0);
        }

        internal static bool CaptureProgramOnFirstBinding(string expectedIdentity,
            Program.FeedbackShaderSource[] retainedShaders,
            Program.FeedbackResourceUsage[] retainedUsages, int setCount)
        {
            using var timing = NativeTimingDiagnostics.Measure(NativeTimingKind.DiagnosticProgram);
            if (!Enabled || string.IsNullOrWhiteSpace(expectedIdentity))
            {
                return false;
            }

            lock (Gate)
            {
                if (_captureDisabled) return false;
                if (ProgramIdentities.Contains(expectedIdentity)) return true;
                if (!EnsureEvidenceDirectory()) return false;
            }

            try
            {
                if (!TryEncodeRetainedProgram(retainedShaders, retainedUsages, setCount,
                        out string identity,
                        out List<Dictionary<string, object>> shaderRecords,
                        out List<(string Name, byte[] Bytes)> shaderFiles,
                        out byte[] layoutBytes, out int sourceBytes, out string failure))
                {
                    NoteLimit(failure ?? "first-binding-program-encoding-failed");
                    return false;
                }

                if (!string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
                {
                    NoteLimit("first-binding-program-identity-mismatch");
                    return false;
                }

                lock (Gate)
                {
                    if (_captureDisabled || !EnsureEvidenceDirectory()) return false;
                    if (ProgramIdentities.Contains(identity)) return true;
                    if (ProgramIdentities.Count >= MaximumProgramCount ||
                        sourceBytes > MaximumShaderSourceBytes - _shaderSourceBytes)
                    {
                        WriteLimitReceipt(ProgramIdentities.Count >= MaximumProgramCount
                            ? "program-count-quota-exhausted"
                            : "shader-source-byte-quota-exhausted");
                        return false;
                    }

                    List<string> sourceFiles = [];
                    foreach ((string name, byte[] bytes) in shaderFiles)
                    {
                        string relative = Path.Combine("programs", identity + "-" + name);
                        if (!WriteNewFile(relative, bytes, source: true)) return false;
                        sourceFiles.Add(Path.GetFileName(relative));
                    }

                    Dictionary<string, object> record = new()
                    {
                        ["schema"] = "smash-couch-cube-program-v1",
                        ["programIdentity"] = identity,
                        ["capturedUtc"] = DateTimeOffset.UtcNow,
                        ["cubeSourceFilter"] = "fragment cube type OR vertex outAttr3; diagnostic selection only; expanded for run31 fault attribution",
                        ["shaderSources"] = shaderRecords,
                        ["resourceLayout"] = JsonSerializer.Deserialize<JsonElement>(layoutBytes),
                        ["shaderFiles"] = sourceFiles,
                        ["layoutSha256"] = Sha256(layoutBytes),
                    };
                    byte[] recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                    if (!WriteNewFile(Path.Combine("programs", identity + ".json"), recordBytes, source: false))
                        return false;

                    ProgramIdentities.Add(identity);
                    return true;
                }
            }
            catch (Exception failure)
            {
                DisableCapture("first-binding-program-capture-failed", failure);
                return false;
            }
        }

        internal static bool ContainsCubeType(string code) =>
            code?.Contains("texturecube", StringComparison.Ordinal) == true ||
            code?.Contains("depthcube", StringComparison.Ordinal) == true;

        internal static string PipelineLabel(string programIdentity) =>
            Enabled && !string.IsNullOrWhiteSpace(programIdentity)
                ? $"CouchCubeDiag:v1:{programIdentity}"
                : null;

        internal static string ComputeProgramIdentity(ShaderSource[] shaders, ResourceLayout layout)
        {
            if (!TryEncodeProgram(shaders, layout, out string identity, out _, out _, out _, out _, out _))
                return null;
            return identity;
        }

        internal static bool IsPrivateEvidenceDirectory(string configured, string privateData,
            string labDirectory, out string fullDirectory, out string failure)
        {
            fullDirectory = null;
            failure = null;
            try
            {
                string directory = Path.GetFullPath(configured ?? string.Empty);
                string data = Path.GetFullPath(privateData ?? string.Empty);
                string lab = Path.GetFullPath(labDirectory ?? string.Empty);
                string dataPrefix = data.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string labPrefix = lab.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                if (!Directory.Exists(directory) || !Directory.Exists(data) || !Directory.Exists(lab) ||
                    !data.StartsWith(labPrefix, StringComparison.Ordinal) ||
                    !directory.StartsWith(dataPrefix, StringComparison.Ordinal))
                {
                    failure = "evidence directory must be a pre-existing private Lab child of the actual -r data root";
                    return false;
                }

                string current = directory;
                while (current != null)
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        failure = "evidence path ancestry contains a symbolic link or reparse point";
                        return false;
                    }
                    if (string.Equals(current, lab, StringComparison.Ordinal)) break;
                    current = Path.GetDirectoryName(current);
                }

                if (!string.Equals(current, lab, StringComparison.Ordinal))
                {
                    failure = "evidence path ancestry did not reach the Lab directory";
                    return false;
                }

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    failure = "evidence directory must be fresh and empty";
                    return false;
                }

                fullDirectory = directory;
                return true;
            }
            catch (Exception exception)
            {
                failure = $"{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        internal static bool FitsQuota(int currentBytes, int incomingBytes, int maximumBytes) =>
            currentBytes >= 0 && incomingBytes >= 0 && maximumBytes >= 0 &&
            incomingBytes <= maximumBytes - currentBytes;

        internal static void LabelTexture(MTLTexture handle, TextureBase storage, string role)
        {
            if (!Enabled || handle.NativePtr == IntPtr.Zero || storage == null) return;

            try
            {
                RenderFeedbackGuard.Identity identity = RenderFeedbackGuard.ReadIdentity(handle);
                TextureCreateInfo info = storage.Info;
                string label = $"CouchCubeDiagTexture:v1:id={handle.GpuResourceID._impl:X};" +
                    $"role={role};target={info.Target};format={info.Format};" +
                    $"extent={info.Width}x{info.Height}x{info.Depth};levels={info.Levels};" +
                    $"sub={storage.FirstLevel},{storage.FirstLayer};" +
                    $"view=0x{(ulong)identity.View:X};root=0x{(ulong)identity.Root:X}";
                // StringHelper returns an autoreleased NSString; the label setter owns its copy.
                NSString nativeLabel = StringHelper.NSString(label);
                handle.Label = nativeLabel;
            }
            catch
            {
                // Debug labels are best-effort and cannot affect resource use.
            }
        }

        internal static Dictionary<string, object> TextureRecord(TextureBase storage,
            MTLTexture handle, string role, bool snapshotReplacement)
        {
            Dictionary<string, object> record = new()
            {
                ["role"] = role,
                ["snapshotReplacement"] = snapshotReplacement,
                ["wrapperType"] = storage?.GetType().Name,
                ["wrapper"] = storage == null ? null : TextureInfoRecord(storage),
                ["native"] = NativeTextureRecord(handle),
            };

            if (storage is Texture texture)
            {
                try
                {
                    record["identityView"] = NativeTextureRecord(texture.GetRenderAttachmentHandle());
                }
                catch (Exception failure)
                {
                    record["identityViewFailure"] = $"{failure.GetType().Name}: {failure.Message}";
                }
            }

            return record;
        }

        internal static Dictionary<string, object> BindingRecord(int setIndex,
            ResourceBindingSegment segment, int logicalBinding, int? arrayIndex, int? arrayLength,
            int expectedFirstSlot, int expectedPointerSlots, int? expectedSamplerSlot, int actualTextureSlot,
            ulong textureGpuResourceId, TextureBase originalStorage, TextureBase storage, bool snapshotReplacement,
            bool samplerExpected, int? actualSamplerSlot, ulong? samplerGpuResourceId)
        {
            MTLTexture handle = storage?.GetHandle() ?? default;
            MTLTexture originalHandle = originalStorage?.GetHandle() ?? default;
            return new Dictionary<string, object>
            {
                ["setIndex"] = setIndex,
                ["segmentBinding"] = segment.Binding,
                ["segmentCount"] = segment.Count,
                ["segmentType"] = segment.Type.ToString(),
                ["segmentStages"] = segment.Stages.ToString(),
                ["segmentIsArray"] = segment.IsArray,
                ["logicalBinding"] = logicalBinding,
                ["arrayIndex"] = arrayIndex,
                ["arrayLength"] = arrayLength,
                ["expectedFirstArgumentBufferSlot"] = expectedFirstSlot,
                ["expectedPointerSlots"] = expectedPointerSlots,
                ["expectedSamplerArgumentBufferSlot"] = expectedSamplerSlot,
                ["actualTextureSlot"] = actualTextureSlot,
                ["textureGpuResourceId"] = textureGpuResourceId,
                ["samplerExpectedBySegment"] = samplerExpected,
                ["samplerWasNull"] = samplerExpected && !samplerGpuResourceId.HasValue,
                ["actualSamplerSlot"] = actualSamplerSlot,
                ["samplerGpuResourceId"] = samplerGpuResourceId,
                ["snapshotReplacement"] = snapshotReplacement,
                ["originalTexture"] = TextureRecord(originalStorage, originalHandle,
                    "original-bound-texture", snapshotReplacement),
                ["texture"] = TextureRecord(storage, handle, "effective-bound-texture", snapshotReplacement),
            };
        }

        internal static Dictionary<string, object> EmittedSlot(int slot, int logicalBinding,
            int? arrayIndex, string resourceKind, ulong gpuResourceId) => new()
        {
            ["slotIndex"] = slot,
            ["logicalBinding"] = logicalBinding,
            ["arrayIndex"] = arrayIndex,
            ["resourceKind"] = resourceKind,
            ["gpuResourceId"] = gpuResourceId,
        };

        internal static void RecordArgumentBuffer(Program program, string drawContext,
            IntPtr commandBuffer, ulong pipelineUid0, ulong pipelineUid1, int setIndex,
            int expectedSlots, int actualSlotsWritten,
            List<Dictionary<string, object>> bindings, List<Dictionary<string, object>> emittedSlots)
        {
            using var timing = NativeTimingDiagnostics.Measure(NativeTimingKind.DiagnosticCapture);
            if (!Enabled || program?.CubeDiagnosticIdentity == null || bindings == null || emittedSlots == null)
                return;

            try
            {
                if (!CaptureProgramOnFirstBinding(program)) return;

                Dictionary<string, object> stable = new()
                {
                    ["programIdentity"] = program.CubeDiagnosticIdentity,
                    ["pipelineUid0"] = $"0x{pipelineUid0:X16}",
                    ["pipelineUid1"] = $"0x{pipelineUid1:X16}",
                    ["setIndex"] = setIndex,
                    ["expectedFragmentArgumentBufferSlots"] = expectedSlots,
                    ["actualFragmentArgumentBufferSlotsWritten"] = actualSlotsWritten,
                    ["bindings"] = bindings,
                    ["emittedFragmentArgumentBufferIds"] = emittedSlots,
                };
                byte[] stableBytes = JsonSerializer.SerializeToUtf8Bytes(stable, JsonOptions);
                string identity = Sha256(stableBytes);

                lock (Gate)
                {
                    if (_captureDisabled || !EnsureEvidenceDirectory() || BindingIdentities.Contains(identity)) return;
                    if (BindingIdentities.Count >= MaximumBindingRecordCount)
                    {
                        WriteLimitReceipt("binding-record-count-quota-exhausted");
                        return;
                    }

                    Dictionary<string, object> record = new(stable)
                    {
                        ["schema"] = "smash-couch-cube-binding-state-v1",
                        ["capturedUtc"] = DateTimeOffset.UtcNow,
                        ["firstSeenDrawContext"] = drawContext,
                        ["commandBuffer"] = $"0x{(ulong)commandBuffer:X}",
                        ["deduplicationIdentity"] = identity,
                    };
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
                    if (!WriteNewFile(Path.Combine("bindings", identity + ".json"), bytes, source: false)) return;
                    BindingIdentities.Add(identity);
                }
            }
            catch (Exception failure)
            {
                DisableCapture("binding-capture-failed", failure);
            }
        }

        private static Dictionary<string, object> TextureInfoRecord(TextureBase storage)
        {
            TextureCreateInfo info = storage.Info;
            MTLTexture handle = storage.GetHandle();
            RenderFeedbackGuard.Identity identity = RenderFeedbackGuard.ReadIdentity(handle);
            return new Dictionary<string, object>
            {
                ["target"] = info.Target.ToString(),
                ["format"] = info.Format.ToString(),
                ["width"] = info.Width,
                ["height"] = info.Height,
                ["depth"] = info.Depth,
                ["levels"] = info.Levels,
                ["samples"] = info.Samples,
                ["bytesPerPixel"] = info.BytesPerPixel,
                ["blockWidth"] = info.BlockWidth,
                ["blockHeight"] = info.BlockHeight,
                ["depthStencilMode"] = info.DepthStencilMode.ToString(),
                ["swizzle"] = new[] { info.SwizzleR.ToString(), info.SwizzleG.ToString(),
                    info.SwizzleB.ToString(), info.SwizzleA.ToString() },
                ["firstLevel"] = storage.FirstLevel,
                ["firstLayer"] = storage.FirstLayer,
                ["feedbackIdentity"] = new Dictionary<string, object>
                {
                    ["known"] = identity.Known,
                    ["view"] = Pointer(identity.View),
                    ["root"] = Pointer(identity.Root),
                    ["buffer"] = Pointer(identity.Buffer),
                    ["level"] = identity.Level,
                    ["slice"] = identity.Slice,
                    ["levels"] = identity.Levels,
                    ["layers"] = identity.Layers,
                    ["fault"] = identity.Fault,
                },
            };
        }

        private static Dictionary<string, object> NativeTextureRecord(MTLTexture texture)
        {
            Dictionary<string, object> record = new()
            {
                ["nativePointer"] = Pointer(texture.NativePtr),
                ["queried"] = false,
            };
            if (texture.NativePtr == IntPtr.Zero) return record;

            try
            {
                record["queried"] = true;
                record["gpuResourceId"] = texture.GpuResourceID._impl;
                record["textureType"] = texture.TextureType.ToString();
                record["pixelFormat"] = texture.PixelFormat.ToString();
                record["width"] = texture.Width;
                record["height"] = texture.Height;
                record["depth"] = texture.Depth;
                record["sampleCount"] = texture.SampleCount;
                record["mipmapLevelCount"] = texture.MipmapLevelCount;
                record["arrayLength"] = texture.ArrayLength;
                record["storageMode"] = texture.StorageMode.ToString();
                record["hazardTrackingMode"] = texture.HazardTrackingMode.ToString();
                // Label is borrowed. Copy UTF-8 while the texture/pool is alive;
                // the old helper undersized a UTF-16 getCString buffer.
                NSString label = texture.Label;
                record["label"] = label.NativePtr == IntPtr.Zero ? null :
                    Marshal.PtrToStringUTF8(ObjectiveC.IntPtr_objc_msgSend(label.NativePtr, "UTF8String"));
            }
            catch (Exception failure)
            {
                record["queryFailure"] = $"{failure.GetType().Name}: {failure.Message}";
            }
            return record;
        }

        private static bool TryEncodeProgram(ShaderSource[] shaders, ResourceLayout layout,
            out string identity, out List<Dictionary<string, object>> shaderRecords,
            out List<(string Name, byte[] Bytes)> shaderFiles, out byte[] layoutBytes,
            out int sourceBytes, out string failure)
        {
            identity = null;
            shaderRecords = [];
            shaderFiles = [];
            layoutBytes = [];
            sourceBytes = 0;
            failure = null;

            if (shaders == null || layout.SetUsages == null)
            {
                failure = "program-or-layout-missing";
                return false;
            }

            foreach (ShaderSource shader in shaders.OrderBy(shader => shader.Stage))
            {
                byte[] code = Encoding.UTF8.GetBytes(shader.Code ?? string.Empty);
                if (code.Length > MaximumShaderSourceBytes - sourceBytes)
                {
                    failure = "program-shader-source-exceeds-per-program-byte-quota";
                    return false;
                }
                sourceBytes += code.Length;
                string codeHash = Sha256(code);
                string fileName = $"{shader.Stage}.metal";
                shaderFiles.Add((fileName, code));
                shaderRecords.Add(new Dictionary<string, object>
                {
                    ["stage"] = shader.Stage.ToString(),
                    ["entryPoint"] = shader.Stage switch
                    {
                        ShaderStage.Vertex => "vertexMain",
                        ShaderStage.Fragment => "fragmentMain",
                        ShaderStage.Compute => "kernelMain",
                        _ => "unsupported",
                    },
                    ["file"] = fileName,
                    ["utf8Bytes"] = code.Length,
                    ["sha256"] = codeHash,
                });
            }

            List<Dictionary<string, object>> sets = [];
            for (int set = 0; set < layout.SetUsages.Count; set++)
            {
                List<Dictionary<string, object>> usages = [];
                foreach (ResourceUsage usage in layout.SetUsages[set].Usages)
                {
                    usages.Add(new Dictionary<string, object>
                    {
                        ["binding"] = usage.Binding,
                        ["arrayLength"] = usage.ArrayLength,
                        ["type"] = usage.Type.ToString(),
                        ["stages"] = usage.Stages.ToString(),
                        ["write"] = usage.Write,
                    });
                }
                sets.Add(new Dictionary<string, object> { ["set"] = set, ["usages"] = usages });
            }
            layoutBytes = JsonSerializer.SerializeToUtf8Bytes(sets, JsonOptions);

            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (Dictionary<string, object> shader in shaderRecords)
            {
                AppendHash(digest, (string)shader["stage"]);
                AppendHash(digest, (string)shader["sha256"]);
            }
            AppendHash(digest, Sha256(layoutBytes));
            identity = Convert.ToHexString(digest.GetHashAndReset());
            return true;
        }

        private static bool TryEncodeRetainedProgram(Program.FeedbackShaderSource[] retainedShaders,
            Program.FeedbackResourceUsage[] retainedUsages, int setCount,
            out string identity, out List<Dictionary<string, object>> shaderRecords,
            out List<(string Name, byte[] Bytes)> shaderFiles, out byte[] layoutBytes,
            out int sourceBytes, out string failure)
        {
            identity = null;
            shaderRecords = [];
            shaderFiles = [];
            layoutBytes = [];
            sourceBytes = 0;
            failure = null;

            if (retainedShaders == null || retainedUsages == null || setCount < 0)
            {
                failure = "first-binding-retained-program-metadata-missing";
                return false;
            }

            List<ResourceUsage>[] usagesBySet = new List<ResourceUsage>[setCount];
            for (int set = 0; set < setCount; set++) usagesBySet[set] = [];
            foreach (Program.FeedbackResourceUsage retained in retainedUsages)
            {
                if ((uint)retained.Set >= (uint)setCount)
                {
                    failure = "first-binding-retained-resource-set-out-of-range";
                    return false;
                }

                usagesBySet[retained.Set].Add(new ResourceUsage(
                    retained.Binding, retained.ArrayLength, retained.Type, retained.Stages, retained.Write));
            }

            ShaderSource[] shaders = retainedShaders
                .Select(shader => new ShaderSource(shader.Code, shader.Stage, TargetLanguage.Msl))
                .ToArray();
            ResourceUsageCollection[] setUsages = new ResourceUsageCollection[setCount];
            ResourceDescriptorCollection[] descriptors = new ResourceDescriptorCollection[setCount];
            for (int set = 0; set < setCount; set++)
            {
                ResourceUsage[] usages = usagesBySet[set].ToArray();
                ResourceDescriptor[] setDescriptors = usages
                    .Select(usage => new ResourceDescriptor(
                        usage.Binding, usage.ArrayLength, usage.Type, usage.Stages))
                    .ToArray();
                setUsages[set] = new ResourceUsageCollection(Array.AsReadOnly(usages));
                descriptors[set] = new ResourceDescriptorCollection(Array.AsReadOnly(setDescriptors));
            }

            ResourceLayout layout = new(Array.AsReadOnly(descriptors), Array.AsReadOnly(setUsages));
            return TryEncodeProgram(shaders, layout, out identity, out shaderRecords,
                out shaderFiles, out layoutBytes, out sourceBytes, out failure);
        }

        private static void AppendHash(IncrementalHash hash, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }

        private static bool EnsureEvidenceDirectory()
        {
            if (_pathChecked) return _directory != null;
            _pathChecked = true;

            string configured = Environment.GetEnvironmentVariable(EvidenceDirectoryVariable);
            string[] arguments = Environment.GetCommandLineArgs();
            int rootSwitch = Array.IndexOf(arguments, "-r");
            if (rootSwitch < 0 || rootSwitch + 1 >= arguments.Length)
            {
                _pathFailure = "cube evidence requires the actual private -r data path";
                return false;
            }

            string lab = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Smash Couch", "Lab"));
            if (!IsPrivateEvidenceDirectory(configured, arguments[rootSwitch + 1], lab,
                    out _directory, out _pathFailure))
            {
                _directory = null;
                return false;
            }
            return true;
        }

        private static bool WriteNewFile(string relativePath, byte[] bytes, bool source)
        {
            if (_captureDisabled || !EnsureEvidenceDirectory()) return false;
            int sourceNext = source ? _shaderSourceBytes + bytes.Length : _shaderSourceBytes;
            int metadataNext = source ? _metadataBytes : _metadataBytes + bytes.Length;
            if (sourceNext > MaximumShaderSourceBytes || metadataNext > MaximumMetadataBytes ||
                !FitsQuota(_totalBytes, bytes.Length, MaximumTotalBytes - LimitReceiptReserve))
            {
                WriteLimitReceipt(sourceNext > MaximumShaderSourceBytes
                    ? "shader-source-byte-quota-exhausted"
                    : metadataNext > MaximumMetadataBytes
                        ? "metadata-byte-quota-exhausted"
                        : "total-byte-quota-exhausted");
                return false;
            }

            string destination = Path.GetFullPath(Path.Combine(_directory, relativePath));
            string prefix = _directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(prefix, StringComparison.Ordinal))
            {
                WriteLimitReceipt("capture-path-escaped-evidence-directory");
                return false;
            }

            string parent = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(parent);
            using FileStream file = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            _totalBytes += bytes.Length;
            if (source) _shaderSourceBytes += bytes.Length;
            else _metadataBytes += bytes.Length;
            return true;
        }

        private static void WriteLimitReceipt(string reason)
        {
            if (_limitReceiptWritten) return;
            _limitReceiptWritten = true;
            _captureDisabled = true;
            if (!EnsureEvidenceDirectory() || _directory == null) return;

            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["schema"] = "smash-couch-cube-capture-limit-v1",
                    ["capturedUtc"] = DateTimeOffset.UtcNow,
                    ["reason"] = reason,
                    ["maximumPrograms"] = MaximumProgramCount,
                    ["maximumBindingRecords"] = MaximumBindingRecordCount,
                    ["maximumShaderSourceBytes"] = MaximumShaderSourceBytes,
                    ["maximumMetadataBytes"] = MaximumMetadataBytes,
                    ["maximumTotalBytes"] = MaximumTotalBytes,
                    ["programsCaptured"] = ProgramIdentities.Count,
                    ["bindingRecordsCaptured"] = BindingIdentities.Count,
                    ["shaderSourceBytesCaptured"] = _shaderSourceBytes,
                    ["metadataBytesCaptured"] = _metadataBytes,
                    ["totalBytesCaptured"] = _totalBytes,
                }, JsonOptions);
                if (FitsQuota(_totalBytes, bytes.Length, MaximumTotalBytes))
                {
                    using FileStream file = new(Path.Combine(_directory, "capture-limit.json"),
                        FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                    _totalBytes += bytes.Length;
                }
            }
            catch
            {
                // Quota reporting cannot affect graphics work.
            }
        }

        private static void NoteLimit(string reason)
        {
            lock (Gate)
            {
                WriteLimitReceipt(reason);
                _captureDisabled = true;
            }
        }

        private static void DisableCapture(string reason, Exception failure)
        {
            lock (Gate)
            {
                if (_captureDisabled) return;
                WriteLimitReceipt($"{reason}:{failure.GetType().Name}:{failure.Message}");
                _captureDisabled = true;
            }
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        private static string Pointer(IntPtr value) => $"0x{(ulong)value:X}";
        private static JsonSerializerOptions JsonOptions => new() { WriteIndented = true };
    }
}
