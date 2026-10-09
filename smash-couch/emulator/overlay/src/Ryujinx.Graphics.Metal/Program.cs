using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Graphics.Metal
{
    // The public renderer uses a bounded read-only resource contract. Exact
    // shader identities remain an internal regression-fixture seam only; no
    // environment or game setting changes either admission policy.
    internal readonly record struct DepthSnapshotShaderPolicy(
        int VertexBytes, string VertexSha256, int FragmentBytes, string FragmentSha256)
    {
        internal bool UseReadOnlyFamily { get; init; }
        internal static DepthSnapshotShaderPolicy ReadOnlyFamily =>
            new() { UseReadOnlyFamily = true };

        internal static DepthSnapshotShaderPolicy Recovered => new(
            61549, "432784375F0E37A69FB8E60096601179B92F28D13DBD76D9D43941380BCFECC0",
            11690, "065A9F3BF00D66CBDCE86454AF2DE0E49CDE1CFF1728724CE614FD61A5075C1D");
    }

    [SupportedOSPlatform("macos")]
    class Program : IProgram
    {
        private readonly ProgramLinkStatus _status;

        private readonly MetalRenderer _renderer;

        public MTLFunction VertexFunction;
        public MTLFunction FragmentFunction;
        public MTLFunction ComputeFunction;
        public ComputeSize ComputeLocalSize { get; }
        // A transport variant is selected from the actual encoded primitive.
        internal bool UsesPerVertexTransport { get; }
        internal bool UsesPointTransport { get; }
        private readonly Program _transportRoot;
        private readonly MTLDevice _transportDevice;
        private readonly ShaderSource[] _transportSources;
        private readonly ResourceLayout _transportLayout;
        private readonly DepthSnapshotShaderPolicy _transportSnapshotPolicy;
        private readonly object _variantGate = new();
        private Program _pointVariant;
        private bool _disposed;
        private readonly string _recipeGroupKey;
        private readonly string _recipeProgramKey;
        private readonly byte _recipeKind;

        private HashTableSlim<PipelineUid, MTLRenderPipelineState> _graphicsPipelineCache;
        private MTLComputePipelineState? _computePipelineCache;
        private bool _firstBackgroundUse;

        public ResourceBindingSegment[][] BindingSegments { get; }
        // This is only the program-level resource contract. Exact layout and
        // read-only attachment/native-resource checks are repeated every draw.
        internal bool DepthSnapshotShaderAllowed { get; }
        internal DepthSnapshotResourceCardinalities DepthSnapshotResourceCounts { get; }
        internal ReadonlyStencil148ProgramWitness ReadonlyStencil148Witness { get; }
        internal string CubeDiagnosticIdentity { get; }
        internal readonly record struct FeedbackShaderSource(ShaderStage Stage, string Code);
        internal readonly record struct FeedbackResourceUsage(
            int Set, int Binding, int ArrayLength, ResourceType Type, ResourceStages Stages, bool Write);
        internal FeedbackShaderSource[] FeedbackShaderSources { get; }
        internal FeedbackResourceUsage[] FeedbackResourceUsages { get; }
        internal const bool FeedbackPreserveInvariance = true;
        internal const MTLLanguageVersion FeedbackLanguageVersion = MTLLanguageVersion.Version31;
        internal const MTLLanguageVersion TransportLanguageVersion = (MTLLanguageVersion)0x40000;
        internal MTLLanguageVersion ShaderLanguageVersion => UsesPerVertexTransport ? TransportLanguageVersion : FeedbackLanguageVersion;
        // Argument buffer sizes for Vertex or Compute stages
        public int[] ArgumentBufferSizes { get; }
        // Argument buffer sizes for Fragment stage
        public int[] FragArgumentBufferSizes { get; }

        // Correctness-first prototype: compilation is synchronous. The caller's
        // existing background work queue may still create programs off the draw
        // thread, but a returned Program has no pending native callbacks.
        public Program(
            MetalRenderer renderer,
            MTLDevice device,
            ShaderSource[] shaders,
            ResourceLayout resourceLayout,
            ComputeSize computeLocalSize = default,
            bool restoreRecipes = false)
            : this(renderer, device, shaders, resourceLayout, computeLocalSize,
                   DepthSnapshotShaderPolicy.ReadOnlyFamily, null, restoreRecipes, authenticCapturePublicRoute: true)
        {
        }

        internal Program(
            MetalRenderer renderer,
            MTLDevice device,
            ShaderSource[] shaders,
            ResourceLayout resourceLayout,
            ComputeSize computeLocalSize,
            DepthSnapshotShaderPolicy snapshotPolicy)
            : this(renderer, device, shaders, resourceLayout, computeLocalSize, snapshotPolicy, null)
        {
        }

        private Program(
            MetalRenderer renderer,
            MTLDevice device,
            ShaderSource[] shaders,
            ResourceLayout resourceLayout,
            ComputeSize computeLocalSize,
            DepthSnapshotShaderPolicy snapshotPolicy,
            Program transportRoot,
            bool restoreRecipes = false,
            bool authenticCapturePublicRoute = false)
        {
            if (shaders.Length == 0)
                throw new ArgumentException("A Metal program requires at least one shader.", nameof(shaders));

            _renderer = renderer;
            _transportRoot = transportRoot ?? this;
            _transportDevice = device;
            _transportSnapshotPolicy = snapshotPolicy;
            UsesPointTransport = transportRoot != null;
            ComputeLocalSize = computeLocalSize;
            using var creation = renderer.Programs.BeginCreate() ?? throw new ObjectDisposedException(nameof(MetalRenderer));
            (BindingSegments, ArgumentBufferSizes, FragArgumentBufferSizes) = BuildBindingSegments(resourceLayout.SetUsages);
            using NSAutoreleasePool pool = new();
            HashSet<ShaderStage> stages = [];

            try
            {
                // The inherited zero-replacement classifier is deliberately bypassed.
                // This source-paired transport preserves the ordinary finite path.
                string interfaceReceipt;
                ShaderSource[] originals;
                try
                {
                    originals = shaders;
                    if (renderer.Recipes.Enabled)
                        _recipeGroupKey = transportRoot?._recipeGroupKey ??
                            PipelineRecipeIdentity.Group(originals, resourceLayout, computeLocalSize);
                    bool usesTransport;
                    shaders = UsesPointTransport
                        ? PerVertexTransport.AdaptPoint(shaders, out usesTransport, out interfaceReceipt)
                        : PerVertexTransport.Adapt(shaders, out usesTransport, out interfaceReceipt);
                    UsesPerVertexTransport = usesTransport;
                    if (UsesPointTransport && !usesTransport)
                        throw new InvalidOperationException("Point variant requires authenticated transport sources.");
                    if (usesTransport && !UsesPointTransport)
                    {
                        _transportSources = (ShaderSource[])originals.Clone();
                        _transportLayout = CopyLayout(resourceLayout);
                    }
                }
                catch (InvalidOperationException exception)
                {
                    throw new ShaderCompilationException("Metal transport source refusal: " + exception.Message);
                }
                if (UsesPerVertexTransport && (!OperatingSystem.IsMacOSVersionAtLeast(27) ||
                    !device.SupportsFamily((MTLGPUFamily)1010) || !device.SupportsShaderBarycentricCoordinates))
                    throw new ShaderCompilationException("Per-vertex transport requires the validated macOS27 Apple10/barycentric device path.");
                if (interfaceReceipt != null)
                    Logger.Notice.Print(LogClass.Gpu, interfaceReceipt);
                DepthSnapshotShaderAllowed = snapshotPolicy.UseReadOnlyFamily
                    ? DepthSnapshotProgramPolicy.Allows(shaders, resourceLayout)
                    : MatchesDepthSnapshotShader(shaders, snapshotPolicy);
                DepthSnapshotResourceCounts = DepthSnapshotResourceCardinalities.Capture(resourceLayout);
                if (ReadonlyStencil148SnapshotPolicy.Enabled)
                    ReadonlyStencil148Witness = ReadonlyStencil148SnapshotPolicy.CreateWitness(
                        shaders, resourceLayout, snapshotPolicy.UseReadOnlyFamily,
                        DepthSnapshotShaderAllowed, UsesPerVertexTransport, UsesPointTransport);
                Stencil148InputCapture.Observe(authenticCapturePublicRoute, originals, shaders,
                    resourceLayout, computeLocalSize, UsesPerVertexTransport, UsesPointTransport,
                    interfaceReceipt, (ulong)ShaderLanguageVersion, FeedbackPreserveInvariance,
                    DepthSnapshotShaderAllowed);
                CubeDiagnosticIdentity = CubeBindingDiagnostics.RegisterProgram(shaders, resourceLayout);
                if (NativeFeedbackDiagnostics.Enabled || CubeBindingDiagnostics.Enabled)
                {
                    FeedbackShaderSources = new FeedbackShaderSource[shaders.Length];
                    for (int index = 0; index < shaders.Length; index++)
                    {
                        // Shader strings are immutable; retain only the existing references.
                        FeedbackShaderSources[index] = new FeedbackShaderSource(shaders[index].Stage, shaders[index].Code);
                    }

                    List<FeedbackResourceUsage> usages = [];
                    for (int set = 0; set < resourceLayout.SetUsages.Count; set++)
                    {
                        foreach (ResourceUsage usage in resourceLayout.SetUsages[set].Usages)
                        {
                            usages.Add(new FeedbackResourceUsage(
                                set, usage.Binding, usage.ArrayLength, usage.Type, usage.Stages, usage.Write));
                        }
                    }
                    FeedbackResourceUsages = usages.ToArray();
                }
                foreach (ShaderSource shader in shaders)
                {
                    if (!stages.Add(shader.Stage))
                        throw new ShaderCompilationException($"Duplicate Metal shader stage {shader.Stage}.");
                    string entry = shader.Stage switch
                    {
                        ShaderStage.Compute => "kernelMain",
                        ShaderStage.Vertex => "vertexMain",
                        ShaderStage.Fragment => "fragmentMain",
                        _ => throw new ShaderCompilationException($"Unsupported Metal shader stage {shader.Stage}."),
                    };
                    using MTLCompileOptions options = new()
                    {
                        PreserveInvariance = FeedbackPreserveInvariance,
                        LanguageVersion = ShaderLanguageVersion,
                        // Guest floating-point instructions may produce zero,
                        // infinity or NaN. Do not assume finite-only arithmetic.
                        FastMathEnabled = false,
                    };
                    NSError error = default;
                    MTLLibrary compiledLibrary;
                    using (NativeTimingDiagnostics.Measure(NativeTimingKind.ShaderLibrary))
                    {
                        compiledLibrary = device.NewLibrary(StringHelper.NSString(shader.Code), options, ref error);
                    }
                    using MTLLibrary library = compiledLibrary;
                    if (library.NativePtr == IntPtr.Zero)
                    {
                        string details = ErrorDescription(error);
                        throw new ShaderCompilationException($"{shader.Stage} Metal compilation failed: {details}");
                    }
                    if (error != IntPtr.Zero)
                        Logger.Warning?.Print(LogClass.Gpu, $"{shader.Stage} Metal compiler message: {ErrorDescription(error)}");

                    MTLFunction function = library.NewFunction(StringHelper.NSString(entry));
                    if (function.NativePtr == IntPtr.Zero)
                        throw new ShaderCompilationException($"Metal entry point {entry} is missing.");
                    switch (shader.Stage)
                    {
                        case ShaderStage.Compute: ComputeFunction = function; break;
                        case ShaderStage.Vertex: VertexFunction = function; break;
                        case ShaderStage.Fragment: FragmentFunction = function; break;
                    }
                }
                _status = ProgramLinkStatus.Success;
                if (renderer.Recipes.Enabled)
                {
                    _recipeKind = ComputeFunction.NativePtr != IntPtr.Zero ? (byte)3
                        : UsesPointTransport ? (byte)2 : UsesPerVertexTransport ? (byte)1 : (byte)0;
                    _recipeProgramKey = PipelineRecipeIdentity.Program(_recipeGroupKey, shaders,
                        _recipeKind, (ulong)ShaderLanguageVersion, FeedbackPreserveInvariance, false);
                }
            }
            catch (ShaderCompilationException exception)
            {
                // Existing IProgram callers inspect a terminal Failure and own
                // disposal; shader failures must not escape their compile queue.
                DisposeShaderFunctions();
                _status = ProgramLinkStatus.Failure;
                Logger.Error?.Print(LogClass.Gpu, exception.Message);
            }
            catch
            {
                DisposeShaderFunctions();
                throw;
            }

            if (!renderer.Programs.Register(this, creation))
            {
                DisposeShaderFunctions();
                throw new InvalidOperationException("Metal program registration lost creation ownership.");
            }

            // The existing creation lease remains held through synchronous
            // restoration. Never bulk-compile historical variants for a newly
            // created mid-game program. Helpers initialize before gameplay.
            if (_status == ProgramLinkStatus.Success && renderer.Recipes.Enabled &&
                (restoreRecipes || renderer.InitializingRecipes))
            {
                try
                {
                    RestoreRecipes(device);
                    if (UsesPerVertexTransport && !UsesPointTransport &&
                        renderer.Recipes.HasGroup(_recipeGroupKey, 2))
                    {
                        try { ResolveForPrimitiveCore(MTLPrimitiveType.Point, true); }
                        catch (InvalidOperationException exception)
                        {
                            renderer.RecipeSkipped();
                            Logger.Warning?.Print(LogClass.Gpu, "MetalPipelineRecipes: point restore skipped: " + exception.Message);
                        }
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
        }

        private void RestoreRecipes(MTLDevice device)
        {
            foreach (byte[] payload in _renderer.Recipes.Find(_recipeProgramKey, _recipeKind))
            {
                try
                {
                    if (_recipeKind == 3)
                    {
                        if (payload.Length != 0) { _renderer.RecipeSkipped(); continue; }
                        PipelineState.CreateComputePipeline(device, this);
                    }
                    else
                    {
                        if (!PipelineRecipeCodec.TryDecode(payload, out PipelineState state) ||
                            state.InputPrimitiveTopologyClass != (_recipeKind == 2 ? MTLPrimitiveTopologyClass.Point
                                : _recipeKind == 1 ? MTLPrimitiveTopologyClass.Triangle : MTLPrimitiveTopologyClass.Unspecified))
                        {
                            _renderer.RecipeSkipped();
                            continue;
                        }
                        state.CreateRenderPipeline(device, this);
                    }
                    _renderer.RecipeRestored();
                }
                catch (InvalidOperationException exception)
                {
                    // Failed native PSOs are never inserted; retain the normal
                    // first-draw/dispatch path for this exact program.
                    _renderer.RecipeSkipped();
                    Logger.Warning?.Print(LogClass.Gpu, "MetalPipelineRecipes: restore skipped: " + exception.Message);
                }
            }
        }

        private static ResourceLayout CopyLayout(ResourceLayout layout)
        {
            var sets = new ResourceDescriptorCollection[layout.Sets.Count];
            for (int index = 0; index < sets.Length; index++)
            {
                var values = new ResourceDescriptor[layout.Sets[index].Descriptors.Count];
                layout.Sets[index].Descriptors.CopyTo(values, 0);
                sets[index] = new(new ReadOnlyCollection<ResourceDescriptor>(values));
            }
            var usages = new ResourceUsageCollection[layout.SetUsages.Count];
            for (int index = 0; index < usages.Length; index++)
            {
                var values = new ResourceUsage[layout.SetUsages[index].Usages.Count];
                layout.SetUsages[index].Usages.CopyTo(values, 0);
                usages[index] = new(new ReadOnlyCollection<ResourceUsage>(values));
            }
            return new(new ReadOnlyCollection<ResourceDescriptorCollection>(sets),
                new ReadOnlyCollection<ResourceUsageCollection>(usages));
        }

        internal Program ResolveForPrimitive(MTLPrimitiveType primitiveType) => ResolveForPrimitiveCore(primitiveType, false);

        private Program ResolveForPrimitiveCore(MTLPrimitiveType primitiveType, bool restoreRecipes)
        {
            Program root = _transportRoot;
            lock (root._variantGate)
            {
                ObjectDisposedException.ThrowIf(root._disposed, root);
                if (root._status != ProgramLinkStatus.Success)
                    throw new InvalidOperationException("Cannot select a variant of an unlinked Metal program.");
                if (!root.UsesPerVertexTransport) return root;
                EncoderStateManager.ValidateTransportTopology(true, primitiveType);
                if (primitiveType != MTLPrimitiveType.Point) return root;
                if (root._pointVariant != null)
                {
                    ObjectDisposedException.ThrowIf(root._pointVariant._disposed, root._pointVariant);
                    return root._pointVariant;
                }

                Program variant = new(root._renderer, root._transportDevice, root._transportSources,
                    root._transportLayout, root.ComputeLocalSize, root._transportSnapshotPolicy, root, restoreRecipes);
                if (variant._status != ProgramLinkStatus.Success)
                {
                    variant.Dispose();
                    throw new InvalidOperationException("Point transport shader compilation failed; draw refused.");
                }
                root._pointVariant = variant;
                return variant;
            }
        }

        private static bool MatchesDepthSnapshotShader(ShaderSource[] shaders,
            DepthSnapshotShaderPolicy policy)
        {
            if (shaders.Length != 2 ||
                shaders[0].Stage != ShaderStage.Vertex || shaders[1].Stage != ShaderStage.Fragment ||
                shaders[0].Code == null || shaders[1].Code == null ||
                policy.VertexBytes <= 0 || policy.FragmentBytes <= 0 ||
                Encoding.UTF8.GetByteCount(shaders[0].Code) != policy.VertexBytes ||
                Encoding.UTF8.GetByteCount(shaders[1].Code) != policy.FragmentBytes)
            {
                return false;
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shaders[0].Code))) ==
                   policy.VertexSha256 &&
                   Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shaders[1].Code))) ==
                   policy.FragmentSha256;
        }

        private sealed class ShaderCompilationException(string message) : Exception(message);

        private static string ErrorDescription(NSError error)
        {
            if (error == IntPtr.Zero) return "Metal returned no library and no error description.";
            // The pinned binding exposes NSString.Utf8String as UInt16. Use the
            // correctly pointer-sized objc message and copy while the pool lives.
            IntPtr utf8 = ObjectiveC.IntPtr_objc_msgSend(error.LocalizedDescription.NativePtr, "UTF8String");
            return Marshal.PtrToStringUTF8(utf8) ?? "No Metal error description.";
        }

        private static (ResourceBindingSegment[][], int[], int[]) BuildBindingSegments(ReadOnlyCollection<ResourceUsageCollection> setUsages)
        {
            ResourceBindingSegment[][] segments = new ResourceBindingSegment[setUsages.Count][];
            int[] argBufferSizes = new int[setUsages.Count];
            int[] fragArgBufferSizes = new int[setUsages.Count];

            for (int setIndex = 0; setIndex < setUsages.Count; setIndex++)
            {
                List<ResourceBindingSegment> currentSegments = [];

                ResourceUsage currentUsage = default;
                int currentCount = 0;

                for (int index = 0; index < setUsages[setIndex].Usages.Count; index++)
                {
                    ResourceUsage usage = setUsages[setIndex].Usages[index];

                    if (currentUsage.Binding + currentCount != usage.Binding ||
                        currentUsage.Type != usage.Type ||
                        currentUsage.Stages != usage.Stages ||
                        currentUsage.ArrayLength > 1 ||
                        usage.ArrayLength > 1)
                    {
                        if (currentCount != 0)
                        {
                            currentSegments.Add(new ResourceBindingSegment(
                                currentUsage.Binding,
                                currentCount,
                                currentUsage.Type,
                                currentUsage.Stages,
                                currentUsage.ArrayLength > 1));

                            int size = currentCount * ResourcePointerSize(currentUsage.Type);
                            if (currentUsage.Stages.HasFlag(ResourceStages.Fragment))
                            {
                                fragArgBufferSizes[setIndex] += size;
                            }

                            if (currentUsage.Stages.HasFlag(ResourceStages.Vertex) ||
                                currentUsage.Stages.HasFlag(ResourceStages.Compute))
                            {
                                argBufferSizes[setIndex] += size;
                            }
                        }

                        currentUsage = usage;
                        currentCount = usage.ArrayLength;
                    }
                    else
                    {
                        currentCount++;
                    }
                }

                if (currentCount != 0)
                {
                    currentSegments.Add(new ResourceBindingSegment(
                        currentUsage.Binding,
                        currentCount,
                        currentUsage.Type,
                        currentUsage.Stages,
                        currentUsage.ArrayLength > 1));

                    int size = currentCount * ResourcePointerSize(currentUsage.Type);
                    if (currentUsage.Stages.HasFlag(ResourceStages.Fragment))
                    {
                        fragArgBufferSizes[setIndex] += size;
                    }

                    if (currentUsage.Stages.HasFlag(ResourceStages.Vertex) ||
                        currentUsage.Stages.HasFlag(ResourceStages.Compute))
                    {
                        argBufferSizes[setIndex] += size;
                    }
                }

                segments[setIndex] = currentSegments.ToArray();
            }

            return (segments, argBufferSizes, fragArgBufferSizes);
        }

        private static int ResourcePointerSize(ResourceType type)
        {
            return (type == ResourceType.TextureAndSampler ? 2 : 1);
        }

        public ProgramLinkStatus CheckProgramLink(bool blocking)
        {
            // A successfully constructed program is already linked.
            return _status;
        }

        public byte[] GetBinary()
        {
            return [];
        }

        public void AddGraphicsPipeline(ref PipelineUid key, MTLRenderPipelineState pipeline)
        {
            (_graphicsPipelineCache ??= new()).Add(ref key, pipeline);
            if (_renderer.Recipes.Enabled && _recipeProgramKey != null)
            {
                byte[] payload = PipelineRecipeCodec.Encode(key);
                if (PipelineRecipeCodec.TryDecode(payload, out _))
                    _renderer.Recipes.Record(_recipeGroupKey, _recipeProgramKey, _recipeKind, payload);
                else _renderer.RecipeRejected();
            }
        }

        public void AddComputePipeline(MTLComputePipelineState pipeline)
        {
            _computePipelineCache = pipeline;
            if (_renderer.Recipes.Enabled && _recipeProgramKey != null && _recipeKind == 3)
                _renderer.Recipes.Record(_recipeGroupKey, _recipeProgramKey, 3, ReadOnlySpan<byte>.Empty);
        }

        public bool TryGetGraphicsPipeline(ref PipelineUid key, out MTLRenderPipelineState pipeline)
        {
            if (_graphicsPipelineCache == null)
            {
                pipeline = default;
                return false;
            }

            if (!_graphicsPipelineCache.TryGetValue(ref key, out pipeline))
            {
                if (_firstBackgroundUse)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "Background pipeline compile missed on draw - incorrect pipeline state?");
                    _firstBackgroundUse = false;
                }

                return false;
            }

            _firstBackgroundUse = false;

            return true;
        }

        public bool TryGetComputePipeline(out MTLComputePipelineState pipeline)
        {
            if (_computePipelineCache.HasValue)
            {
                pipeline = _computePipelineCache.Value;
                return true;
            }

            pipeline = default;
            return false;
        }

        public void Dispose()
        {
            lock (_variantGate)
            {
                if (_disposed || !_renderer.Programs.Remove(this)) return;
                _disposed = true;
                _pointVariant?.Dispose();
                _pointVariant = null;
                if (_graphicsPipelineCache != null)
                {
                    foreach (MTLRenderPipelineState pipeline in _graphicsPipelineCache.Values)
                    {
                        pipeline.Dispose();
                    }
                }
                _computePipelineCache?.Dispose();
                DisposeShaderFunctions();
            }
        }

        private void DisposeShaderFunctions()
        {
            if (VertexFunction.NativePtr != IntPtr.Zero) VertexFunction.Dispose();
            if (FragmentFunction.NativePtr != IntPtr.Zero) FragmentFunction.Dispose();
            if (ComputeFunction.NativePtr != IntPtr.Zero) ComputeFunction.Dispose();
            VertexFunction = default;
            FragmentFunction = default;
            ComputeFunction = default;
        }
    }
}
