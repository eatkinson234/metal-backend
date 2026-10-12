using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.QuartzCore;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    public enum EncoderType
    {
        Blit,
        Compute,
        Render,
        None
    }

    [SupportedOSPlatform("macos")]
    class Pipeline : IPipeline, IEncoderFactory, IDisposable
    {
        private const ulong MinByteWeightForFlush = 256 * 1024 * 1024; // MiB

        private readonly MTLDevice _device;
        private readonly MetalRenderer _renderer;
        private EncoderStateManager _encoderStateManager;
        private ulong _byteWeight;
        private ulong _diagnosticDrawCount;
        private ulong _diagnosticBarrierCount;
        private string _lastDiagnosticBarrier = "None";

        public MTLCommandBuffer CommandBuffer;

        public IndexBufferPattern QuadsToTrisPattern;
        public IndexBufferPattern TriFanToTrisPattern;

        internal CommandBufferScoped? PreloadCbs { get; private set; }
        internal CommandBufferScoped Cbs { get; private set; }
        internal CommandBufferEncoder Encoders => Cbs.Encoders;
        internal EncoderType CurrentEncoderType => Encoders.CurrentEncoderType;
        internal Texture CreateFeedbackSnapshotTexture(TextureCreateInfo info) => new(_device, _renderer, this, info);
        internal FeedbackCopyTexture CreateFeedbackCopyTexture(TextureCreateInfo info, MTLTexture view, MTLPixelFormat format) =>
            new(_device, _renderer, this, info, view, format);
        internal readonly FeedbackCopyPool FeedbackCopyPool;

        public Pipeline(MTLDevice device, MetalRenderer renderer)
        {
            _device = device;
            _renderer = renderer;
            FeedbackCopyPool = new FeedbackCopyPool(device);

            renderer.CommandBufferPool.Initialize(this);

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.Rent()).CommandBuffer;
        }

        internal void InitEncoderStateManager(BufferManager bufferManager)
        {
            _encoderStateManager = new EncoderStateManager(_device, bufferManager, this);

            QuadsToTrisPattern = new IndexBufferPattern(_renderer, 4, 6, 0, [0, 1, 2, 0, 2, 3], 4, false);
            TriFanToTrisPattern = new IndexBufferPattern(_renderer, 3, 3, 2, [int.MinValue, -1, 0], 1, true);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All, bool endRenderPass = true)
        {
            if (endRenderPass && CurrentEncoderType == EncoderType.Render)
            {
                EndCurrentPass();
            }

            return _encoderStateManager.SwapState(state, flags);
        }

        public PredrawState SavePredrawState()
        {
            return _encoderStateManager.SavePredrawState();
        }

        public void RestorePredrawState(PredrawState state)
        {
            _encoderStateManager.RestorePredrawState(state);
        }

        public void SetClearLoadAction(bool clear)
        {
            _encoderStateManager.SetClearLoadAction(clear);
        }

        public MTLRenderCommandEncoder GetOrCreateRenderEncoder(bool forDraw = false)
        {
            if (!forDraw)
            {
                if (CurrentEncoderType != EncoderType.Render) _encoderStateManager.SignalRenderDirty();
                return Cbs.Encoders.EnsureRenderEncoder();
            }

            _diagnosticDrawCount++;
            string context = $"draw={_diagnosticDrawCount};barrier={_diagnosticBarrierCount}:{_lastDiagnosticBarrier};encoder={CurrentEncoderType}";
            try
            {
                _encoderStateManager.PrepareFeedbackDraw(context);
                _encoderStateManager.PrepareFeedbackCopies(context);
                // Snapshot/copy preparation can have changed Render to Blit. Recheck
                // *after* that boundary, before writing any argument buffers.
                if (CurrentEncoderType != EncoderType.Render)
                    _encoderStateManager.SignalRenderDirty();
                _encoderStateManager.RenderResourcesPrepass();
                _encoderStateManager.ValidateRenderFeedback(context);
                _encoderStateManager.VerifyFeedbackDraw(context);
                MTLRenderCommandEncoder encoder = Cbs.Encoders.EnsureRenderEncoder();
                _encoderStateManager.RebindRenderState(encoder);
                return encoder;
            }
            catch (RenderFeedbackGuard.FeedbackGuardException failure)
            {
                _encoderStateManager.CaptureSnapshotAdmissionRejection(context, failure);
                _encoderStateManager.FinishFeedbackDraw();
                throw;
            }
            catch
            {
                _encoderStateManager.FinishFeedbackDraw();
                throw;
            }
        }

        private void FinishFeedbackDraw() => _encoderStateManager.FinishFeedbackDraw();

        // The guard throws before the render encoder is ensured, so a rejected draw
        // is never encoded. Drop that one draw instead of ending the process: the
        // unsupported alias still never reaches the GPU. This is degraded rendering;
        // every skip is counted by class (FeedbackStats) and reported at shutdown.
        private void SkipFeedbackDraw(RenderFeedbackGuard.FeedbackGuardException failure)
        {
            _encoderStateManager.SignalRenderDirty();
            RenderFeedbackGuard.CountSkippedDraw(failure);
        }

        public MTLBlitCommandEncoder GetOrCreateBlitEncoder()
        {
            return Cbs.Encoders.EnsureBlitEncoder();
        }

        public MTLComputeCommandEncoder GetOrCreateComputeEncoder(bool forDispatch = false)
        {
            // Mark all state as dirty to ensure it is set on the new encoder
            if (Cbs.Encoders.CurrentEncoderType != EncoderType.Compute)
            {
                _encoderStateManager.SignalComputeDirty();
            }

            if (forDispatch)
            {
                _encoderStateManager.ComputeResourcesPrepass();
            }

            MTLComputeCommandEncoder computeCommandEncoder = Cbs.Encoders.EnsureComputeEncoder();

            if (forDispatch)
            {
                _encoderStateManager.RebindComputeState(computeCommandEncoder);
            }

            return computeCommandEncoder;
        }

        public void EndCurrentPass()
        {
            Cbs.Encoders.EndCurrentPass();
        }

        public MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            return _encoderStateManager.CreateRenderCommandEncoder();
        }

        public MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            return _encoderStateManager.CreateComputeCommandEncoder();
        }

        public void Present(CAMetalDrawable drawable, Texture src, Extents2D srcRegion, Extents2D dstRegion, bool isLinear,
            FramePhaseRecorder phase = null, FramePresentationTiming timing = default, long backendBegin = 0, long drawableAcquired = 0)
        {
            // TODO: Clean this up
            TextureCreateInfo textureInfo = new((int)drawable.Texture.Width, (int)drawable.Texture.Height, (int)drawable.Texture.Depth, (int)drawable.Texture.MipmapLevelCount, (int)drawable.Texture.SampleCount, 0, 0, 0, Format.B8G8R8A8Unorm, 0, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
            Texture dst = new(_device, _renderer, this, textureInfo, drawable.Texture, 0, 0);

            _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, isLinear, true);

            EndCurrentPass();

            bool observe = phase != null && timing.Sequence != 0;
            int registrationStatus;
            if (observe && PresentBufferDiagnostic.Enabled)
                registrationStatus = Cbs.ObservePresent(phase, drawable.NativePtr, timing.Sequence).PresentedRegisterStatus;
            else
                registrationStatus = observe ? phase.Register(drawable.NativePtr, timing.Sequence) : -1;
            long beforePresent = observe ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (PresentPacing.Active)
            {
                PresentPacing.Present(Cbs.CommandBuffer.NativePtr, drawable.NativePtr);
            }
            else
            {
                Cbs.CommandBuffer.PresentDrawable(drawable);
            }

            FlushCommandsImpl();
            if (observe)
            {
                long afterCommit = System.Diagnostics.Stopwatch.GetTimestamp();
                phase.Submit(new FramePhaseRecorder.Submission(timing, backendBegin, drawableAcquired,
                    beforePresent, afterCommit, registrationStatus));
            }

            // TODO: Auto flush counting
            _renderer.SyncManager.GetAndResetWaitTicks();

            // Cleanup
            dst.Dispose();
        }

        public CommandBufferScoped GetPreloadCommandBuffer()
        {
            PreloadCbs ??= _renderer.CommandBufferPool.Rent();

            return PreloadCbs.Value;
        }

        public void FlushCommandsIfWeightExceeding(IAuto disposedResource, ulong byteWeight)
        {
            bool usedByCurrentCb = disposedResource.HasCommandBufferDependency(Cbs);

            if (PreloadCbs != null && !usedByCurrentCb)
            {
                usedByCurrentCb = disposedResource.HasCommandBufferDependency(PreloadCbs.Value);
            }

            if (usedByCurrentCb)
            {
                // Since we can only free memory after the command buffer that uses a given resource was executed,
                // keeping the command buffer might cause a high amount of memory to be in use.
                // To prevent that, we force submit command buffers if the memory usage by resources
                // in use by the current command buffer is above a given limit, and those resources were disposed.
                _byteWeight += byteWeight;

                if (_byteWeight >= MinByteWeightForFlush)
                {
                    FlushCommandsImpl();
                }
            }
        }

        public void FlushCommandsImpl()
        {
            EndCurrentPass();

            _byteWeight = 0;

            if (PreloadCbs != null)
            {
                PreloadCbs.Value.Dispose();
                PreloadCbs = null;
            }

            CommandBuffer = (Cbs = _renderer.CommandBufferPool.ReturnAndRent(Cbs)).CommandBuffer;
            _renderer.RegisterFlush();
        }

        public void DirtyTextures()
        {
            _encoderStateManager.DirtyTextures();
        }

        public void DirtyImages()
        {
            _encoderStateManager.DirtyImages();
        }

        public void Blit(
            Texture src,
            Texture dst,
            Extents2D srcRegion,
            Extents2D dstRegion,
            bool isDepthOrStencil,
            bool linearFilter)
        {
            if (isDepthOrStencil)
            {
                _renderer.HelperShader.BlitDepthStencil(Cbs, src, dst, srcRegion, dstRegion);
            }
            else
            {
                _renderer.HelperShader.BlitColor(Cbs, src, dst, srcRegion, dstRegion, linearFilter);
            }
        }

        private void EndEncoderForHostBarrier(string operation)
        {
            // Metal 3 tracked resources are ordered between encoders. Apple tile GPUs
            // cannot wait on Fragment inside a render pass. Preserve Store/Load and
            // let the next encoder restore state; this is not a CPU wait or a commit.
            _diagnosticBarrierCount++;
            _lastDiagnosticBarrier = operation;
            if (_diagnosticBarrierCount <= 8)
            {
                Logger.Info?.PrintMsg(LogClass.Gpu,
                    $"[CouchNativeSync] barrier={_diagnosticBarrierCount}:{operation};encoder={CurrentEncoderType};draws={_diagnosticDrawCount};action=end-encoder");
            }
            EndCurrentPass();
        }

        public void Barrier()
        {
            EndEncoderForHostBarrier(nameof(Barrier));
        }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            MTLBlitCommandEncoder blitCommandEncoder = GetOrCreateBlitEncoder();

            MTLBuffer mtlBuffer = _renderer.BufferManager.GetBuffer(destination, offset, size, true).Get(Cbs, offset, size, true).Value;

            // Might need a closer look, range's count, lower, and upper bound
            // must be a multiple of 4
            blitCommandEncoder.FillBuffer(mtlBuffer,
                new NSRange
                {
                    location = (ulong)offset,
                    length = (ulong)size
                },
                (byte)value);
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            float[] colors = [color.Red, color.Green, color.Blue, color.Alpha];
            Texture dst = _encoderStateManager.RenderTargets[index];

            // TODO: Remove workaround for Wonder which has an invalid texture due to unsupported format
            if (dst == null)
            {
                Logger.Warning?.PrintMsg(LogClass.Gpu, "Attempted to clear invalid render target!");
                return;
            }

            _renderer.HelperShader.ClearColor(index, colors, componentMask, dst.Width, dst.Height, dst.Info.Format);
        }

        public void ClearRenderTargetDepthStencil(int layer, int layerCount, float depthValue, bool depthMask, int stencilValue, int stencilMask)
        {
            Texture depthStencil = _encoderStateManager.DepthStencil;

            if (depthStencil == null)
            {
                return;
            }

            _renderer.HelperShader.ClearDepthStencil(depthValue, depthMask, stencilValue, stencilMask, depthStencil.Width, depthStencil.Height);
        }

        public void CommandBufferBarrier()
        {
            EndEncoderForHostBarrier(nameof(CommandBufferBarrier));
        }

        public void CopyBuffer(BufferHandle src, BufferHandle dst, int srcOffset, int dstOffset, int size)
        {
            Auto<DisposableBuffer> srcBuffer = _renderer.BufferManager.GetBuffer(src, srcOffset, size, false);
            Auto<DisposableBuffer> dstBuffer = _renderer.BufferManager.GetBuffer(dst, dstOffset, size, true);

            BufferHolder.Copy(Cbs, srcBuffer, dstBuffer, srcOffset, dstOffset, size);
        }

        public void PushDebugGroup(string name)
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;
            NSString debugGroupName = StringHelper.NSString(name);

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Blit:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
                case EncoderType.Compute:
                    encoder.Value.PushDebugGroup(debugGroupName);
                    break;
            }
        }

        public void PopDebugGroup()
        {
            MTLCommandEncoder? encoder = Encoders.CurrentEncoder;

            if (encoder == null)
            {
                return;
            }

            switch (Encoders.CurrentEncoderType)
            {
                case EncoderType.Render:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Blit:
                    encoder.Value.PopDebugGroup();
                    break;
                case EncoderType.Compute:
                    encoder.Value.PopDebugGroup();
                    break;
            }
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            using NSAutoreleasePool pool = new();
            DispatchCompute(groupsX, groupsY, groupsZ, String.Empty);
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ, string debugGroupName)
        {
            using NSAutoreleasePool pool = new();
            MTLComputeCommandEncoder computeCommandEncoder = GetOrCreateComputeEncoder(true);

            ComputeSize localSize = _encoderStateManager.ComputeLocalSize;

            if (debugGroupName != String.Empty)
            {
                PushDebugGroup(debugGroupName);
            }

            computeCommandEncoder.DispatchThreadgroups(
                new MTLSize { width = (ulong)groupsX, height = (ulong)groupsY, depth = (ulong)groupsZ },
                new MTLSize { width = (ulong)localSize.X, height = (ulong)localSize.Y, depth = (ulong)localSize.Z });

            if (debugGroupName != String.Empty)
            {
                PopDebugGroup();
            }
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            Draw(vertexCount, instanceCount, firstVertex, firstInstance, String.Empty);
        }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance, string debugGroupName)
        {
            using NSAutoreleasePool pool = new();
            try
            {
            if (vertexCount == 0)
            {
                return;
            }

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            _encoderStateManager.PrepareActualDrawPrimitive(primitiveType);

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();

                BufferHandle handle = pattern.GetRepeatingBuffer(vertexCount, out int indexCount);
                Auto<DisposableBuffer> buffer = _renderer.BufferManager.GetBuffer(handle, false);
                MTLBuffer mtlBuffer = buffer.Get(Cbs, 0, indexCount * sizeof(int)).Value;

                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                renderCommandEncoder.DrawIndexedPrimitives(
                    primitiveType,
                    (ulong)indexCount,
                    MTLIndexType.UInt32,
                    mtlBuffer,
                    0);
                _encoderStateManager.ReportFeedbackDrawEncoded();
            }
            else
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                if (debugGroupName != String.Empty)
                {
                    PushDebugGroup(debugGroupName);
                }

                renderCommandEncoder.DrawPrimitives(
                    primitiveType,
                    (ulong)firstVertex,
                    (ulong)vertexCount,
                    (ulong)instanceCount,
                    (ulong)firstInstance);
                _encoderStateManager.ReportFeedbackDrawEncoded();

                if (debugGroupName != String.Empty)
                {
                    PopDebugGroup();
                }
            }
            }
            catch (RenderFeedbackGuard.FeedbackGuardException failure)
            {
                SkipFeedbackDraw(failure);
            }
            finally
            {
                FinishFeedbackDraw();
            }
        }

        private IndexBufferPattern GetIndexBufferPattern()
        {
            return _encoderStateManager.Topology switch
            {
                PrimitiveTopology.Quads => QuadsToTrisPattern,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => TriFanToTrisPattern,
                _ => throw new NotSupportedException($"Unsupported topology: {_encoderStateManager.Topology}"),
            };
        }

        private static PrimitiveTopology TopologyRemap(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads => PrimitiveTopology.Triangles,
                PrimitiveTopology.QuadStrip => PrimitiveTopology.TriangleStrip,
                PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => PrimitiveTopology.Triangles,
                _ => topology,
            };
        }

        private bool TopologyUnsupported(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Quads or PrimitiveTopology.TriangleFan or PrimitiveTopology.Polygon => true,
                _ => false,
            };
        }

        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            using NSAutoreleasePool pool = new();
            try
            {
            if (indexCount == 0)
            {
                return;
            }

            MTLBuffer mtlBuffer;
            int offset;
            MTLIndexType type;
            int finalIndexCount = indexCount;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            _encoderStateManager.PrepareActualDrawPrimitive(primitiveType);

            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                IndexBufferPattern pattern = GetIndexBufferPattern();
                int convertedCount = pattern.GetConvertedCount(indexCount);

                finalIndexCount = convertedCount;

                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetConvertedIndexBuffer(_renderer, Cbs, firstIndex, indexCount, convertedCount, pattern);
            }
            else
            {
                (mtlBuffer, offset, type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

                // The bound offset is the start of the index buffer range. Metal
                // takes a byte offset for the first index; UByte indices have
                // already been converted to UInt16 by GetIndexBuffer.
                if (firstIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(firstIndex));
                }

                int indexSize = type switch
                {
                    MTLIndexType.UInt16 => sizeof(ushort),
                    MTLIndexType.UInt32 => sizeof(uint),
                    _ => throw new InvalidOperationException($"Unsupported Metal index type {type}."),
                };
                offset = checked(offset + checked(firstIndex * indexSize));
            }

            if (mtlBuffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                renderCommandEncoder.DrawIndexedPrimitives(
                    primitiveType,
                    (ulong)finalIndexCount,
                    type,
                    mtlBuffer,
                    (ulong)offset,
                    (ulong)instanceCount,
                    firstVertex,
                    (ulong)firstInstance);
                _encoderStateManager.ReportFeedbackDrawEncoded();
            }
            }
            catch (RenderFeedbackGuard.FeedbackGuardException failure)
            {
                SkipFeedbackDraw(failure);
            }
            finally
            {
                FinishFeedbackDraw();
            }
        }

        public void DrawIndexedIndirect(BufferRange indirectBuffer)
        {
            DrawIndexedIndirectOffset(indirectBuffer);
        }

        public void DrawIndexedIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            using NSAutoreleasePool pool = new();
            try
            {
            // TODO: Reindex unsupported topologies
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indexed with unsupported topology: {_encoderStateManager.Topology}");
            }

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            _encoderStateManager.PrepareActualDrawPrimitive(primitiveType);

            (MTLBuffer indexBuffer, int indexOffset, MTLIndexType type) = _encoderStateManager.IndexBuffer.GetIndexBuffer(_renderer, Cbs);

            if (indexBuffer.NativePtr != IntPtr.Zero && buffer.NativePtr != IntPtr.Zero)
            {
                MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

                renderCommandEncoder.DrawIndexedPrimitives(
                    primitiveType,
                    type,
                    indexBuffer,
                    (ulong)indexOffset,
                    buffer,
                    (ulong)(indirectBuffer.Offset + offset));
                _encoderStateManager.ReportFeedbackDrawEncoded();
            }
            }
            catch (RenderFeedbackGuard.FeedbackGuardException failure)
            {
                SkipFeedbackDraw(failure);
            }
            finally
            {
                FinishFeedbackDraw();
            }
        }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndexedIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawIndirect(BufferRange indirectBuffer)
        {
            DrawIndirectOffset(indirectBuffer);
        }

        public void DrawIndirectOffset(BufferRange indirectBuffer, int offset = 0)
        {
            using NSAutoreleasePool pool = new();
            try
            {
            if (TopologyUnsupported(_encoderStateManager.Topology))
            {
                // TODO: Reindex unsupported topologies
                Logger.Warning?.Print(LogClass.Gpu, $"Drawing indirect with unsupported topology: {_encoderStateManager.Topology}");
            }

            MTLBuffer buffer = _renderer.BufferManager
                .GetBuffer(indirectBuffer.Handle, indirectBuffer.Offset, indirectBuffer.Size, false)
                .Get(Cbs, indirectBuffer.Offset, indirectBuffer.Size).Value;

            MTLPrimitiveType primitiveType = TopologyRemap(_encoderStateManager.Topology).Convert();
            _encoderStateManager.PrepareActualDrawPrimitive(primitiveType);
            MTLRenderCommandEncoder renderCommandEncoder = GetOrCreateRenderEncoder(true);

            renderCommandEncoder.DrawPrimitives(
                primitiveType,
                buffer,
                (ulong)(indirectBuffer.Offset + offset));
            _encoderStateManager.ReportFeedbackDrawEncoded();
            }
            catch (RenderFeedbackGuard.FeedbackGuardException failure)
            {
                SkipFeedbackDraw(failure);
            }
            finally
            {
                FinishFeedbackDraw();
            }
        }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride)
        {
            for (int i = 0; i < maxDrawCount; i++)
            {
                DrawIndirectOffset(indirectBuffer, stride * i);
            }
        }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            _renderer.HelperShader.DrawTexture(texture, sampler, srcRegion, dstRegion);
        }

        public void SetAlphaTest(bool enable, float reference, CompareOp op)
        {
            // This is currently handled using shader specialization, as Metal does not support alpha test.
            // In the future, we may want to use this to write the reference value into the support buffer,
            // to avoid creating one version of the shader per reference value used.
        }

        public void SetBlendState(AdvancedBlendDescriptor blend)
        {
            // Metal does not support advanced blend.
        }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            _encoderStateManager.UpdateBlendDescriptors(index, blend);
        }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            if (enables == 0)
            {
                _encoderStateManager.UpdateDepthBias(0, 0, 0);
            }
            else
            {
                _encoderStateManager.UpdateDepthBias(units, factor, clamp);
            }
        }

        public void SetDepthClamp(bool clamp)
        {
            _encoderStateManager.UpdateDepthClamp(clamp);
        }

        public void SetDepthMode(DepthMode mode)
        {
            // Metal does not support depth clip control.
        }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _encoderStateManager.UpdateDepthState(depthTest);
        }

        public void SetFaceCulling(bool enable, Face face)
        {
            _encoderStateManager.UpdateCullMode(enable, face);
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            _encoderStateManager.UpdateFrontFace(frontFace);
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _encoderStateManager.UpdateIndexBuffer(buffer, type);
        }

        public void SetImage(ShaderStage stage, int binding, ITexture image)
        {
            if (image is TextureBase img)
            {
                _encoderStateManager.UpdateImage(stage, binding, img);
            }
        }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArray(stage, binding, imageArray);
            }
        }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array)
        {
            if (array is ImageArray imageArray)
            {
                _encoderStateManager.UpdateImageArraySeparate(stage, setIndex, imageArray);
            }
        }

        public void SetLineParameters(float width, bool smooth)
        {
            // Metal does not support wide-lines.
        }

        public void SetLogicOpState(bool enable, LogicalOp op)
        {
            _encoderStateManager.UpdateLogicOpState(enable, op);
        }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _encoderStateManager.UpdateMultisampleState(multisample);
        }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel)
        {
            Logger.Warning?.Print(LogClass.Gpu, "Not Implemented!");
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin)
        {
            Logger.Warning?.Print(LogClass.Gpu, "Not Implemented!");
        }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode)
        {
            // Metal does not support polygon mode.
        }

        public void SetPrimitiveRestart(bool enable, int index)
        {
            // Always active for LineStrip and TriangleStrip
            // https://github.com/gpuweb/gpuweb/issues/1220#issuecomment-732483263
            // https://developer.apple.com/documentation/metal/mtlrendercommandencoder/1515520-drawindexedprimitives
            // https://stackoverflow.com/questions/70813665/how-to-render-multiple-trianglestrips-using-metal

            // Emulating disabling this is very difficult. It's unlikely for an index buffer to use the largest possible index,
            // so it's fine nearly all of the time.
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _encoderStateManager.UpdatePrimitiveTopology(topology);
        }

        public void SetProgram(IProgram program)
        {
            _encoderStateManager.UpdateProgram(program);
        }

        public void SetRasterizerDiscard(bool discard)
        {
            _encoderStateManager.UpdateRasterizerDiscard(discard);
        }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            _encoderStateManager.UpdateRenderTargetColorMasks(componentMask);
        }

        public void SetRenderTargets(ITexture[] colors, ITexture depthStencil)
        {
            _encoderStateManager.UpdateRenderTargets(colors, depthStencil);
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            _encoderStateManager.UpdateScissors(regions);
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _encoderStateManager.UpdateStencilState(stencilTest);
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateUniformBuffers(buffers);
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(buffers);
        }

        internal void SetStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            _encoderStateManager.UpdateStorageBuffers(first, buffers);
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            // A null texture is an explicit unbind. Ignoring it preserves a stale
            // resource, which can have the wrong type for the next shader.
            if (texture == null || texture is TextureBase)
            {
                if (sampler == null || sampler is SamplerHolder)
                {
                    _encoderStateManager.UpdateTextureAndSampler(stage, binding, (TextureBase)texture, (SamplerHolder)sampler);
                }
            }
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArray(stage, binding, textureArray);
            }
        }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array)
        {
            if (array is TextureArray textureArray)
            {
                _encoderStateManager.UpdateTextureArraySeparate(stage, setIndex, textureArray);
            }
        }

        public void SetUserClipDistance(int index, bool enableClip)
        {
            // TODO. Same as Vulkan
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _encoderStateManager.UpdateVertexAttribs(vertexAttribs);
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _encoderStateManager.UpdateVertexBuffers(vertexBuffers);
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _encoderStateManager.UpdateViewports(viewports);
        }

        public void TextureBarrier()
        {
            EndEncoderForHostBarrier(nameof(TextureBarrier));
        }

        public void TextureBarrierTiled()
        {
            EndEncoderForHostBarrier(nameof(TextureBarrierTiled));
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual)
        {
            // TODO: Implementable via indirect draw commands
            return false;
        }

        public void EndHostConditionalRendering()
        {
            // TODO: Implementable via indirect draw commands
        }

        public void BeginTransformFeedback(PrimitiveTopology topology)
        {
            // Metal does not support transform feedback.
        }

        public void EndTransformFeedback()
        {
            // Metal does not support transform feedback.
        }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers)
        {
            // Metal does not support transform feedback.
        }

        public void Dispose()
        {
            EndCurrentPass();
            _encoderStateManager.Dispose();
            FeedbackStats.LogSummary("renderer-dispose");
            FeedbackCopyPool.Dispose();
        }
    }
}
