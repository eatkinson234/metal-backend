using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // Capture-only, synchronous, pre-native-compilation observation. There is
    // deliberately no target override, source reconstruction, renderer admission,
    // background worker, callback or capture-completion exception here.
    internal static class Stencil148InputCapture
    {
        internal const string Prefix = "SMASH_COUCH_STENCIL148_INPUT_CAPTURE";
        internal const string OutputLeaf = "stencil148-authentic-inputs";
        internal const string BaseSourceManifestSha256 = "46a6950f93b8b238ab486f10954b152c6335ea301229c6a0786e7b7344b8664f";
        internal const string OriginalVertexSha256 = "cb39380b174e5c85c6d0d1b324373012535464130cb0a01454bb6497f27da116";
        internal const string OriginalFragmentSha256 = "b101a4fe469e4a1aa9c811cccdb90cd7c6aafcfa95e05cc8faef37176e54a4ab";
        internal const string AdaptedVertexSha256 = "8cd664344e876cfcb97ae8187557d1f37b8821155ba3bd4b5761ed2c05b8f665";
        internal const string AdaptedFragmentSha256 = "ad30755c5a224083ea12fae073368753e6b76b0523e57c79945d0c39c310057e";
        internal const int MaximumConstructions = 8192;
        internal const int MaximumCodeBytes = 2 * 1024 * 1024;
        internal const int MaximumTotalBytes = 9 * 1024 * 1024;
        internal const int MaximumMetadataBytes = 256 * 1024;
        internal const int MaximumExecutableBytes = 128 * 1024 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly CaptureOnceGate Gate = new();

        internal static bool Enabled => Environment.GetEnvironmentVariable(Prefix) == "1";

        internal static void Observe(bool ordinaryPublicRoute, ShaderSource[] originals,
            ShaderSource[] adapted, ResourceLayout layout, ComputeSize computeLocalSize,
            bool usesTransport, bool usesPointTransport, string interfaceReceipt,
            ulong compilerLanguage, bool preserveInvariance, bool depthSnapshotShaderAllowed)
        {
            // No capture I/O, hashing, inventory, or thread creation outside this flag.
            if (!Enabled || !ordinaryPublicRoute || Gate.Claimed) return;
            int attempt = Gate.CountConstruction();
            if (attempt > MaximumConstructions) return;
            string directory = null;
            bool claimed = false;
            try
            {
                if (!MatchesOriginalTarget(originals))
                {
                    if (attempt != MaximumConstructions || !Gate.TryClaim()) return;
                    claimed = true;
                    throw new InvalidDataException("bounded-construction-limit-without-original-target");
                }
                if (!Gate.TryClaim()) return;
                claimed = true;
                // Production target predicates run once per construction. Later
                // comparisons only establish stable bytes/metadata for publication.
                ValidateMetadata(originals, adapted, layout, usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage);
                if (!MatchesAdaptedTarget(adapted)) throw new InvalidDataException("adapted-target-pair-mismatch");
                Dictionary<string, object> provenance = ObserveProvenance(out directory);
                byte[] inputs = SerializeInputs(originals, adapted, layout, computeLocalSize,
                    usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage,
                    preserveInvariance, depthSnapshotShaderAllowed);
                using CaptureFileWriter writer = new(directory, MaximumTotalBytes);
                List<object> files = [];
                for (int index = 0; index < originals.Length; index++)
                {
                    AddFile(writer, files, SourceName("original", index, originals[index].Stage), CodeBytes(originals[index].Code));
                    AddFile(writer, files, SourceName("adapted", index, adapted[index].Stage), CodeBytes(adapted[index].Code));
                }
                AddFile(writer, files, "inputs.json", inputs);
                // Re-observe the live immutable values and full collection topology;
                // ReadOnlyCollection can still wrap a mutable owner collection.
                ValidateMetadata(originals, adapted, layout, usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage);
                byte[] stableInputs = SerializeInputs(originals, adapted, layout, computeLocalSize,
                    usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage,
                    preserveInvariance, depthSnapshotShaderAllowed);
                if (!inputs.AsSpan().SequenceEqual(stableInputs))
                    throw new InvalidDataException("live-inputs-changed-during-capture");
                writer.VerifyAll();
                byte[] manifest = Json(new Dictionary<string, object>
                {
                    ["schema"] = "smash-couch-stencil148-authentic-inputs-v1",
                    ["status"] = "OBSERVED_AUTHENTIC_INPUTS",
                    ["hookStage"] = "ordinary-public-constructor-after-unchanged-adaptation-and-program-policy-before-native-compilation",
                    ["constructorReturned"] = false,
                    ["nativeCompilationObserved"] = false,
                    ["nativeLinkRenderingGameplayAccepted"] = false,
                    ["publicationUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["constructionChecks"] = attempt,
                    ["priorNonTargetConstructions"] = attempt - 1,
                    ["provenance"] = provenance,
                    ["files"] = files,
                    ["limits"] = Record(("maximumConstructions", MaximumConstructions), ("maximumCompleteFiles", 6),
                        ("maximumDirectoryEntriesIncludingPendingOrRefusal", 7),
                        ("maximumCodeBytesPerSource", MaximumCodeBytes), ("maximumTotalBytes", MaximumTotalBytes),
                        ("maximumMetadataBytes", MaximumMetadataBytes), ("synchronousCaptureOnlyPause", true)),
                });
                writer.PublishManifest("capture-manifest.json", manifest);
                Receipt("OBSERVED_AUTHENTIC_INPUTS", "pre-native-compilation; no constructor/link/rendering completion claim");
            }
            catch (Exception exception)
            {
                // Every capture failure is contained. The original constructor,
                // compilation, guard and caller-owned disposal remain authoritative.
                if (!claimed && Gate.TryClaim()) claimed = true;
                if (claimed)
                {
                    string reason = exception is InvalidDataException ? exception.Message : exception.GetType().Name;
                    TryWriteRefusal(directory, reason, attempt);
                    Receipt("REFUSED_OR_INCOMPLETE", reason);
                }
            }
        }

        internal static bool MatchesOriginalTarget(ShaderSource[] sources)
        {
            if (!IsPair(sources)) return false;
            foreach (ShaderSource source in sources)
            {
                string expected = source.Stage == ShaderStage.Vertex ? OriginalVertexSha256 : OriginalFragmentSha256;
                if (Sha(CodeBytes(source.Code)) != expected) return false;
            }
            return true;
        }

        private static bool IsPair(ShaderSource[] sources) => sources != null && sources.Length == 2 &&
            sources[0].Stage != sources[1].Stage && sources.All(source =>
                source.Stage is ShaderStage.Vertex or ShaderStage.Fragment &&
                source.Language == TargetLanguage.Msl && source.Code != null);

        internal static void ValidateInputs(ShaderSource[] originals, ShaderSource[] adapted,
            ResourceLayout layout, bool usesTransport, bool usesPointTransport,
            string interfaceReceipt, ulong compilerLanguage)
        {
            if (!MatchesOriginalTarget(originals)) throw new InvalidDataException("original-target-pair-mismatch");
            ValidateMetadata(originals, adapted, layout, usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage);
            if (!MatchesAdaptedTarget(adapted)) throw new InvalidDataException("adapted-target-pair-mismatch");
        }

        internal static bool MatchesAdaptedTarget(ShaderSource[] adapted)
        {
            if (!IsPair(adapted)) return false;
            foreach (ShaderSource source in adapted)
            {
                string expected = source.Stage == ShaderStage.Vertex ? AdaptedVertexSha256 : AdaptedFragmentSha256;
                if (Sha(CodeBytes(source.Code)) != expected) return false;
            }
            return true;
        }

        // Independent metadata/serialization checks are useful for explicitly
        // synthetic CPU fixtures; they cannot admit or change the pinned target.
        internal static void ValidateMetadata(ShaderSource[] originals, ShaderSource[] adapted,
            ResourceLayout layout, bool usesTransport, bool usesPointTransport,
            string interfaceReceipt, ulong compilerLanguage)
        {
            if (!IsPair(originals) || !IsPair(adapted)) throw new InvalidDataException("stage-language-code-pair-invalid");
            if (!usesTransport || usesPointTransport || compilerLanguage != 0x40000 ||
                string.IsNullOrEmpty(interfaceReceipt) || interfaceReceipt.Length > 8192)
                throw new InvalidDataException("transport-interface-language-observation-incomplete");
            for (int index = 0; index < 2; index++)
            {
                ShaderSource source = originals[index];
                MslNumericVaryingFacts facts = source.NumericVaryings;
                if (source.Stage != adapted[index].Stage || facts == null ||
                    !facts.Matches(source.Code, source.Stage) || adapted[index].NumericVaryings != null)
                    throw new InvalidDataException("source-bound-numeric-facts-or-adapted-order-invalid");
                if (facts.RefusalCounts == null || facts.RefusalCounts.Count > 128)
                    throw new InvalidDataException("refusal-count-metadata-incomplete-or-unbounded");
                foreach (var refusal in facts.RefusalCounts)
                    if (string.IsNullOrEmpty(refusal.Key) || refusal.Key.Length > 256 || refusal.Value < 0)
                        throw new InvalidDataException("refusal-count-metadata-malformed");
                for (int location = 0; location < 32; location++)
                    if (facts.GetComponentMask(location) > 15) throw new InvalidDataException("numeric-component-mask-invalid");
                CodeBytes(source.Code);
                CodeBytes(adapted[index].Code);
                if (source.BinaryCode?.Length > MaximumMetadataBytes || adapted[index].BinaryCode?.Length > MaximumMetadataBytes)
                    throw new InvalidDataException("binary-source-metadata-unbounded");
            }
            if (layout.Sets == null || layout.SetUsages == null || layout.Sets.Count > 64 || layout.SetUsages.Count > 64)
                throw new InvalidDataException("resource-layout-metadata-incomplete-or-unbounded");
            int descriptors = 0, usages = 0;
            foreach (var set in layout.Sets)
            {
                if (set.Descriptors == null) throw new InvalidDataException("resource-descriptor-set-missing");
                descriptors = checked(descriptors + set.Descriptors.Count);
                foreach (var item in set.Descriptors)
                    if (item.Binding < 0 || item.Count <= 0 || !Enum.IsDefined(item.Type) || ((byte)item.Stages & ~63) != 0)
                        throw new InvalidDataException("resource-descriptor-malformed");
            }
            foreach (var set in layout.SetUsages)
            {
                if (set.Usages == null) throw new InvalidDataException("resource-usage-set-missing");
                usages = checked(usages + set.Usages.Count);
                foreach (var item in set.Usages)
                    if (item.Binding < 0 || item.ArrayLength <= 0 || !Enum.IsDefined(item.Type) || ((byte)item.Stages & ~63) != 0)
                        throw new InvalidDataException("resource-usage-malformed");
            }
            if (descriptors > 4096 || usages > 4096) throw new InvalidDataException("resource-layout-record-limit");
        }

        internal static byte[] SerializeInputs(ShaderSource[] originals, ShaderSource[] adapted,
            ResourceLayout layout, ComputeSize computeLocalSize, bool usesTransport, bool usesPointTransport,
            string interfaceReceipt, ulong compilerLanguage, bool preserveInvariance, bool depthSnapshotShaderAllowed)
        {
            ValidateMetadata(originals, adapted, layout, usesTransport, usesPointTransport, interfaceReceipt, compilerLanguage);
            var record = new Dictionary<string, object>
            {
                ["schema"] = "smash-couch-stencil148-full-constructor-inputs-v1",
                ["originalSources"] = SourceRecords("original", originals),
                ["adaptedSources"] = SourceRecords("adapted", adapted),
                ["computeLocalSize"] = Record(("x", computeLocalSize.X), ("y", computeLocalSize.Y), ("z", computeLocalSize.Z)),
                ["resourceLayout"] = LayoutRecord(layout),
                ["observations"] = Record(("usesPerVertexTransport", usesTransport), ("usesPointTransport", usesPointTransport),
                    ("interfaceReceipt", interfaceReceipt), ("compilerLanguageNumeric", compilerLanguage), ("compilerLanguageName", "MSL4"),
                    ("preserveInvariance", preserveInvariance), ("fastMathEnabled", false), ("depthSnapshotShaderAllowed", depthSnapshotShaderAllowed),
                    ("familyGuard", "existing-macOS27-Apple10-barycentric-guard-passed-before-hook"),
                    ("deviceFamilyQueriedAgainByCapture", false), ("constructorReturned", false)),
            };
            return Json(record);
        }

        private static object[] SourceRecords(string kind, ShaderSource[] sources)
        {
            object[] records = new object[sources.Length];
            for (int index = 0; index < sources.Length; index++)
            {
                ShaderSource source = sources[index];
                byte[] code = CodeBytes(source.Code);
                Dictionary<string, object> record = new()
                {
                    ["index"] = index, ["stage"] = source.Stage.ToString(), ["stageNumeric"] = (int)source.Stage,
                    ["language"] = source.Language.ToString(), ["languageNumeric"] = (int)source.Language,
                    ["codeFile"] = SourceName(kind, index, source.Stage), ["codeUtf8Bytes"] = code.Length,
                    ["codeUtf8Sha256"] = Sha(code), ["codeUtf16Length"] = source.Code.Length,
                    ["binaryCode"] = source.BinaryCode, ["binaryCodeIsNull"] = source.BinaryCode == null,
                    ["numericVaryings"] = null,
                };
                if (source.NumericVaryings is MslNumericVaryingFacts facts)
                {
                    int[] masks = new int[32];
                    for (int location = 0; location < 32; location++) masks[location] = facts.GetComponentMask(location);
                    record["numericVaryings"] = Record(("policyVersion", MslNumericVaryingFacts.PolicyVersion),
                        ("stage", facts.Stage.ToString()), ("stageNumeric", (int)facts.Stage),
                        ("sourceSha256", facts.SourceSha256), ("componentMasks", masks),
                        ("refusalCounts", facts.RefusalCounts.Select(item => Record(("key", item.Key), ("value", item.Value))).ToArray()));
                }
                records[index] = record;
            }
            return records;
        }

        private static object LayoutRecord(ResourceLayout layout) => Record(
            ("descriptorSetCount", layout.Sets.Count), ("usageSetCount", layout.SetUsages.Count),
            ("sets", layout.Sets.Select((set, index) => Record(("index", index), ("count", set.Descriptors.Count),
                ("descriptors", set.Descriptors.Select((item, ordinal) => Record(("ordinal", ordinal), ("binding", item.Binding),
                    ("count", item.Count), ("type", item.Type.ToString()), ("typeNumeric", (byte)item.Type),
                    ("stages", item.Stages.ToString()), ("stagesNumeric", (byte)item.Stages))).ToArray()))).ToArray()),
            ("setUsages", layout.SetUsages.Select((set, index) => Record(("index", index), ("count", set.Usages.Count),
                ("usages", set.Usages.Select((item, ordinal) => Record(("ordinal", ordinal), ("binding", item.Binding),
                    ("arrayLength", item.ArrayLength), ("type", item.Type.ToString()), ("typeNumeric", (byte)item.Type),
                    ("stages", item.Stages.ToString()), ("stagesNumeric", (byte)item.Stages), ("write", item.Write))).ToArray()))).ToArray()));

        private static Dictionary<string, object> ObserveProvenance(out string directory)
        {
            directory = null;
            string[] args = Environment.GetCommandLineArgs();
            int rootIndex = Array.IndexOf(args, "-r");
            if (rootIndex < 0 || rootIndex + 1 >= args.Length || Array.LastIndexOf(args, "-r") != rootIndex)
                throw new InvalidDataException("actual-command-line-private-root-missing-or-ambiguous");
            string lab = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", "Smash Couch", "Lab"));
            string actualRoot = args[rootIndex + 1];
            string declaredRoot = Required("_OWNER_ROOT");
            string declaredStage = Required("_OWNER_STAGE");
            string configured = Required("_DIR");
            string sourceManifest = RequiredHash("_SOURCE_MANIFEST_SHA256");
            string expectedExecutable = RequiredHash("_EXECUTABLE_SHA256");
            string declaredMetalDll = RequiredHash("_METAL_DLL_SHA256");
            ValidateDeclaredBinding(actualRoot, lab, declaredRoot, declaredStage, configured);
            // Reuse the accepted private Lab/-r/empty-directory guard unchanged.
            if (!CubeBindingDiagnostics.IsPrivateEvidenceDirectory(configured, actualRoot, lab, out directory, out _))
                throw new InvalidDataException("private-evidence-directory-guard-refused");
            CaptureFileWriter.ValidateAncestry(declaredStage);
            Assembly assembly = typeof(Stencil148InputCapture).Assembly;
            // Published Ryujinx is single-file. Assembly.Location is not a live
            // on-disk DLL witness there; bind the actual signed process executable
            // and observed managed MVID instead of inventing an extracted DLL.
            string executablePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executablePath) || !IsCanonical(executablePath) ||
                !executablePath.StartsWith(declaredStage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("observed-executable-outside-declared-owner-stage");
            CaptureFileWriter.ValidateAncestry(Path.GetDirectoryName(executablePath));
            if ((File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("observed-executable-is-link");
            using FileStream executable = new(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (executable.Length <= 0 || executable.Length > MaximumExecutableBytes) throw new InvalidDataException("executable-byte-bound");
            string executableSha = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
            if (executableSha != expectedExecutable) throw new InvalidDataException("observed-executable-hash-not-declared-executable");
            return new Dictionary<string, object>
            {
                ["observedPid"] = Environment.ProcessId,
                ["observedActualCommandLinePrivateRoot"] = actualRoot,
                ["observedExecutablePath"] = executablePath, ["observedExecutableBytes"] = executable.Length,
                ["observedExecutableSha256"] = executableSha,
                ["observedBinaryHashKind"] = "actual-process-executable-file; single-file bundle supported",
                ["observedManagedModuleName"] = assembly.ManifestModule.Name,
                ["observedModuleMvid"] = assembly.ManifestModule.ModuleVersionId.ToString(),
                ["observedAssemblyName"] = assembly.GetName().Name,
                ["observedAssemblyVersion"] = assembly.GetName().Version?.ToString(),
                ["observedInformationalVersion"] = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                ["observedCaptureUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["observedCaptureMonotonicTicksDecimal"] = Stopwatch.GetTimestamp().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["observedMonotonicFrequencyDecimal"] = Stopwatch.Frequency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["declaredOwnerRoot"] = declaredRoot, ["declaredOwnerStage"] = declaredStage,
                ["declaredCandidateSourceManifestSha256"] = sourceManifest,
                ["declaredExpectedExecutableSha256"] = expectedExecutable,
                ["declaredMetalDllSha256"] = declaredMetalDll,
                ["acceptedBaseSourceManifestSha256"] = BaseSourceManifestSha256,
                ["sourceManifestAndOwnerProcessBindingVerifiedByParentRunner"] = false,
                ["sourceManifestQualification"] = "supplied identity; runtime does not inspect or reconstruct source tree; frozen parent build binding required",
                ["metalDllHashQualification"] = "supplied pre-bundle DLL identity; parent packaged canary required; not an observed runtime DLL byte hash",
                ["route"] = "ordinary-public-Program-constructor",
            };
        }

        internal static void ValidateDeclaredBinding(string actualRoot, string lab,
            string declaredRoot, string declaredStage, string configured)
        {
            if (!IsCanonical(lab) || !IsCanonical(actualRoot) || actualRoot != declaredRoot ||
                !actualRoot.StartsWith(lab + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !IsCanonical(declaredStage) || !declaredStage.StartsWith(lab + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                configured != Path.Combine(actualRoot, OutputLeaf))
                throw new InvalidDataException("declared-owner-stage-root-or-output-binding-invalid");
        }

        internal static bool IsCanonical(string path) => path != null && Path.IsPathFullyQualified(path) &&
            path == Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        private static string Required(string suffix) => Environment.GetEnvironmentVariable(Prefix + suffix) is string value && value.Length > 0 && value.Length < 4096
            ? value : throw new InvalidDataException("mandatory-capture-binding-missing");
        private static string RequiredHash(string suffix)
        {
            return ValidateDeclaredHash(Required(suffix));
        }
        internal static string ValidateDeclaredHash(string value)
        {
            if (value == null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
                throw new InvalidDataException("mandatory-capture-hash-malformed");
            return value.ToLowerInvariant();
        }
        internal static byte[] CodeBytes(string code)
        {
            if (code == null || code.Length > MaximumCodeBytes || StrictUtf8.GetByteCount(code) > MaximumCodeBytes)
                throw new InvalidDataException("source-code-byte-bound-or-missing");
            return StrictUtf8.GetBytes(code);
        }
        internal static string Sha(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static string SourceName(string kind, int index, ShaderStage stage) => $"{kind}-{index:D2}-{stage}.metal";
        private static Dictionary<string, object> Record(params (string Key, object Value)[] fields)
        {
            Dictionary<string, object> record = new(StringComparer.Ordinal);
            foreach (var field in fields) record.Add(field.Key, field.Value);
            return record;
        }
        private static byte[] Json(object value) => SerializeMetadataRecord(value);
        internal static byte[] SerializeMetadataRecord(object value)
        {
            // Only the capture's closed primitive/dictionary shapes are accepted.
            // No reflection-based property discovery or anonymous-type metadata
            // is needed in the existing trimmed single-file application.
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream)) WriteMetadataValue(writer, value);
            byte[] bytes = stream.ToArray();
            if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("serialized-metadata-byte-bound");
            return bytes;
        }
        private static void WriteMetadataValue(Utf8JsonWriter writer, object value)
        {
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case bool flag: writer.WriteBooleanValue(flag); break;
                case int number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case ulong number: writer.WriteNumberValue(number); break;
                case byte number: writer.WriteNumberValue(number); break;
                case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
                case int[] numbers:
                    writer.WriteStartArray(); foreach (int number in numbers) writer.WriteNumberValue(number); writer.WriteEndArray(); break;
                case Dictionary<string, object> record:
                    writer.WriteStartObject();
                    foreach (var field in record) { writer.WritePropertyName(field.Key); WriteMetadataValue(writer, field.Value); }
                    writer.WriteEndObject(); break;
                case IEnumerable<object> items:
                    writer.WriteStartArray(); foreach (object item in items) WriteMetadataValue(writer, item); writer.WriteEndArray(); break;
                default: throw new InvalidDataException("capture-metadata-value-type-invalid");
            }
        }
        private static void AddFile(CaptureFileWriter writer, List<object> records, string name, byte[] bytes)
        {
            writer.WriteNew(name, bytes);
            records.Add(Record(("file", name), ("bytes", bytes.Length), ("sha256", Sha(bytes))));
        }
        private static void TryWriteRefusal(string directory, string reason, int attempt)
        {
            if (directory == null) return;
            try
            {
                using CaptureFileWriter writer = new(directory, MaximumMetadataBytes, requireEmpty: false);
                writer.WriteNew("capture-refused.json", Json(Record(("schema", "smash-couch-stencil148-capture-refused-v1"),
                    ("status", "REFUSED_OR_INCOMPLETE"), ("reason", reason[..Math.Min(reason.Length, 256)]),
                    ("observedPid", Environment.ProcessId), ("constructionChecks", attempt), ("noAutomaticRetry", true),
                    ("capturedUtc", DateTimeOffset.UtcNow.ToString("O")), ("constructorBehaviorChanged", false))));
            }
            catch { /* A bounded stderr receipt remains when no safe owned output can be written. */ }
        }
        private static void Receipt(string status, string reason)
        {
            try { Console.Error.WriteLine($"[CouchStencil148InputCapture] status={status} reason={reason[..Math.Min(reason.Length, 256)]}"); }
            catch { /* Diagnostics must not change constructor behavior. */ }
        }
    }

    internal sealed class CaptureOnceGate
    {
        private int _claimed;
        private int _checks;
        internal bool Claimed => Volatile.Read(ref _claimed) != 0;
        internal int CountConstruction() => Interlocked.Increment(ref _checks);
        internal bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }

    // Scoped POSIX file handles anchor writes to the inspected directory. No
    // following of leaf or ancestry links, no replacement, no cache path writes.
    // This is also the exact byte writer exercised by synthetic CPU fixtures.
    internal sealed class CaptureFileWriter : IDisposable
    {
        private readonly SafeFileHandle _directory;
        private readonly string _path;
        private readonly int _maximumBytes;
        private readonly List<(string Name, byte[] Bytes)> _written = [];
        private int _bytes;
        private bool _published;
        private static int NoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;
        private static int DirectoryFlag => OperatingSystem.IsMacOS() ? 0x100000 : 0x10000;
        private static int CreateExclusive => OperatingSystem.IsMacOS() ? 0x200 | 0x800 : 0x40 | 0x80;
        [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int openat(int directory, string path, int flags, uint mode);
        [DllImport("libc", SetLastError = true)] private static extern int fchmod(int file, uint mode);
        [DllImport("libc", SetLastError = true)] private static extern int fstat(int file, [Out] byte[] stat);
        [DllImport("libc", SetLastError = true)] private static extern int linkat(int fromDirectory, string fromName, int toDirectory, string toName, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int unlinkat(int directory, string name, int flags);

        internal CaptureFileWriter(string path, int maximumBytes, bool requireEmpty = true)
        {
            if (!Stencil148InputCapture.IsCanonical(path) || maximumBytes <= 0) throw new IOException("noncanonical-or-invalid-writer-path");
            ValidateAncestry(path);
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("output-directory-must-be-private-mode-0700");
            if (requireEmpty && Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("output-directory-not-empty");
            _path = path;
            _maximumBytes = maximumBytes;
            _directory = OpenDirectory(path);
        }

        internal static void ValidateAncestry(string path)
        {
            if (!Stencil148InputCapture.IsCanonical(path)) throw new IOException("noncanonical-path");
            string current = path;
            while (current != null)
            {
                if (!Directory.Exists(current) || new DirectoryInfo(current).LinkTarget != null ||
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("path-link-or-missing-ancestor");
                current = Path.GetDirectoryName(current);
            }
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            int fd = open("/", NoFollow | DirectoryFlag);
            if (fd < 0) throw new IOException("root-directory-open-failed");
            SafeFileHandle current = new((IntPtr)fd, true);
            try
            {
                foreach (string part in path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    int next = openat((int)current.DangerousGetHandle(), part, NoFollow | DirectoryFlag, 0);
                    if (next < 0) throw new IOException("nofollow-directory-open-failed");
                    current.Dispose();
                    current = new SafeFileHandle((IntPtr)next, true);
                }
                return current;
            }
            catch { current.Dispose(); throw; }
        }

        internal void WriteNew(string name, byte[] bytes)
        {
            CheckName(name);
            if (_published || bytes == null || _written.Count >= 5 || bytes.Length > _maximumBytes - _bytes)
                throw new IOException("writer-file-or-byte-bound");
            ValidateAnchor();
            using SafeFileHandle handle = Create(name);
            using FileStream stream = new(handle, FileAccess.Write);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            _bytes += bytes.Length;
            _written.Add((name, (byte[])bytes.Clone()));
        }

        private SafeFileHandle Create(string name)
        {
            int fd = openat((int)_directory.DangerousGetHandle(), name, 1 | CreateExclusive | NoFollow, 0x180); // 0600
            if (fd < 0) throw new IOException("exclusive-nofollow-file-create-failed");
            SafeFileHandle handle = new((IntPtr)fd, true);
            // macOS arm64's variadic openat mode is not a reliable fixed P/Invoke
            // argument. The private 0700 directory protects the new empty file;
            // enforce fixed-signature 0600 before writing any captured bytes.
            if (fchmod(fd, 0x180) != 0)
            {
                handle.Dispose();
                throw new IOException("private-file-mode-enforcement-failed");
            }
            return handle;
        }

        internal void VerifyAll()
        {
            ValidateAnchor();
            foreach (var item in _written)
            {
                int fd = openat((int)_directory.DangerousGetHandle(), item.Name, NoFollow, 0);
                if (fd < 0) throw new IOException("stable-file-open-failed");
                using SafeFileHandle handle = new((IntPtr)fd, true);
                using FileStream stream = new(handle, FileAccess.Read);
                if (stream.Length != item.Bytes.Length) throw new IOException("stable-file-length-mismatch");
                byte[] actual = new byte[item.Bytes.Length];
                stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(item.Bytes)) throw new IOException("stable-file-byte-mismatch");
            }
        }

        internal void PublishManifest(string name, byte[] bytes)
        {
            CheckName(name);
            if (_published || bytes == null || bytes.Length > Stencil148InputCapture.MaximumMetadataBytes || bytes.Length > _maximumBytes - _bytes)
                throw new IOException("manifest-byte-bound-or-already-published");
            VerifyAll();
            string temporary = ".capture-manifest.pending";
            using (SafeFileHandle handle = Create(temporary))
            using (FileStream stream = new(handle, FileAccess.Write))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Read the fully flushed manifest before its atomic exclusive link.
            int fd = openat((int)_directory.DangerousGetHandle(), temporary, NoFollow, 0);
            if (fd < 0) throw new IOException("pending-manifest-open-failed");
            using (SafeFileHandle handle = new((IntPtr)fd, true))
            using (FileStream stream = new(handle, FileAccess.Read))
            {
                if (stream.Length != bytes.Length) throw new IOException("pending-manifest-length-mismatch");
                byte[] actual = new byte[bytes.Length]; stream.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(bytes)) throw new IOException("pending-manifest-byte-mismatch");
            }
            VerifyAll();
            ValidateAnchor();
            if (linkat((int)_directory.DangerousGetHandle(), temporary, (int)_directory.DangerousGetHandle(), name, 0) != 0)
                throw new IOException("exclusive-manifest-publication-failed");
            _published = true;
            // Only the owned temporary alias is removed; never any input/prior artifact.
            unlinkat((int)_directory.DangerousGetHandle(), temporary, 0);
        }

        private void ValidateAnchor()
        {
            ValidateAncestry(_path);
            UnixFileMode mode = File.GetUnixFileMode(_path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("output-directory-must-remain-private-mode-0700");
            using SafeFileHandle current = OpenDirectory(_path);
            byte[] heldStat = new byte[256], currentStat = new byte[256];
            if (fstat((int)_directory.DangerousGetHandle(), heldStat) != 0 || fstat((int)current.DangerousGetHandle(), currentStat) != 0)
                throw new IOException("output-directory-identity-query-failed");
            // Darwin arm64: dev_t at 0 (4 bytes), ino_t at 8 (8 bytes).
            // Linux 64-bit: dev_t at 0 (8 bytes), ino_t at 8 (8 bytes).
            int deviceBytes = OperatingSystem.IsMacOS() ? 4 : 8;
            if (!heldStat.AsSpan(0, deviceBytes).SequenceEqual(currentStat.AsSpan(0, deviceBytes)) ||
                !heldStat.AsSpan(8, 8).SequenceEqual(currentStat.AsSpan(8, 8)))
                throw new IOException("output-directory-anchor-changed");
        }

        private static void CheckName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 96 || Path.GetFileName(name) != name || name is "." or ".." || name.Contains('\0'))
                throw new IOException("writer-relative-file-name-invalid");
        }
        public void Dispose() => _directory.Dispose();
    }
}
