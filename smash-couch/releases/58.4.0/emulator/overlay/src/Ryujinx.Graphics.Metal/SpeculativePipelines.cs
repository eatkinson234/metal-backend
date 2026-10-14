using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Metal
{
    // Speculative first-draw pipelines for mid-game guest programs.
    //
    // A program's first draw builds its PipelineUid from the GAL Set* calls and
    // creates that pipeline synchronously on the render thread (about 25 ms for
    // a new variant). The GPU layer passes the requesting draw's pipeline state
    // to CreateProgram (ShaderInfo.State, the state Vulkan uses for its
    // background pipeline); converted exactly as EncoderStateManager converts
    // the matching Set* calls, it predicts that key.
    //
    // The compile worker predicts before it publishes the program and then
    // queues the build on separate builder threads, so a build never delays a
    // shader compile. The pipeline is built detached from the program's cache.
    // When the render thread requests exactly the predicted key it takes the
    // pipeline, waiting only for a build that is already running; a build that
    // has not started is cancelled and the render thread creates the pipeline
    // as it would without speculation. Any other key never waits, and a wrong
    // first-use prediction cancels a build that has not started. A burst of new
    // programs gets its first pipelines built in parallel instead of one after
    // another on the render thread. Boot programs are not speculated; their
    // recorded pipelines come from recipes.
    //
    // SMASH_COUCH_SPECULATIVE_PSO: on (default) | shadow (predict and compare
    // only) | off.
    [SupportedOSPlatform("macos")]
    static class SpeculativePipelines
    {
        internal enum SpeculationMode { Off, Shadow, On }

        internal static readonly SpeculationMode Mode = ReadMode();

        // Key regions compared at a program's first pipeline request.
        private enum Field { Header, ColorFormat, Blend, WriteMask, VertexAttribute, VertexLayout, Count }

        private const int MaxDetailLogs = 24;
        private const int MaxFailureLogs = 8;

        private static readonly int BuilderCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
        private static readonly BlockingCollection<Action> _builds = new();
        private static readonly object _startGate = new();
        private static volatile bool _started;
        private static int _running;

        private static long _offered, _unpredictable, _built, _failed, _hits, _unused, _preempted, _cancelled;
        private static long _buildTicks, _maxBuildTicks;
        private static long _waits, _waitTicks, _maxWaitTicks;
        private static long _firstUseMatches, _firstUseMismatches;
        private static readonly long[] _mismatchFields = new long[(int)Field.Count];
        private static int _detailLogs;
        private static int _failureLogs;
        private static string _lastSummary;

        private static SpeculationMode ReadMode()
        {
            return Environment.GetEnvironmentVariable("SMASH_COUCH_SPECULATIVE_PSO")?.Trim().ToLowerInvariant() switch
            {
                "off" or "0" => SpeculationMode.Off,
                "shadow" => SpeculationMode.Shadow,
                _ => SpeculationMode.On,
            };
        }

        internal static bool Enabled => Mode != SpeculationMode.Off;

        private static bool IsTriangleTopology(PrimitiveTopology topology)
        {
            return topology is PrimitiveTopology.Triangles or PrimitiveTopology.TriangleStrip or
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Quads or PrimitiveTopology.QuadStrip or
                PrimitiveTopology.Polygon;
        }

        // Mirrors EncoderStateManager.UpdateRenderTargetColorMasks.
        private static MTLColorWriteMask WriteMask(uint componentMask)
        {
            MTLColorWriteMask mask = MTLColorWriteMask.None;
            mask |= (componentMask & (0x1 << 0)) != 0 ? MTLColorWriteMask.Red : 0;
            mask |= (componentMask & (0x1 << 1)) != 0 ? MTLColorWriteMask.Green : 0;
            mask |= (componentMask & (0x1 << 2)) != 0 ? MTLColorWriteMask.Blue : 0;
            mask |= (componentMask & (0x1 << 3)) != 0 ? MTLColorWriteMask.Alpha : 0;
            return mask;
        }

        // Worker, before the program is published. Never throws: a failed
        // prediction is counted and the program is used as without speculation.
        internal static bool TryPredict(ProgramPipelineState gal, bool usesPerVertexTransport, out PipelineState state)
        {
            Interlocked.Increment(ref _offered);
            try
            {
                if (Predict(gal, usesPerVertexTransport, out state))
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                state = default;
                LogFailure("prediction", exception);
            }

            Interlocked.Increment(ref _unpredictable);
            return false;
        }

        // Predicts the render thread's PipelineState for a draw with this GAL state.
        // Each part mirrors the EncoderStateManager update named beside it, applied to
        // the state EncoderState starts from (PipelineState.Initialize).
        private static bool Predict(ProgramPipelineState gal, bool usesPerVertexTransport, out PipelineState state)
        {
            state = new PipelineState();
            state.Initialize();

            // UpdatePipelineForPrimitive: only transport programs carry a class. Their
            // point and line draws use other program variants or are refused.
            if (usesPerVertexTransport)
            {
                if (!IsTriangleTopology(gal.Topology))
                {
                    return false;
                }

                state.InputPrimitiveTopologyClass = MTLPrimitiveTopologyClass.Triangle;
            }

            // UpdateRasterizerDiscard, UpdateLogicOpState. Alpha-to-coverage and
            // alpha-to-one (UpdateMultisampleState) are not in the GAL pipeline
            // state; they are predicted off.
            state.RasterizerDiscardEnable = gal.RasterizerDiscard;
            state.LogicOpEnable = gal.LogicOpEnable;
            state.LogicOp = gal.LogicOp.Convert();

            // UpdateRenderTargetsInternal: the texture manager always passes all
            // color slots; formats come from FormatTable like Texture creation.
            // UpdateRenderTargetColorMasks / UpdateBlendDescriptors: a zero write
            // mask swaps the blend state out, keeping only the pixel format.
            state.ColorBlendAttachmentStateCount = Constants.MaxColorAttachments;
            for (int index = 0; index < Constants.MaxColorAttachments; index++)
            {
                ref ColorBlendStateUid blend = ref state.Internal.ColorBlendState[index];
                MTLColorWriteMask mask = WriteMask(gal.ColorWriteMask[index]);
                blend = new ColorBlendStateUid();
                if (mask != MTLColorWriteMask.None)
                {
                    BlendDescriptor descriptor = gal.BlendDescriptors[index];
                    blend.Enable = descriptor.Enable;
                    blend.AlphaBlendOperation = descriptor.AlphaOp.Convert();
                    blend.RgbBlendOperation = descriptor.ColorOp.Convert();
                    blend.SourceAlphaBlendFactor = descriptor.AlphaSrcFactor.Convert();
                    blend.DestinationAlphaBlendFactor = descriptor.AlphaDstFactor.Convert();
                    blend.SourceRGBBlendFactor = descriptor.ColorSrcFactor.Convert();
                    blend.DestinationRGBBlendFactor = descriptor.ColorDstFactor.Convert();
                }

                blend.WriteMask = mask;
                blend.PixelFormat = gal.AttachmentEnable[index]
                    ? FormatTable.GetFormat(gal.AttachmentFormats[index])
                    : MTLPixelFormat.Invalid;
            }

            state.DepthStencilFormat = gal.DepthStencilEnable
                ? FormatTable.GetFormat(gal.DepthStencilFormat)
                : MTLPixelFormat.Invalid;

            // UpdateVertexAttribs / UpdateVertexBuffers: the GPU layer always sends
            // all of its attributes; unused renderer slots keep their defaults.
            // Unmapped or disabled buffers reach the renderer with stride 0.
            VertexAttribDescriptor[] attribs = new VertexAttribDescriptor[Constants.MaxVertexAttributes];
            int attribCount = Math.Clamp(gal.VertexAttribCount, 0, attribs.Length);
            gal.VertexAttribs.AsSpan()[..attribCount].CopyTo(attribs);

            VertexBufferState[] buffers = new VertexBufferState[Constants.MaxVertexBuffers];
            int bufferCount = Math.Clamp(gal.VertexBufferCount, 0, buffers.Length);
            for (int index = 0; index < buffers.Length; index++)
            {
                BufferPipelineDescriptor vb = index < bufferCount ? gal.VertexBuffers[index] : default;
                buffers[index] = vb.Enable
                    ? new VertexBufferState(BufferHandle.Null, 0, 0, vb.Divisor, vb.Stride)
                    : new VertexBufferState(BufferHandle.Null, 0, 0, 0, 0);
            }

            EncoderStateManager.ApplyVertexState(ref state, buffers, attribs);

            return true;
        }

        // Compile worker, after the program is published. False when the builder
        // threads could not be started; the caller then cancels the build.
        internal static bool Queue(Action build)
        {
            if (!_started)
            {
                lock (_startGate)
                {
                    if (!_started)
                    {
                        try
                        {
                            // Builders that did start stay counted: a later attempt starts only the missing ones.
                            while (_running < BuilderCount)
                            {
                                new Thread(Work) { Name = $"Metal.SpeculativeBuild.{_running}", IsBackground = true }.Start();
                                _running++;
                            }
                        }
                        catch (Exception exception)
                        {
                            LogFailure("builder start", exception);
                            return false;
                        }

                        _started = true;
                        Logger.Info?.Print(LogClass.Gpu, $"MetalSpeculativePipelines: started {BuilderCount} builder threads");
                    }
                }
            }

            _builds.Add(build);
            return true;
        }

        private static void Work()
        {
            foreach (Action build in _builds.GetConsumingEnumerable())
            {
                try
                {
                    build();
                }
                catch (Exception exception)
                {
                    // Builds complete their own state in a finally; never lose a builder.
                    LogFailure("builder", exception);
                }
            }
        }

        // Builder thread. Never throws; returns default when creation failed
        // (counted and logged), and the draw then creates the pipeline normally.
        internal static MTLRenderPipelineState Build(MTLDevice device, Program program, ref PipelineState state)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                MTLRenderPipelineState pipeline = state.CreateDetachedRenderPipeline(device, program);
                Interlocked.Increment(ref _built);
                return pipeline;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failed);
                LogFailure("build", exception);
                return default;
            }
            finally
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                Interlocked.Add(ref _buildTicks, ticks);
                UpdateMax(ref _maxBuildTicks, ticks);
            }
        }

        // Waits for a running build: from the render thread before it takes the
        // pipeline (counted), or from Dispose (not counted).
        internal static void WaitForBuild(Task work, bool counted)
        {
            if (work == null || work.IsCompleted)
            {
                return;
            }

            long start = Stopwatch.GetTimestamp();
            try
            {
                work.Wait();
            }
            catch (AggregateException exception)
            {
                LogFailure("program work", exception.InnerException ?? exception);
            }

            if (counted)
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                Interlocked.Increment(ref _waits);
                Interlocked.Add(ref _waitTicks, ticks);
                UpdateMax(ref _maxWaitTicks, ticks);
            }
        }

        private static void UpdateMax(ref long max, long value)
        {
            long current;
            while (value > (current = Interlocked.Read(ref max)) &&
                   Interlocked.CompareExchange(ref max, value, current) != current)
            {
            }
        }

        private static void LogFailure(string what, Exception exception)
        {
            if (Interlocked.Increment(ref _failureLogs) <= MaxFailureLogs)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"MetalSpeculativePipelines: {what} failed: {exception.GetType().Name}: {exception.Message}");
            }
        }

        internal static void CountHit() => Interlocked.Increment(ref _hits);

        internal static void CountUnused() => Interlocked.Increment(ref _unused);

        internal static void CountPreempted() => Interlocked.Increment(ref _preempted);

        internal static void CountCancelled() => Interlocked.Increment(ref _cancelled);

        // Render thread: the program's first pipeline request against its
        // prediction. Returns true when they match.
        internal static bool ObserveFirstUse(ref PipelineUid predicted, ref PipelineUid actual)
        {
            if (predicted.Equals(ref actual))
            {
                Interlocked.Increment(ref _firstUseMatches);
                return true;
            }

            Interlocked.Increment(ref _firstUseMismatches);

            PipelineState p = new() { Internal = predicted };
            PipelineState a = new() { Internal = actual };
            bool header = p.Internal.Id0 != a.Internal.Id0 || p.Internal.Id1 != a.Internal.Id1;
            bool colorFormat = false, blend = false, writeMask = false, attribute = false, layout = false;
            for (int index = 0; index < Constants.MaxColorAttachments; index++)
            {
                ColorBlendStateUid pb = p.Internal.ColorBlendState[index], ab = a.Internal.ColorBlendState[index];
                colorFormat |= pb.PixelFormat != ab.PixelFormat;
                writeMask |= pb.WriteMask != ab.WriteMask;
                blend |= ab.PixelFormat != MTLPixelFormat.Invalid && pb.PixelFormat == ab.PixelFormat &&
                    (pb.Id0 & ~(0xFUL << 56)) != (ab.Id0 & ~(0xFUL << 56));
            }

            int attributes = (int)Math.Min(a.VertexAttributeDescriptionsCount, (uint)Constants.MaxVertexAttributes);
            for (int index = 0; index < attributes; index++)
            {
                attribute |= p.Internal.VertexAttributes[index].Id0 != a.Internal.VertexAttributes[index].Id0;
            }

            int layouts = (int)Math.Min(a.VertexBindingDescriptionsCount, Constants.ZeroBufferIndex + 1);
            for (int index = 0; index < layouts; index++)
            {
                layout |= p.Internal.VertexBindings[index].Id0 != a.Internal.VertexBindings[index].Id0;
            }

            if (header) Interlocked.Increment(ref _mismatchFields[(int)Field.Header]);
            if (colorFormat) Interlocked.Increment(ref _mismatchFields[(int)Field.ColorFormat]);
            if (blend) Interlocked.Increment(ref _mismatchFields[(int)Field.Blend]);
            if (writeMask) Interlocked.Increment(ref _mismatchFields[(int)Field.WriteMask]);
            if (attribute) Interlocked.Increment(ref _mismatchFields[(int)Field.VertexAttribute]);
            if (layout) Interlocked.Increment(ref _mismatchFields[(int)Field.VertexLayout]);

            if (Interlocked.Increment(ref _detailLogs) <= MaxDetailLogs)
            {
                Logger.Info?.Print(LogClass.Gpu,
                    $"MetalSpeculativePipelines: first-use mismatch: " +
                    $"header={header} [pred id0={p.Internal.Id0:X16} id1={p.Internal.Id1:X16} actual id0={a.Internal.Id0:X16} id1={a.Internal.Id1:X16}] " +
                    $"colorFormat={colorFormat} blend={blend} writeMask={writeMask} " +
                    $"attr={attribute} layout={layout} " +
                    $"pred[{Describe(ref p)}] actual[{Describe(ref a)}]");
            }

            return false;
        }

        private static string Describe(ref PipelineState s)
        {
            System.Text.StringBuilder text = new();
            text.Append($"cls={s.InputPrimitiveTopologyClass} ds={s.DepthStencilFormat} a2c={s.AlphaToCoverageEnable} logic={s.LogicOpEnable}/{s.LogicOp} discard={s.RasterizerDiscardEnable} rts={s.ColorBlendAttachmentStateCount}");
            for (int index = 0; index < Constants.MaxColorAttachments; index++)
            {
                ColorBlendStateUid blend = s.Internal.ColorBlendState[index];
                if (blend.Id0 != 0)
                {
                    text.Append($" c{index}={blend.Id0:X16}");
                }
            }

            int attributes = (int)Math.Min(s.VertexAttributeDescriptionsCount, (uint)Constants.MaxVertexAttributes);
            for (int index = 0; index < attributes; index++)
            {
                text.Append($" a{index}={s.Internal.VertexAttributes[index].Id0:X}");
            }

            int layouts = (int)Math.Min(s.VertexBindingDescriptionsCount, 33u);
            for (int index = 0; index < layouts; index++)
            {
                ulong id = s.Internal.VertexBindings[index].Id0;
                if (id != 0)
                {
                    text.Append($" l{index}={id:X}");
                }
            }

            return text.ToString();
        }

        internal static string Summary()
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            return $"MetalSpeculativePipelines: mode={Mode};offered={Interlocked.Read(ref _offered)};" +
                $"unpredictable={Interlocked.Read(ref _unpredictable)};built={Interlocked.Read(ref _built)};" +
                $"failed={Interlocked.Read(ref _failed)};hits={Interlocked.Read(ref _hits)};unused={Interlocked.Read(ref _unused)};" +
                $"preempted={Interlocked.Read(ref _preempted)};cancelled={Interlocked.Read(ref _cancelled)};" +
                $"buildMs={Interlocked.Read(ref _buildTicks) * ms:F1};maxBuildMs={Interlocked.Read(ref _maxBuildTicks) * ms:F1};" +
                $"waits={Interlocked.Read(ref _waits)};waitMs={Interlocked.Read(ref _waitTicks) * ms:F1};" +
                $"maxWaitMs={Interlocked.Read(ref _maxWaitTicks) * ms:F1};" +
                $"firstUseMatches={Interlocked.Read(ref _firstUseMatches)};" +
                $"firstUseMismatches={Interlocked.Read(ref _firstUseMismatches)};" +
                $"mismatchHeader={Interlocked.Read(ref _mismatchFields[(int)Field.Header])};" +
                $"mismatchColorFormat={Interlocked.Read(ref _mismatchFields[(int)Field.ColorFormat])};" +
                $"mismatchBlend={Interlocked.Read(ref _mismatchFields[(int)Field.Blend])};" +
                $"mismatchWriteMask={Interlocked.Read(ref _mismatchFields[(int)Field.WriteMask])};" +
                $"mismatchVertexAttribute={Interlocked.Read(ref _mismatchFields[(int)Field.VertexAttribute])};" +
                $"mismatchVertexLayout={Interlocked.Read(ref _mismatchFields[(int)Field.VertexLayout])}";
        }

        internal static void LogSummaryIfChanged(string reason)
        {
            if (!Enabled)
            {
                return;
            }

            string summary = Summary();
            if (summary == _lastSummary)
            {
                return;
            }

            _lastSummary = summary;
            Logger.Info?.Print(LogClass.Gpu, summary + ";reason=" + reason);
        }
    }
}
