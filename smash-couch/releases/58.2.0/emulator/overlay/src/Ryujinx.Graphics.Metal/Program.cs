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
using System.Threading;
using System.Threading.Tasks;

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
        private volatile ProgramLinkStatus _status;
        // Boot-cached programs compile on ProgramCompileWorkers; null when the
        // constructor compiled synchronously. Every native use waits first.
        private Task _compile;

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
        // Original sources for a point variant; compacted once compiled.
        private readonly TransportSourceArchive _transportSources;
        private readonly ResourceLayout _transportLayout;
        private readonly DepthSnapshotShaderPolicy _transportSnapshotPolicy;
        private readonly object _variantGate = new();
        private Program _pointVariant;
        private bool _disposed;
        private readonly string _recipeGroupKey;
        private string _recipeProgramKey;
        private byte _recipeKind;

        private HashTableSlim<PipelineUid, MTLRenderPipelineState> _graphicsPipelineCache;
        private MTLComputePipelineState? _computePipelineCache;
        private bool _firstBackgroundUse;

        // A mid-game program's predicted first-draw pipeline (SpeculativePipelines).
        // The compile work item creates it before the status is published, so the
        // render thread reads it only after it saw the status. A speculative
        // builder thread may start the build only while it is still Queued; the
        // render thread or Dispose may cancel it only then. Whoever leaves the
        // build (builder when done, canceller otherwise) completes Built, and
        // Pipeline is read only after Built completed.
        private sealed class Speculation
        {
            internal const int Queued = 0, Building = 1, Done = 2, Cancelled = 3;

            internal PipelineState Request;
            internal PipelineUid PredictedKey;
            internal bool PredictionPending = true;
            internal bool Offered;
            internal int State;
            internal readonly TaskCompletionSource Built = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal MTLRenderPipelineState Pipeline;

            // Not offered (shadow mode): prediction counters only, nothing to build.
            internal Speculation(PipelineState request, bool offered)
            {
                Request = request;
                PredictedKey = request.Internal;
                Offered = offered;
                State = offered ? Queued : Cancelled;
                if (!offered)
                {
                    Built.TrySetResult();
                }
            }

            internal bool TryCancel()
            {
                if (Interlocked.CompareExchange(ref State, Cancelled, Queued) != Queued)
                {
                    return false;
                }

                Built.TrySetResult();
                return true;
            }
        }

        private Speculation _speculation;

        // Set while recorded recipes are rebuilt, so those rebuilds are not counted as already-known recipes.
        private bool _restoringRecipes;

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
            bool restoreRecipes = false,
            ProgramPipelineState? firstDrawState = null)
            : this(renderer, device, shaders, resourceLayout, computeLocalSize,
                   DepthSnapshotShaderPolicy.ReadOnlyFamily, null, restoreRecipes, authenticCapturePublicRoute: true,
                   firstDrawState: firstDrawState)
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
            bool authenticCapturePublicRoute = false,
            ProgramPipelineState? firstDrawState = null)
        {
            if (shaders.Length == 0)
                throw new ArgumentException("A Metal program requires at least one shader.", nameof(shaders));

            _renderer = renderer;
            _transportRoot = transportRoot ?? this;
            _transportDevice = device;
            _transportSnapshotPolicy = snapshotPolicy;
            UsesPointTransport = transportRoot != null;
            ComputeLocalSize = computeLocalSize;
            var creation = renderer.Programs.BeginCreate() ?? throw new ObjectDisposedException(nameof(MetalRenderer));
            bool creationTransferred = false;
            try
            {
            (BindingSegments, ArgumentBufferSizes, FragArgumentBufferSizes) = BuildBindingSegments(resourceLayout.SetUsages);
            using NSAutoreleasePool pool = new();

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
                        _transportSources = new TransportSourceArchive(originals);
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
                if (renderer.Recipes.Enabled)
                {
                    bool compute = Array.Exists(shaders, shader => shader.Stage == ShaderStage.Compute);
                    _recipeKind = compute ? (byte)3
                        : UsesPointTransport ? (byte)2 : UsesPerVertexTransport ? (byte)1 : (byte)0;
                    _recipeProgramKey = PipelineRecipeIdentity.Program(_recipeGroupKey, shaders,
                        _recipeKind, (ulong)ShaderLanguageVersion, FeedbackPreserveInvariance, false);
                }
            }
            catch (ShaderCompilationException exception)
            {
                _status = ProgramLinkStatus.Failure;
                Logger.Error?.Print(LogClass.Gpu, exception.Message);
            }

            // Guest programs compile on the bounded workers. At boot the disk
            // cache loader keeps several in flight and waits through
            // CheckProgramLink(true). Mid-game, the threaded renderer's
            // ProgramQueue starts later-queued programs while SetProgram waits
            // for an earlier one, so a burst of new shaders compiles in
            // parallel instead of one after another on the render thread.
            // Helpers and transport variants stay synchronous.
            if (_status != ProgramLinkStatus.Failure && transportRoot == null &&
                !renderer.InitializingRecipes && ProgramCompileWorkers.Enabled)
            {
                ShaderSource[] compileSources = shaders;
                ProgramLinkStatus prior = _status;
                _status = ProgramLinkStatus.Incomplete;
                // Only mid-game programs are speculated (SpeculativePipelines).
                ProgramPipelineState? speculativeState =
                    !restoreRecipes && SpeculativePipelines.Enabled ? firstDrawState : null;
                // _compile completes once CompileRegisterRestore has published
                // the status (faulted if it threw). It is assigned before the
                // work is queued, so no thread can see the program without it.
                TaskCompletionSource compiled = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _compile = compiled.Task;
                try
                {
                    ProgramCompileWorkers.Run(() =>
                    {
                        bool speculate;
                        try
                        {
                            speculate = CompileRegisterRestore(device, compileSources, creation, restoreRecipes, background: true,
                                speculativeState);
                        }
                        catch (Exception exception)
                        {
                            // EnsureCompiled observes and reports it.
                            compiled.TrySetException(exception);
                            return;
                        }
                        compiled.TrySetResult();
                        // The build runs on the speculative builders, never on
                        // the compile workers, so it cannot delay a compile.
                        if (speculate) QueueSpeculativeBuild(device);
                        // After publishing: compacting adds nothing to a wait.
                        _transportSources?.Compact();
                    });
                    creationTransferred = true;
                    return;
                }
                catch (Exception exception)
                {
                    _compile = null;
                    _status = prior;
                    ProgramCompileWorkers.CountFailure();
                    Logger.Error?.Print(LogClass.Gpu, $"MetalProgramWorkers: could not queue background compile; compiling synchronously: {exception}");
                }
            }

            CompileRegisterRestore(device, shaders, creation, restoreRecipes, background: false);
            _transportSources?.Compact();
            }
            finally
            {
                if (!creationTransferred) creation.Dispose();
            }
        }

        // Returns true when the work item should build a speculative pipeline
        // after this method has published the status.
        private bool CompileRegisterRestore(MTLDevice device, ShaderSource[] shaders,
            ProgramCreationRegistry<Program>.CreationLease creation, bool restoreRecipes, bool background,
            ProgramPipelineState? speculativeState = null)
        {
            using NSAutoreleasePool pool = new();
            ProgramLinkStatus status = _status;
            bool speculate = false;
            try
            {
            if (status != ProgramLinkStatus.Failure)
            {
            HashSet<ShaderStage> stages = [];
            // Mid-game vertex+fragment programs: the render thread is waiting for
            // this program, so the fragment stage compiles beside the vertex stage
            // when a stage thread is idle (ProgramStageCompilers).
            Task<MTLFunction> fragmentStage = null;
            try
            {
                foreach (ShaderSource shader in shaders)
                {
                    if (!stages.Add(shader.Stage))
                        throw new ShaderCompilationException($"Duplicate Metal shader stage {shader.Stage}.");
                }
                if (background && !restoreRecipes && shaders.Length == 2 &&
                    shaders[0].Stage == ShaderStage.Vertex && shaders[1].Stage == ShaderStage.Fragment)
                {
                    ShaderSource fragmentSource = shaders[1];
                    fragmentStage = ProgramStageCompilers.TryStart(() => CompileStage(device, fragmentSource));
                }
                foreach (ShaderSource shader in shaders)
                {
                    MTLFunction function;
                    if (fragmentStage != null && shader.Stage == ShaderStage.Fragment)
                    {
                        Task<MTLFunction> stage = fragmentStage;
                        fragmentStage = null;
                        // Rethrows the stage's own exception, as if compiled here.
                        function = stage.GetAwaiter().GetResult();
                    }
                    else
                    {
                        function = CompileStage(device, shader);
                    }
                    switch (shader.Stage)
                    {
                        case ShaderStage.Compute: ComputeFunction = function; break;
                        case ShaderStage.Vertex: VertexFunction = function; break;
                        case ShaderStage.Fragment: FragmentFunction = function; break;
                    }
                }
                status = ProgramLinkStatus.Success;
            }
            catch (ShaderCompilationException exception)
            {
                // Existing IProgram callers inspect a terminal Failure and own
                // disposal; shader failures must not escape their compile queue.
                DisposeShaderFunctions();
                status = ProgramLinkStatus.Failure;
                Logger.Error?.Print(LogClass.Gpu, exception.Message);
            }
            catch (Exception exception) when (background)
            {
                status = ProgramLinkStatus.Failure;
                ProgramCompileWorkers.CountFailure();
                Logger.Error?.Print(LogClass.Gpu, $"Metal background program compile failed (program {_recipeProgramKey ?? "unkeyed"}): {exception}");
                DisposeShaderFunctions();
            }
            catch
            {
                DisposeShaderFunctions();
                throw;
            }
            finally
            {
                // The vertex stage failed first: the fragment stage still running
                // finishes before the program is published or disposed, unused.
                if (fragmentStage != null)
                {
                    try
                    {
                        MTLFunction unused = fragmentStage.GetAwaiter().GetResult();
                        if (unused.NativePtr != IntPtr.Zero) unused.Dispose();
                    }
                    catch (Exception)
                    {
                        // Its failure is moot; the program already failed.
                    }
                }
            }
            }

            if (!_renderer.Programs.Register(this, creation))
            {
                DisposeShaderFunctions();
                if (!background)
                    throw new InvalidOperationException("Metal program registration lost creation ownership.");
                Logger.Error?.Print(LogClass.Gpu, "Metal program registration lost creation ownership.");
                status = ProgramLinkStatus.Failure;
            }

            // The existing creation lease remains held through restoration.
            // Never bulk-compile historical variants for a newly created
            // mid-game program. Helpers initialize before gameplay.
            if (status == ProgramLinkStatus.Success && _renderer.Recipes.Enabled &&
                (restoreRecipes || _renderer.InitializingRecipes))
            {
                try
                {
                    RestoreRecipes(device);
                    if (UsesPerVertexTransport && !UsesPointTransport &&
                        _renderer.Recipes.HasGroup(_recipeGroupKey, 2))
                    {
                        try { ResolveForPrimitiveCore(MTLPrimitiveType.Point, true); }
                        catch (InvalidOperationException exception)
                        {
                            _renderer.RecipeSkipped();
                            Logger.Warning?.Print(LogClass.Gpu, "MetalPipelineRecipes: point restore skipped: " + exception.Message);
                        }
                    }
                }
                catch (Exception exception) when (background)
                {
                    status = ProgramLinkStatus.Failure;
                    ProgramCompileWorkers.CountFailure();
                    Logger.Error?.Print(LogClass.Gpu, $"Metal background recipe restore failed (program {_recipeProgramKey ?? "unkeyed"}): {exception}");
                    DisposeCore();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            // Mid-game programs on a worker, before the status is published:
            // predict the requesting draw's key (cheap). The pipeline itself is
            // built after publication on the speculative builders, so the render
            // thread's wait for this program never includes it.
            if (background && status == ProgramLinkStatus.Success && speculativeState.HasValue &&
                VertexFunction.NativePtr != IntPtr.Zero &&
                SpeculativePipelines.TryPredict(speculativeState.Value, UsesPerVertexTransport, out PipelineState request))
            {
                speculate = SpeculativePipelines.Mode == SpeculativePipelines.SpeculationMode.On;
                _speculation = new Speculation(request, speculate);
            }
            }
            finally
            {
                _status = status;
                if (background) creation.Dispose();
            }
            return speculate;
        }

        // Compile worker, after the status is published. If the builders cannot
        // start, the build is cancelled and a draw creates the pipeline normally.
        private void QueueSpeculativeBuild(MTLDevice device)
        {
            Speculation speculation = _speculation;
            if (!SpeculativePipelines.Queue(() => BuildSpeculativePipeline(device, speculation)))
            {
                speculation.TryCancel();
            }
        }

        // Speculative builder thread: builds the predicted pipeline detached from
        // _graphicsPipelineCache, which the render thread may be using, unless
        // the build was cancelled first.
        private void BuildSpeculativePipeline(MTLDevice device, Speculation speculation)
        {
            if (Interlocked.CompareExchange(ref speculation.State, Speculation.Building, Speculation.Queued) != Speculation.Queued)
            {
                return;
            }

            try
            {
                using NSAutoreleasePool pool = new();
                speculation.Pipeline = SpeculativePipelines.Build(device, this, ref speculation.Request);
            }
            finally
            {
                Volatile.Write(ref speculation.State, Speculation.Done);
                speculation.Built.TrySetResult();
            }
        }

        // Render thread, before each pipeline lookup for this program: the first
        // request is compared with the prediction (counters only). A build for a
        // wrong prediction that has not started yet is cancelled.
        internal void ObservePipelineRequest(ref PipelineUid key)
        {
            Speculation speculation = _speculation;
            if (speculation == null || !speculation.PredictionPending) return;
            speculation.PredictionPending = false;
            if (!SpeculativePipelines.ObserveFirstUse(ref speculation.PredictedKey, ref key) && speculation.TryCancel())
            {
                speculation.Offered = false;
                SpeculativePipelines.CountCancelled();
            }
        }

        // Render thread, on a pipeline cache miss for exactly the predicted key:
        // takes the speculative pipeline, waiting only if its build is already
        // running. A build that has not started is cancelled and the caller
        // creates the pipeline as without speculation. Taken at most once.
        private bool TakeSpeculativePipeline(ref PipelineUid key, out MTLRenderPipelineState pipeline)
        {
            pipeline = default;
            Speculation speculation = _speculation;
            if (speculation == null || !speculation.Offered || !key.Equals(ref speculation.PredictedKey))
            {
                return false;
            }

            speculation.Offered = false;
            if (speculation.TryCancel())
            {
                SpeculativePipelines.CountPreempted();
                return false;
            }

            SpeculativePipelines.WaitForBuild(speculation.Built.Task, counted: true);
            pipeline = speculation.Pipeline;
            speculation.Pipeline = default;
            if (pipeline.NativePtr == IntPtr.Zero)
            {
                // Build failed (counted there); the caller creates it normally.
                return false;
            }

            (_graphicsPipelineCache ??= new()).Add(ref key, pipeline);
            RecordRecipe(ref key, speculative: true);
            SpeculativePipelines.CountHit();
            return true;
        }

        // Waits for a boot-cached program's background compile. Called before
        // any use of its native functions, pipelines or variants.
        internal void EnsureCompiled()
        {
            Task compile = _compile;
            if (compile == null) return;
            if (!compile.IsCompleted) ProgramCompileWorkers.Wait(compile);
            if (compile.IsFaulted && _status != ProgramLinkStatus.Failure)
            {
                // CompileRegisterRestore publishes its status in a finally, so
                // this is only reachable if that publication itself failed.
                _status = ProgramLinkStatus.Failure;
                ProgramCompileWorkers.CountFailure();
                Logger.Error?.Print(LogClass.Gpu, $"Metal background program work faulted (program {_recipeProgramKey ?? "unkeyed"}): {compile.Exception?.InnerException}");
            }
        }

        private void RestoreRecipes(MTLDevice device)
        {
            _restoringRecipes = true;
            try
            {
                RestoreRecipesCore(device);
            }
            finally
            {
                _restoringRecipes = false;
            }
        }

        private void RestoreRecipesCore(MTLDevice device)
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
            if (!restoreRecipes) root.EnsureCompiled();
            lock (root._variantGate)
            {
                ObjectDisposedException.ThrowIf(root._disposed, root);
                // Restoration runs before the linked status is published; its
                // caller has already established a successful compile.
                if (root._status != ProgramLinkStatus.Success && !restoreRecipes)
                    throw new InvalidOperationException("Cannot select a variant of an unlinked Metal program.");
                if (!root.UsesPerVertexTransport) return root;
                EncoderStateManager.ValidateTransportTopology(true, primitiveType);
                if (primitiveType != MTLPrimitiveType.Point) return root;
                if (root._pointVariant != null)
                {
                    ObjectDisposedException.ThrowIf(root._pointVariant._disposed, root._pointVariant);
                    return root._pointVariant;
                }

                Program variant = new(root._renderer, root._transportDevice, root._transportSources.Expand(),
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
            if (blocking) EnsureCompiled();
            return _status;
        }

        public byte[] GetBinary()
        {
            return [];
        }

        public void AddGraphicsPipeline(ref PipelineUid key, MTLRenderPipelineState pipeline)
        {
            (_graphicsPipelineCache ??= new()).Add(ref key, pipeline);
            RecordRecipe(ref key, speculative: false);
        }

        private void RecordRecipe(ref PipelineUid key, bool speculative)
        {
            if (_renderer.Recipes.Enabled && _recipeProgramKey != null)
            {
                byte[] payload = PipelineRecipeCodec.Encode(key);
                if (PipelineRecipeCodec.TryDecode(payload, out _))
                {
                    // A recipe the store already held: a pipeline a mid-game program could have rebuilt from its
                    // recipes before the draw instead of at it.
                    _renderer.Recipes.Record(_recipeGroupKey, _recipeProgramKey, _recipeKind, payload, out bool alreadyHeld);
                    if (alreadyHeld && !_restoringRecipes)
                    {
                        _renderer.RecipeAlreadyKnown(speculative);
                    }
                }
                else _renderer.RecipeRejected();
            }
        }

        public void AddComputePipeline(MTLComputePipelineState pipeline)
        {
            _computePipelineCache = pipeline;
            if (_renderer.Recipes.Enabled && _recipeProgramKey != null && _recipeKind == 3)
                _renderer.Recipes.Record(_recipeGroupKey, _recipeProgramKey, 3, ReadOnlySpan<byte>.Empty, out _);
        }

        public bool TryGetGraphicsPipeline(ref PipelineUid key, out MTLRenderPipelineState pipeline)
        {
            if (_graphicsPipelineCache == null)
            {
                return TakeSpeculativePipeline(ref key, out pipeline);
            }

            if (!_graphicsPipelineCache.TryGetValue(ref key, out pipeline))
            {
                if (TakeSpeculativePipeline(ref key, out pipeline))
                {
                    return true;
                }

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
            EnsureCompiled();
            // A queued speculative build is cancelled; a running one must finish
            // before its pipeline can be disposed.
            Speculation speculation = _speculation;
            if (speculation != null && !speculation.TryCancel())
            {
                SpeculativePipelines.WaitForBuild(speculation.Built.Task, counted: false);
            }
            DisposeCore();
        }

        private void DisposeCore()
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
                if (_speculation != null && _speculation.Pipeline.NativePtr != IntPtr.Zero)
                {
                    // Built, but no draw requested its key.
                    _speculation.Pipeline.Dispose();
                    _speculation.Pipeline = default;
                    SpeculativePipelines.CountUnused();
                }
                DisposeShaderFunctions();
            }
        }

        // One stage: MSL source to its entry-point function. Runs on the compile
        // worker, or for a mid-game fragment stage on a stage thread; reads only
        // members fixed before the program was queued.
        private MTLFunction CompileStage(MTLDevice device, ShaderSource shader)
        {
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
            return function;
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
