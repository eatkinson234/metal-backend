using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.State;
using Ryujinx.Graphics.Shader;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using BufferAssignment = Ryujinx.Graphics.GAL.BufferAssignment;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    struct EncoderStateManager : IDisposable
    {
        private const int ArrayGrowthSize = 16;
        // Metal's resource enum raw values: memoryless=3, tracked=2. The pinned
        // SharpMetal build omits named members for these modes on macOS.
        private const MTLStorageMode MemorylessStorage = (MTLStorageMode)3;
        private const MTLHazardTrackingMode TrackedHazards = (MTLHazardTrackingMode)2;

        private readonly MTLDevice _device;
        private readonly Pipeline _pipeline;
        private readonly BufferManager _bufferManager;

        private readonly DepthStencilCache _depthStencilCache;
        private readonly MTLDepthStencilState _defaultState;

        private readonly EncoderState _mainState = new();
        private EncoderState _currentState;
        private EncoderState _snapshotState;
        private TextureRef _snapshotOriginal;
        private RenderFeedbackGuard.BindingEvidence _snapshotBinding;
        private Auto<DepthFeedbackSnapshot> _snapshotLease;
        private IntPtr _snapshotCommandBuffer;
        private IntPtr _snapshotSourceRoot;
        private string _snapshotContext;
        private string _snapshotAdmissionRejection;
        private bool _snapshotReadonlyStencil148;
        private string _cubeDiagnosticDrawContext;

        // General feedback copies (FeedbackCopy.cs) for the current draw.
        private static readonly bool _feedbackCopyEnabled =
            Environment.GetEnvironmentVariable("SMASH_COUCH_FEEDBACK_COPY") != "0";
        private EncoderState _copyState;
        private Auto<FeedbackCopyLease> _copyLease;
        private List<(int Binding, TextureRef Original)> _copyOriginals;

        public readonly IndexBufferState IndexBuffer => _currentState.IndexBuffer;
        public readonly PrimitiveTopology Topology => _currentState.Topology;
        public readonly Texture[] RenderTargets => _currentState.RenderTargets;
        public readonly Texture DepthStencil => _currentState.DepthStencil;
        public readonly ComputeSize ComputeLocalSize => _currentState.ComputeProgram.ComputeLocalSize;

        // RGBA32F is the biggest format
        private const int ZeroBufferSize = 4 * 4;
        private readonly BufferHandle _zeroBuffer;

        public unsafe EncoderStateManager(MTLDevice device, BufferManager bufferManager, Pipeline pipeline)
        {
            _device = device;
            _pipeline = pipeline;
            _bufferManager = bufferManager;

            _depthStencilCache = new(device);
            _currentState = _mainState;

            _defaultState = _depthStencilCache.GetOrCreate(_currentState.DepthStencilUid);

            // Zero buffer
            byte[] zeros = new byte[ZeroBufferSize];
            fixed (byte* ptr = zeros)
            {
                _zeroBuffer = _bufferManager.Create((IntPtr)ptr, ZeroBufferSize);
            }
        }

        public readonly void Dispose()
        {
            _depthStencilCache.Dispose();
        }

        private readonly void SignalDirty(DirtyFlags flags)
        {
            _currentState.Dirty |= flags;
        }

        public readonly void SignalRenderDirty()
        {
            SignalDirty(DirtyFlags.RenderAll);
        }

        // The returned effective binding remains in place until the *actual*
        // draw has been encoded. Pipeline guarantees FinishFeedbackDraw in every
        // success/throw path, including direct, indexed and indirect variants.
        public bool PrepareFeedbackDraw(string context)
        {
            _cubeDiagnosticDrawContext = context;
            _snapshotAdmissionRejection = null;
            if (_snapshotLease != null)
            {
                _snapshotAdmissionRejection = "nested-snapshot-draw-scope";
                throw new InvalidOperationException("Nested depth feedback draw scope.");
            }

            EncoderState state = _currentState;
            Program program = state.RenderProgram;
            if (program == null) return RejectSnapshotPreparation("active-program-missing");
            if (!program.DepthSnapshotShaderAllowed) return RejectSnapshotPreparation("shader-resource-contract-not-admitted");
            if (!ReferenceEquals(state, _mainState)) return RejectSnapshotPreparation("state-is-not-main");
            if (state.ClearLoadAction) return RejectSnapshotPreparation("color-load-action-is-clear");
            if (state.Pipeline.RasterizerDiscardEnable) return RejectSnapshotPreparation("rasterizer-discard-enabled");
            bool readonlyStencil148 = false;
            if (state.DepthStencilUid.DepthCompareFunction != MTLCompareFunction.LessEqual)
            {
                if (!ReadonlyStencil148SnapshotPolicy.Enabled)
                    return RejectSnapshotPreparation("depth-compare-is-not-less-equal");
                if (!program.ReadonlyStencil148Witness.Allowed)
                    return RejectSnapshotPreparation("stencil148-reviewed-program-witness-not-admitted");
                string rejectedState = ReadonlyStencil148SnapshotPolicy.RejectState(
                    state.DepthStencilUid, state.FrontRefValue, state.BackRefValue);
                if (rejectedState != null) return RejectSnapshotPreparation(rejectedState);
                readonlyStencil148 = true;
            }
            else
            {
                // Preserve the existing LessEqual/stencil-disabled path exactly.
                if (state.DepthStencilUid.DepthWriteEnabled) return RejectSnapshotPreparation("depth-write-enabled");
                if (state.DepthStencilUid.StencilTestEnabled) return RejectSnapshotPreparation("stencil-test-enabled");
            }

            RenderFeedbackGuard.FailureEvidence alias =
                RenderFeedbackGuard.FindRecoveredDepthAlias(state, program.BindingSegments);
            if (alias == null) return RejectSnapshotPreparation("recovered-alias-layout-or-uniqueness-check");
            if (alias.SampledTexture is not Texture sampled || alias.Attachment.Texture is not Texture attachment)
                return RejectSnapshotPreparation("recovered-alias-is-not-metal-texture");
            if (readonlyStencil148)
            {
                // The scan above visited all active bindings/attachments. Unlike
                // the generic path, this reviewed source requires both samples
                // present, alias at128 and a known/disjoint129, never final-slot129.
                if (!ReadonlyStencil148SnapshotPolicy.MatchesBinding(alias.Binding) ||
                    state.TextureRefs[129].Storage == null)
                    return RejectSnapshotPreparation("stencil148-actual-binding-pair-not-reviewed");
                if (state.TextureRefs[128].Stage != ShaderStage.Fragment ||
                    state.TextureRefs[129].Stage != ShaderStage.Fragment ||
                    state.TextureRefs[128].Sampler == null || state.TextureRefs[129].Sampler == null ||
                    state.TextureRefs[128].Sampler.GetUnsafe().Value.NativePtr == IntPtr.Zero ||
                    state.TextureRefs[129].Sampler.GetUnsafe().Value.NativePtr == IntPtr.Zero)
                    return RejectSnapshotPreparation("stencil148-sampled-binding-stage-or-sampler-invalid");
            }

            MTLTexture sourceSamplingHandle = sampled.GetHandle();
            MTLTexture sourceIdentityHandle = sampled.GetRenderAttachmentHandle();
            MTLTexture attachmentHandle = attachment.GetRenderAttachmentHandle();
            RenderFeedbackGuard.Identity sourceId = alias.ShaderIdentity;
            RenderFeedbackGuard.Identity attachmentId = alias.Attachment.Identity;
            TextureCreateInfo info = sampled.Info;
            TextureCreateInfo attachmentInfo = attachment.Info;
            if (readonlyStencil148 && !ReadonlyStencil148SnapshotPolicy.HasExactGuestTexelShape(info))
                return RejectSnapshotPreparation("stencil148-guest-texel-shape-not-reviewed");
            if (!DepthSnapshotTextureInfoPolicy.SameAllocationShape(attachmentInfo, info))
                return RejectSnapshotPreparation("attachment-sampled-allocation-shape-mismatch");
            if (!DepthSnapshotTextureInfoPolicy.HasIdentitySwizzle(attachmentInfo))
                return RejectSnapshotPreparation("depth-attachment-swizzle-is-not-identity");
            if (!DepthSnapshotTextureInfoPolicy.HasAdmittedSampleSwizzle(info))
                return RejectSnapshotPreparation("sampled-swizzle-is-not-identity-or-canonical-depth");
            if (info.Format != Format.D24UnormS8Uint || info.DepthStencilMode != DepthStencilMode.Depth ||
                info.Target != Target.Texture2D ||
                info.Depth != 1 || info.Levels != 1 || info.Samples != 1)
                return RejectSnapshotPreparation("guest-depth-info-outside-bounded-shape");
            if (!DepthSnapshotExtent.TryCreate(info.Width, info.Height, info.BytesPerPixel,
                    out DepthSnapshotExtent extent))
                return RejectSnapshotPreparation("guest-depth-info-outside-bounded-shape");
            if (sampled.FirstLevel != 0 || sampled.FirstLayer != 0 ||
                attachment.FirstLevel != 0 || attachment.FirstLayer != 0)
                return RejectSnapshotPreparation("texture-wrapper-subresource-is-not-zero");
            if (sampled.MtlFormat != MTLPixelFormat.Depth32FloatStencil8 ||
                attachment.MtlFormat != MTLPixelFormat.Depth32FloatStencil8)
                return RejectSnapshotPreparation("guest-to-metal-depth-format-mismatch");

            RenderFeedbackGuard.Identity sourceIdentityId = RenderFeedbackGuard.ReadIdentity(sourceIdentityHandle);
            if (!sourceIdentityId.Known || sourceId.Root != attachmentId.Root || sourceId.Buffer != IntPtr.Zero ||
                sourceId.Level != 0 || sourceId.Slice != 0 || sourceId.Levels != 1 || sourceId.Layers != 1 ||
                sourceIdentityId.Root != sourceId.Root || sourceIdentityId.Buffer != IntPtr.Zero ||
                sourceIdentityId.Level != sourceId.Level || sourceIdentityId.Slice != sourceId.Slice ||
                sourceIdentityId.Levels != sourceId.Levels || sourceIdentityId.Layers != sourceId.Layers ||
                attachmentId.Level != 0 || attachmentId.Slice != 0 ||
                attachmentId.Levels != 1 || attachmentId.Layers != 1)
                return RejectSnapshotPreparation("source-identity-view-subresource-mismatch");

            if (!extent.MatchesNativeDepthTexture(sourceSamplingHandle, MTLPixelFormat.Depth32FloatStencil8) ||
                !extent.MatchesNativeDepthTexture(sourceIdentityHandle, MTLPixelFormat.Depth32FloatStencil8) ||
                !extent.MatchesNativeDepthTexture(attachmentHandle, MTLPixelFormat.Depth32FloatStencil8) ||
                sourceId.Root == IntPtr.Zero || attachmentId.Root == IntPtr.Zero)
                return RejectSnapshotPreparation("source-or-attachment-native-handle-properties-mismatch");

            if (readonlyStencil148 &&
                (!ReadonlyStencil148SnapshotPolicy.HasKnownStorage(sourceSamplingHandle) ||
                 !ReadonlyStencil148SnapshotPolicy.HasKnownStorage(sourceIdentityHandle) ||
                 !ReadonlyStencil148SnapshotPolicy.HasKnownStorage(attachmentHandle)))
                return RejectSnapshotPreparation("stencil148-source-or-attachment-storage-unknown");

            Texture snapshot = null;
            DepthFeedbackSnapshot owner = null;
            Auto<DepthFeedbackSnapshot> lease = null;
            using var snapshotTiming = NativeTimingDiagnostics.Measure(NativeTimingKind.DepthSnapshot);
            try
            {
                snapshot = _pipeline.CreateFeedbackSnapshotTexture(info);
                MTLTexture destinationIdentityHandle = snapshot.GetRenderAttachmentHandle();
                MTLTexture destinationSamplingHandle = snapshot.GetHandle();
                RenderFeedbackGuard.Identity destId = RenderFeedbackGuard.ReadIdentity(destinationIdentityHandle);
                RenderFeedbackGuard.Identity destSamplingId = RenderFeedbackGuard.ReadIdentity(destinationSamplingHandle);
                if (!destId.Known || destId.Root == sourceId.Root || destId.Buffer != IntPtr.Zero ||
                    destId.Level != 0 || destId.Slice != 0 || destId.Levels != 1 || destId.Layers != 1 ||
                    !destSamplingId.Known || destSamplingId.Root != destId.Root || destSamplingId.Buffer != IntPtr.Zero ||
                    destSamplingId.Level != 0 || destSamplingId.Slice != 0 ||
                    destSamplingId.Levels != 1 || destSamplingId.Layers != 1 ||
                    !extent.MatchesNativeDepthTexture(destinationIdentityHandle, sourceIdentityHandle.PixelFormat) ||
                    !extent.MatchesNativeDepthTexture(destinationSamplingHandle, sourceSamplingHandle.PixelFormat) ||
                    destinationIdentityHandle.PixelFormat != sourceIdentityHandle.PixelFormat ||
                    destinationIdentityHandle.SampleCount != sourceIdentityHandle.SampleCount ||
                    destinationIdentityHandle.TextureType != sourceIdentityHandle.TextureType ||
                    destinationIdentityHandle.Width != sourceIdentityHandle.Width ||
                    destinationIdentityHandle.Height != sourceIdentityHandle.Height ||
                    destinationIdentityHandle.ArrayLength != 1 ||
                    destinationIdentityHandle.MipmapLevelCount != 1)
                    throw new NotSupportedException("Depth snapshot allocation does not match its source.");

                if (readonlyStencil148 &&
                    (!ReadonlyStencil148SnapshotPolicy.HasKnownStorage(destinationIdentityHandle) ||
                     !ReadonlyStencil148SnapshotPolicy.HasKnownStorage(destinationSamplingHandle)))
                    throw new NotSupportedException("Depth snapshot native storage mode is unknown.");

                owner = new DepthFeedbackSnapshot(snapshot, sourceSamplingHandle, sourceIdentityHandle, sourceId.Root);
                snapshot = null;
                lease = new Auto<DepthFeedbackSnapshot>(owner);
                owner = null;
                // The dependency exists before the first encoded GPU use. It
                // retains both the source view and complete snapshot owner.
                lease.Get(_pipeline.Cbs);
                DepthFeedbackSnapshot.BeforeCopyTestHook?.Invoke(lease.GetUnsafe());
                _pipeline.EndCurrentPass();
                MTLBlitCommandEncoder blit = _pipeline.GetOrCreateBlitEncoder();
                blit.CopyFromTexture(sourceIdentityHandle, 0, 0, new MTLOrigin(),
                    new MTLSize { width = (ulong)extent.Width, height = (ulong)extent.Height, depth = 1 },
                    destinationIdentityHandle, 0, 0, new MTLOrigin());
                _pipeline.EndCurrentPass();
                DepthFeedbackSnapshot.AfterCopyTestHook?.Invoke(lease.GetUnsafe());

                // A pass may have been active on entry. Every state category,
                // including uniforms and attachments, needs a new encoder bind.
                SignalRenderDirty();
                _snapshotBinding = alias.Binding;
                _snapshotOriginal = state.TextureRefs[_snapshotBinding.ActualBinding];
                state.TextureRefs[_snapshotBinding.ActualBinding] = _snapshotOriginal with { Storage = lease.GetUnsafe().Texture };
                _snapshotState = state;
                _snapshotLease = lease;
                _snapshotCommandBuffer = _pipeline.Cbs.CommandBuffer.NativePtr;
                _snapshotSourceRoot = sourceId.Root;
                _snapshotContext = context;
                _snapshotReadonlyStencil148 = readonlyStencil148;
                lease = null;
                _snapshotAdmissionRejection = "snapshot-prepared";
                return true;
            }
            catch (Exception failure)
            {
                _snapshotAdmissionRejection = "snapshot-allocation-or-copy-failed";
                RenderFeedbackGuard.StopSnapshotFailure(state, program.BindingSegments,
                    context, alias, failure);
                throw; // StopSnapshotFailure always throws.
            }
            finally
            {
                lease?.Dispose();
                owner?.Dispose();
                snapshot?.Release();
            }
        }

        private bool RejectSnapshotPreparation(string predicate)
        {
            _snapshotAdmissionRejection = predicate;
            return false;
        }

        internal void CaptureSnapshotAdmissionRejection(string context,
            RenderFeedbackGuard.FeedbackGuardException failure)
        {
            if (!ReferenceEquals(_currentState, _mainState)) return;
            DepthSnapshotAdmissionEvidence.TryCapture(context,
                _currentState?.RenderProgram, _snapshotAdmissionRejection, failure);
        }

        // Runs after the reviewed depth-snapshot path and before argument
        // buffers are written. Replaces every non-array sampled binding that
        // aliases a current attachment with a copy of its pre-draw contents.
        // Anything outside the supported scope is left unchanged, so the guard
        // still rejects it. Never throws a FeedbackGuardException.
        public void PrepareFeedbackCopies(string context)
        {
            if (!_feedbackCopyEnabled || _copyLease != null) return;
            EncoderState state = _currentState;
            Program program = state.RenderProgram;
            if (program?.BindingSegments == null || state.ClearLoadAction ||
                program.BindingSegments.Length <= Constants.TexturesSetIndex) return;

            List<(string Role, RenderFeedbackGuard.Identity Id)> attachments = null;
            for (int i = 0; i < state.RenderTargets.Length; i++)
            {
                if (state.RenderTargets[i] is not Texture target) continue;
                RenderFeedbackGuard.Identity id = RenderFeedbackGuard.ReadIdentity(target.GetRenderAttachmentHandle());
                if (!id.Known) return;
                (attachments ??= new(Constants.MaxColorAttachments + 1)).Add(($"color[{i}]", id));
            }
            if (state.DepthStencil is Texture depth)
            {
                RenderFeedbackGuard.Identity id = RenderFeedbackGuard.ReadIdentity(depth.GetHandle());
                if (!id.Known) return;
                (attachments ??= new(1)).Add(($"depth/stencil:{depth.MtlFormat}", id));
            }
            if (attachments == null) return;

            List<(int Binding, Texture Source, MTLTexture Handle, RenderFeedbackGuard.Identity Id, string Signature)> aliases = null;
            foreach (ResourceBindingSegment segment in program.BindingSegments[Constants.TexturesSetIndex])
            {
                if ((segment.Stages & (ResourceStages.Vertex | ResourceStages.Fragment)) == 0 || segment.IsArray) continue;
                for (int i = 0; i < segment.Count; i++)
                {
                    int binding = segment.Binding + i;
                    if (state.TextureRefs[binding].Storage is not Texture source) continue;
                    MTLTexture handle = source.GetHandle();
                    RenderFeedbackGuard.Identity id = RenderFeedbackGuard.ReadIdentity(handle);
                    if (!id.Known) return;
                    string role = null;
                    foreach ((string attachmentRole, RenderFeedbackGuard.Identity attachmentId) in attachments)
                    {
                        RenderFeedbackGuard.Relation relation = RenderFeedbackGuard.Compare(id, attachmentId);
                        if (relation == RenderFeedbackGuard.Relation.Unknown) return;
                        if (relation == RenderFeedbackGuard.Relation.PotentialAlias)
                        {
                            role = attachmentRole;
                            break;
                        }
                    }
                    if (role == null) continue;
                    string signature = $"stages={segment.Stages};binding={binding};attachment={role};" +
                        $"view={handle.PixelFormat};root={new MTLTexture(id.Root).PixelFormat};levels={id.Levels};" +
                        $"rootLevel={id.Level};rootSlice={id.Slice};state={(ReferenceEquals(state, _mainState) ? "Main" : "Helper")}";
                    if (!FeedbackCopyable(handle, id))
                    {
                        // Record why the copy was declined; the guard then
                        // rejects and counts the draw under its own class.
                        FeedbackStats.CountStop($"copy-declined=unsupported-view;type={handle.TextureType};" +
                            $"samples={handle.SampleCount};layers={id.Layers};buffer={id.Buffer != IntPtr.Zero};{signature}");
                        return;
                    }
                    (aliases ??= new(2)).Add((binding, source, handle, id, signature));
                }
            }
            if (aliases == null) return;

            Auto<FeedbackCopyLease> lease = new(new FeedbackCopyLease(_pipeline.FeedbackCopyPool));
            List<(int Binding, TextureRef Original)> originals = new(aliases.Count);
            try
            {
                lease.Get(_pipeline.Cbs);
                _pipeline.EndCurrentPass();
                MTLBlitCommandEncoder blit = _pipeline.GetOrCreateBlitEncoder();
                Dictionary<(IntPtr Root, ulong Level, ulong Slice, ulong Levels), MTLTexture> copies = new(aliases.Count);
                foreach ((int binding, Texture source, MTLTexture handle, RenderFeedbackGuard.Identity id, string signature) in aliases)
                {
                    var copyKey = (id.Root, id.Level, id.Slice, id.Levels);
                    if (!copies.TryGetValue(copyKey, out MTLTexture copy))
                    {
                        MTLTexture root = new(id.Root);
                        ulong width = Math.Max(1UL, root.Width >> (int)id.Level);
                        ulong height = Math.Max(1UL, root.Height >> (int)id.Level);
                        FeedbackCopyPool.Key key = new(root.PixelFormat, width, height, id.Levels);
                        copy = _pipeline.FeedbackCopyPool.Rent(key);
                        lease.GetUnsafe().AddRoot(key, copy);
                        for (ulong level = 0; level < id.Levels; level++)
                        {
                            blit.CopyFromTexture(root, id.Slice, id.Level + level, new MTLOrigin(),
                                new MTLSize
                                {
                                    width = Math.Max(1UL, width >> (int)level),
                                    height = Math.Max(1UL, height >> (int)level),
                                    depth = 1,
                                },
                                copy, 0, level, new MTLOrigin());
                        }
                        copies[copyKey] = copy;
                    }

                    MTLTexture view = copy.NewTextureView(handle.PixelFormat, MTLTextureType.Type2D,
                        new NSRange { location = 0, length = id.Levels },
                        new NSRange { location = 0, length = 1 },
                        handle.Swizzle);
                    if (view.NativePtr == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Feedback copy view creation failed.");
                    }
                    FeedbackCopyTexture copyTexture = _pipeline.CreateFeedbackCopyTexture(source.Info, view, handle.PixelFormat);
                    lease.GetUnsafe().AddView(copyTexture);

                    TextureRef original = state.TextureRefs[binding];
                    originals.Add((binding, original));
                    state.TextureRefs[binding] = original with { Storage = copyTexture };
                }
                _pipeline.EndCurrentPass();
            }
            catch (Exception failure)
            {
                foreach ((int binding, TextureRef original) in originals)
                {
                    state.TextureRefs[binding] = original;
                }
                state.Dirty |= DirtyFlags.Textures;
                lease.Dispose(); // Encoded copies keep their command-buffer dependency.
                FeedbackStats.CountStop($"copy-failed={failure.GetType().Name}:{failure.Message}");
                Logger.Error?.Print(LogClass.Gpu, $"[CouchNativeFeedback] copy failed at {context}; " +
                    $"{string.Join(" | ", aliases.Select(alias => alias.Signature))}: {failure}");
                return; // The guard rejects the unchanged draw.
            }

            foreach (var alias in aliases)
            {
                FeedbackStats.CountCopy(alias.Signature);
            }

            // The pass ended; every state category needs a new encoder bind.
            SignalRenderDirty();
            _copyState = state;
            _copyLease = lease;
            _copyOriginals = originals;
        }

        // Single-sample 2D view of one slice of a device texture; root kinds
        // whose slices are addressable by a plain copy.
        private static bool FeedbackCopyable(MTLTexture handle, RenderFeedbackGuard.Identity id)
        {
            if (id.Buffer != IntPtr.Zero || id.Layers != 1 || id.Levels < 1 ||
                handle.SampleCount != 1 || handle.TextureType != MTLTextureType.Type2D) return false;
            MTLTexture root = new(id.Root);
            if (root.SampleCount != 1 || id.Level + id.Levels > root.MipmapLevelCount) return false;
            ulong slices = root.TextureType switch
            {
                MTLTextureType.Type2D => 1,
                MTLTextureType.Type2DArray => root.ArrayLength,
                MTLTextureType.Cube => 6,
                MTLTextureType.CubeArray => 6 * root.ArrayLength,
                _ => 0,
            };
            return id.Slice < slices;
        }

        private void FinishFeedbackCopies()
        {
            Auto<FeedbackCopyLease> lease = _copyLease;
            if (lease == null) return;
            try
            {
                foreach ((int binding, TextureRef original) in _copyOriginals)
                {
                    _copyState.TextureRefs[binding] = original;
                }
                _copyState.Dirty |= DirtyFlags.Textures;
            }
            finally
            {
                _copyState = null;
                _copyLease = null;
                _copyOriginals = null;
                // Copies return to the pool only when this command buffer
                // completes; count them like disposed buffers so a frame with
                // many feedback draws is submitted before memory piles up.
                _pipeline.FlushCommandsIfWeightExceeding(lease, lease.GetUnsafe().Bytes);
                lease.Dispose(); // Command-buffer dependency persists until fence.
            }
        }

        public void FinishFeedbackDraw()
        {
            FinishFeedbackCopies();
            Auto<DepthFeedbackSnapshot> lease = _snapshotLease;
            if (lease == null) return;

            try
            {
                _snapshotState.TextureRefs[_snapshotBinding.ActualBinding] = _snapshotOriginal;
                _snapshotState.Dirty |= DirtyFlags.Textures;
            }
            finally
            {
                _snapshotState = null;
                _snapshotBinding = default;
                _snapshotLease = null;
                _snapshotCommandBuffer = IntPtr.Zero;
                _snapshotSourceRoot = IntPtr.Zero;
                _snapshotContext = null;
                _snapshotReadonlyStencil148 = false;
                lease.Dispose(); // Command-buffer dependency persists until fence.
            }
        }

        public readonly void ReportFeedbackDrawEncoded()
        {
            if (_snapshotLease == null || !DepthSnapshotSuccessEvidence.Pending) return;
            try
            {
                EncoderState state = _snapshotState;
                MTLTexture sampled = _snapshotLease.GetUnsafe().Texture.GetHandle();
                MTLPixelFormat format = state.DepthStencil.GetHandle().PixelFormat;
                DepthSnapshotSuccessEvidence.TryWrite(_snapshotContext,
                    RenderFeedbackGuard.ReadIdentity(_snapshotOriginal.Storage.GetHandle()),
                    RenderFeedbackGuard.ReadIdentity(sampled),
                    RenderFeedbackGuard.ReadIdentity(state.DepthStencil.GetHandle()),
                    _snapshotCommandBuffer, state.DepthStencilUid,
                    DepthStencilAttachmentState.Effective(state.DepthStencilUid, format),
                    _pipeline.CurrentEncoderType, state.RenderProgram, _snapshotBinding,
                    _snapshotReadonlyStencil148, state.FrontRefValue, state.BackRefValue);
            }
            catch
            {
                // Marker capture is optional. Never replace the draw result.
            }
        }

        public readonly void VerifyFeedbackDraw(string context)
        {
            if (_snapshotLease == null) return;
            Texture effective = _snapshotLease.GetUnsafe().Texture;
            MTLTexture handle = effective.GetHandle();
            RenderFeedbackGuard.Identity source =
                RenderFeedbackGuard.ReadIdentity(_snapshotOriginal.Storage.GetHandle());
            MTLRenderStages requiredStage = _snapshotBinding.Segment.Stages == ResourceStages.Vertex
                ? MTLRenderStages.RenderStageVertex : MTLRenderStages.RenderStageFragment;
            bool declaredRead = _snapshotState.RenderEncoderBindings.Resources.Any(resource =>
                resource.MtlResource.NativePtr == handle.NativePtr &&
                (resource.ResourceUsage & MTLResourceUsage.Read) != 0 &&
                (resource.Stages & requiredStage) == requiredStage);
            if (!ReferenceEquals(_currentState, _snapshotState) ||
                _pipeline.Cbs.CommandBuffer.NativePtr != _snapshotCommandBuffer ||
                !source.Known || source.Root != _snapshotSourceRoot ||
                _snapshotState.TextureRefs[_snapshotBinding.ActualBinding].Storage != effective || !declaredRead)
            {
                RenderFeedbackGuard.StopSnapshotFailure(_snapshotState,
                    _snapshotState.RenderProgram.BindingSegments, context,
                    new RenderFeedbackGuard.FailureEvidence(
                        RenderFeedbackGuard.Relation.PotentialAlias,
                        _snapshotBinding,
                        _snapshotOriginal.Storage, source,
                        new RenderFeedbackGuard.AttachmentEvidence(_snapshotState.DepthStencil,
                            RenderFeedbackGuard.ReadIdentity(_snapshotState.DepthStencil.GetHandle()),
                            "depth/stencil", "Load", "Store", "None")),
                    new InvalidOperationException("Snapshot command buffer/source/binding declaration changed."));
            }
        }

        public readonly void SignalComputeDirty()
        {
            SignalDirty(DirtyFlags.ComputeAll);
        }

        public EncoderState SwapState(EncoderState state, DirtyFlags flags = DirtyFlags.All)
        {
            _currentState = state ?? _mainState;

            SignalDirty(flags);

            return _mainState;
        }

        public PredrawState SavePredrawState()
        {
            return new PredrawState
            {
                CullMode = _currentState.CullMode,
                DepthStencilUid = _currentState.DepthStencilUid,
                Topology = _currentState.Topology,
                Viewports = _currentState.Viewports.ToArray(),
            };
        }

        public readonly void RestorePredrawState(PredrawState state)
        {
            _currentState.CullMode = state.CullMode;
            _currentState.DepthStencilUid = state.DepthStencilUid;
            _currentState.Topology = state.Topology;
            _currentState.Viewports = state.Viewports;

            SignalDirty(DirtyFlags.CullMode | DirtyFlags.DepthStencil | DirtyFlags.Viewports);
        }

        public readonly void SetClearLoadAction(bool clear)
        {
            _currentState.ClearLoadAction = clear;
        }

        public readonly void DirtyTextures()
        {
            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void DirtyImages()
        {
            SignalDirty(DirtyFlags.Images);
        }

        public readonly MTLRenderCommandEncoder CreateRenderCommandEncoder()
        {
            // Initialise Pass & State
            using MTLRenderPassDescriptor renderPassDescriptor = new();

            for (int i = 0; i < Constants.MaxColorAttachments; i++)
            {
                if (_currentState.RenderTargets[i] is Texture tex)
                {
                    MTLRenderPassColorAttachmentDescriptor passAttachment = renderPassDescriptor.ColorAttachments.Object((ulong)i);
                    tex.PopulateRenderPassAttachment(passAttachment);
                    passAttachment.LoadAction = _currentState.ClearLoadAction ? MTLLoadAction.Clear : MTLLoadAction.Load;
                    passAttachment.StoreAction = MTLStoreAction.Store;
                }
            }

            MTLRenderPassDepthAttachmentDescriptor depthAttachment = renderPassDescriptor.DepthAttachment;
            MTLRenderPassStencilAttachmentDescriptor stencilAttachment = renderPassDescriptor.StencilAttachment;

            if (_currentState.DepthStencil != null)
            {
                switch (_currentState.DepthStencil.GetHandle().PixelFormat)
                {
                    // Depth Only Attachment
                    case MTLPixelFormat.Depth16Unorm:
                    case MTLPixelFormat.Depth32Float:
                        depthAttachment.Texture = _currentState.DepthStencil.GetHandle();
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = MTLStoreAction.Store;
                        break;

                    // Stencil Only Attachment
                    case MTLPixelFormat.Stencil8:
                        stencilAttachment.Texture = _currentState.DepthStencil.GetHandle();
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = MTLStoreAction.Store;
                        break;

                    // Combined Attachment
                    case MTLPixelFormat.Depth24UnormStencil8:
                    case MTLPixelFormat.Depth32FloatStencil8:
                        depthAttachment.Texture = _currentState.DepthStencil.GetHandle();
                        depthAttachment.LoadAction = MTLLoadAction.Load;
                        depthAttachment.StoreAction = MTLStoreAction.Store;

                        stencilAttachment.Texture = _currentState.DepthStencil.GetHandle();
                        stencilAttachment.LoadAction = MTLLoadAction.Load;
                        stencilAttachment.StoreAction = MTLStoreAction.Store;
                        break;
                    default:
                        Logger.Error?.PrintMsg(LogClass.Gpu, $"Unsupported Depth/Stencil Format: {_currentState.DepthStencil.GetHandle().PixelFormat}!");
                        break;
                }
            }

            // Initialise Encoder
            MTLRenderCommandEncoder renderCommandEncoder = _pipeline.CommandBuffer.RenderCommandEncoder(renderPassDescriptor);

            return renderCommandEncoder;
        }

        public readonly MTLComputeCommandEncoder CreateComputeCommandEncoder()
        {
            using MTLComputePassDescriptor descriptor = new();
            MTLComputeCommandEncoder computeCommandEncoder = _pipeline.CommandBuffer.ComputeCommandEncoder(descriptor);

            return computeCommandEncoder;
        }

        public readonly void RenderResourcesPrepass()
        {
            _currentState.RenderEncoderBindings.Clear();

            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetVertexBuffers(_currentState.VertexBuffers, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ConstantBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.StorageBuffersSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.TexturesSetIndex, ref _currentState.RenderEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.RenderProgram, Constants.ImagesSetIndex, ref _currentState.RenderEncoderBindings);
            }
        }

        public readonly void ValidateRenderFeedback(string context)
        {
            RenderFeedbackGuard.Validate(_currentState, _currentState.RenderProgram.BindingSegments,
                $"{context};state={(ReferenceEquals(_currentState, _mainState) ? "Main" : "Helper")}");
        }

        public readonly void ComputeResourcesPrepass()
        {
            _currentState.ComputeEncoderBindings.Clear();

            if ((_currentState.Dirty & DirtyFlags.Uniforms) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ConstantBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Storages) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.StorageBuffersSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Textures) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.TexturesSetIndex, ref _currentState.ComputeEncoderBindings);
            }

            if ((_currentState.Dirty & DirtyFlags.Images) != 0)
            {
                UpdateAndBind(_currentState.ComputeProgram, Constants.ImagesSetIndex, ref _currentState.ComputeEncoderBindings);
            }
        }

        public void RebindRenderState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.RenderPipeline) != 0)
            {
                SetRenderPipelineState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthStencil) != 0)
            {
                SetDepthStencilState(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthClamp) != 0)
            {
                SetDepthClamp(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.DepthBias) != 0)
            {
                SetDepthBias(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.CullMode) != 0)
            {
                SetCullMode(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.FrontFace) != 0)
            {
                SetFrontFace(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.StencilRef) != 0)
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Viewports) != 0)
            {
                SetViewports(renderCommandEncoder);
            }

            if ((_currentState.Dirty & DirtyFlags.Scissors) != 0)
            {
                SetScissors(renderCommandEncoder);
            }

            foreach (Resource resource in _currentState.RenderEncoderBindings.Resources)
            {
                renderCommandEncoder.UseResource(resource.MtlResource, resource.ResourceUsage, resource.Stages);
            }

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.VertexBuffers)
            {
                renderCommandEncoder.SetVertexBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
            }

            foreach (BufferResource buffer in _currentState.RenderEncoderBindings.FragmentBuffers)
            {
                renderCommandEncoder.SetFragmentBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
            }

            _currentState.Dirty &= ~DirtyFlags.RenderAll;
        }

        public readonly void RebindComputeState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if ((_currentState.Dirty & DirtyFlags.ComputePipeline) != 0)
            {
                SetComputePipelineState(computeCommandEncoder);
            }

            foreach (Resource resource in _currentState.ComputeEncoderBindings.Resources)
            {
                computeCommandEncoder.UseResource(resource.MtlResource, resource.ResourceUsage);
            }

            foreach (BufferResource buffer in _currentState.ComputeEncoderBindings.Buffers)
            {
                computeCommandEncoder.SetBuffer(buffer.Buffer, buffer.Offset, buffer.Binding);
            }

            _currentState.Dirty &= ~DirtyFlags.ComputeAll;
        }

        private readonly void SetRenderPipelineState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            _currentState.RenderProgram?.ObservePipelineRequest(ref _currentState.Pipeline.Internal);
            MTLRenderPipelineState pipelineState = _currentState.Pipeline.CreateRenderPipeline(_device, _currentState.RenderProgram);

            renderCommandEncoder.SetRenderPipelineState(pipelineState);

            renderCommandEncoder.SetBlendColor(
                _currentState.BlendColor.Red,
                _currentState.BlendColor.Green,
                _currentState.BlendColor.Blue,
                _currentState.BlendColor.Alpha);
        }

        private readonly void SetComputePipelineState(MTLComputeCommandEncoder computeCommandEncoder)
        {
            if (_currentState.ComputeProgram == null)
            {
                return;
            }

            MTLComputePipelineState pipelineState = PipelineState.CreateComputePipeline(_device, _currentState.ComputeProgram);

            computeCommandEncoder.SetComputePipelineState(pipelineState);
        }

        public readonly void UpdateIndexBuffer(BufferRange buffer, IndexType type)
        {
            if (buffer.Handle != BufferHandle.Null)
            {
                _currentState.IndexBuffer = new IndexBufferState(buffer.Handle, buffer.Offset, buffer.Size, type);
            }
            else
            {
                _currentState.IndexBuffer = IndexBufferState.Null;
            }
        }

        public readonly void UpdatePrimitiveTopology(PrimitiveTopology topology)
        {
            _currentState.Topology = topology;
        }

        internal static MTLPrimitiveTopologyClass ClassForPrimitive(MTLPrimitiveType primitiveType)
        {
            return primitiveType switch
            {
                MTLPrimitiveType.Point => MTLPrimitiveTopologyClass.Point,
                MTLPrimitiveType.Line or MTLPrimitiveType.LineStrip => MTLPrimitiveTopologyClass.Line,
                MTLPrimitiveType.Triangle or MTLPrimitiveType.TriangleStrip => MTLPrimitiveTopologyClass.Triangle,
                _ => throw new InvalidOperationException($"Unsupported Metal draw primitive {primitiveType}."),
            };
        }

        internal static void ValidateTransportTopology(bool usesPerVertexTransport, MTLPrimitiveType primitiveType)
        {
            if (usesPerVertexTransport && ClassForPrimitive(primitiveType) is not
                (MTLPrimitiveTopologyClass.Triangle or MTLPrimitiveTopologyClass.Point))
            {
                throw new InvalidOperationException($"Transport supports actual Metal triangle or point primitives; draw uses {primitiveType}.");
            }
        }

        internal static DirtyFlags UpdatePipelineForPrimitive(ref PipelineState pipeline, MTLPrimitiveType primitiveType, bool usesPerVertexTransport)
        {
            MTLPrimitiveTopologyClass actualClass = usesPerVertexTransport
                ? ClassForPrimitive(primitiveType) : MTLPrimitiveTopologyClass.Unspecified;
            if (pipeline.InputPrimitiveTopologyClass == actualClass)
            {
                return DirtyFlags.None;
            }

            pipeline.InputPrimitiveTopologyClass = actualClass;
            return DirtyFlags.RenderPipeline;
        }

        // Call immediately after the draw's topology conversion and before
        // creating an encoder or its pipeline. The guest topology alone is not
        // sufficient for quads/fans and other remapped triangle draws.
        public readonly void PrepareActualDrawPrimitive(MTLPrimitiveType primitiveType)
        {
            bool usesTransport = _currentState.RenderProgram?.UsesPerVertexTransport == true;
            ValidateTransportTopology(usesTransport, primitiveType);
            Program selected = _currentState.RenderProgram?.ResolveForPrimitive(primitiveType);
            if (!ReferenceEquals(selected, _currentState.RenderProgram))
            {
                _currentState.RenderProgram = selected;
                SignalDirty(DirtyFlags.RenderPipeline | DirtyFlags.ArgBuffers);
            }
            SignalDirty(UpdatePipelineForPrimitive(ref _currentState.Pipeline, primitiveType, usesTransport));
        }

        public readonly void UpdateProgram(IProgram program)
        {
            Program prg = (Program)program;
            prg.EnsureCompiled();

            if (prg.VertexFunction == IntPtr.Zero && prg.ComputeFunction == IntPtr.Zero)
            {
                if (prg.FragmentFunction == IntPtr.Zero)
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No compute function");
                }
                else
                {
                    Logger.Error?.PrintMsg(LogClass.Gpu, "No vertex function");
                }
                return;
            }

            if (prg.VertexFunction != IntPtr.Zero)
            {
                _currentState.RenderProgram = prg;

                SignalDirty(DirtyFlags.RenderPipeline | DirtyFlags.ArgBuffers);
            }
            else if (prg.ComputeFunction != IntPtr.Zero)
            {
                _currentState.ComputeProgram = prg;

                SignalDirty(DirtyFlags.ComputePipeline | DirtyFlags.ArgBuffers);
            }
        }

        public readonly void UpdateRasterizerDiscard(bool discard)
        {
            _currentState.Pipeline.RasterizerDiscardEnable = discard;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateRenderTargets(ITexture[] colors, ITexture depthStencil)
        {
            _currentState.FramebufferUsingColorWriteMask = false;
            UpdateRenderTargetsInternal(colors, depthStencil);
        }

        public readonly void UpdateRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            ref Array8<ColorBlendStateUid> blendState = ref _currentState.Pipeline.Internal.ColorBlendState;

            for (int i = 0; i < componentMask.Length; i++)
            {
                bool red = (componentMask[i] & (0x1 << 0)) != 0;
                bool green = (componentMask[i] & (0x1 << 1)) != 0;
                bool blue = (componentMask[i] & (0x1 << 2)) != 0;
                bool alpha = (componentMask[i] & (0x1 << 3)) != 0;

                MTLColorWriteMask mask = MTLColorWriteMask.None;

                mask |= red ? MTLColorWriteMask.Red : 0;
                mask |= green ? MTLColorWriteMask.Green : 0;
                mask |= blue ? MTLColorWriteMask.Blue : 0;
                mask |= alpha ? MTLColorWriteMask.Alpha : 0;

                ref ColorBlendStateUid mtlBlend = ref blendState[i];

                // When color write mask is 0, remove all blend state to help the pipeline cache.
                // Restore it when the mask becomes non-zero.
                if (mtlBlend.WriteMask != mask)
                {
                    if (mask == 0)
                    {
                        _currentState.StoredBlend[i] = mtlBlend;

                        mtlBlend.Swap(new ColorBlendStateUid());
                    }
                    else if (mtlBlend.WriteMask == 0)
                    {
                        mtlBlend.Swap(_currentState.StoredBlend[i]);
                    }
                }

                blendState[i].WriteMask = mask;
            }

            if (_currentState.FramebufferUsingColorWriteMask)
            {
                UpdateRenderTargetsInternal(_currentState.PreMaskRenderTargets, _currentState.PreMaskDepthStencil);
            }
            else
            {
                // Requires recreating pipeline
                if (_pipeline.CurrentEncoderType == EncoderType.Render)
                {
                    _pipeline.EndCurrentPass();
                }
            }
        }

        private readonly void UpdateRenderTargetsInternal(ITexture[] colors, ITexture depthStencil)
        {
            // TBDR GPUs don't work properly if the same attachment is bound to multiple targets,
            // due to each attachment being a copy of the real attachment, rather than a direct write.
            //
            // Just try to remove duplicate attachments.
            // Save a copy of the array to rebind when mask changes.

            // Look for textures that are masked out.

            ref PipelineState pipeline = ref _currentState.Pipeline;
            ref Array8<ColorBlendStateUid> blendState = ref pipeline.Internal.ColorBlendState;

            pipeline.ColorBlendAttachmentStateCount = (uint)colors.Length;

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] == null)
                {
                    continue;
                }

                MTLColorWriteMask mtlMask = blendState[i].WriteMask;

                for (int j = 0; j < i; j++)
                {
                    // Check each binding for a duplicate binding before it.

                    if (colors[i] == colors[j])
                    {
                        // Prefer the binding with no write mask.

                        MTLColorWriteMask mtlMask2 = blendState[j].WriteMask;

                        if (mtlMask == 0)
                        {
                            colors[i] = null;
                            MaskOut(colors, depthStencil);
                        }
                        else if (mtlMask2 == 0)
                        {
                            colors[j] = null;
                            MaskOut(colors, depthStencil);
                        }
                    }
                }
            }

            _currentState.RenderTargets = new Texture[Constants.MaxColorAttachments];

            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is not Texture tex)
                {
                    blendState[i].PixelFormat = MTLPixelFormat.Invalid;

                    continue;
                }

                blendState[i].PixelFormat = tex.GetHandle().PixelFormat; // TODO: cache this
                _currentState.RenderTargets[i] = tex;
            }

            if (depthStencil is Texture depthTexture)
            {
                pipeline.DepthStencilFormat = depthTexture.GetHandle().PixelFormat; // TODO: cache this
                _currentState.DepthStencil = depthTexture;
            }
            else if (depthStencil == null)
            {
                pipeline.DepthStencilFormat = MTLPixelFormat.Invalid;
                _currentState.DepthStencil = null;
            }

            // Requires recreating pipeline
            if (_pipeline.CurrentEncoderType == EncoderType.Render)
            {
                _pipeline.EndCurrentPass();
            }
        }

        private readonly void MaskOut(ITexture[] colors, ITexture depthStencil)
        {
            if (!_currentState.FramebufferUsingColorWriteMask)
            {
                _currentState.PreMaskRenderTargets = colors;
                _currentState.PreMaskDepthStencil = depthStencil;
            }

            // If true, then the framebuffer must be recreated when the mask changes.
            _currentState.FramebufferUsingColorWriteMask = true;
        }

        public readonly void UpdateVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            vertexAttribs.CopyTo(_currentState.VertexAttribs);

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateBlendDescriptors(int index, BlendDescriptor blend)
        {
            ref ColorBlendStateUid blendState = ref _currentState.Pipeline.Internal.ColorBlendState[index];

            blendState.Enable = blend.Enable;
            blendState.AlphaBlendOperation = blend.AlphaOp.Convert();
            blendState.RgbBlendOperation = blend.ColorOp.Convert();
            blendState.SourceAlphaBlendFactor = blend.AlphaSrcFactor.Convert();
            blendState.DestinationAlphaBlendFactor = blend.AlphaDstFactor.Convert();
            blendState.SourceRGBBlendFactor = blend.ColorSrcFactor.Convert();
            blendState.DestinationRGBBlendFactor = blend.ColorDstFactor.Convert();

            if (blendState.WriteMask == 0)
            {
                _currentState.StoredBlend[index] = blendState;

                blendState.Swap(new ColorBlendStateUid());
            }

            _currentState.BlendColor = blend.BlendConstant;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateStencilState(StencilTestDescriptor stencilTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            uid.FrontFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.FrontSFail.Convert(),
                DepthFailureOperation = stencilTest.FrontDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.FrontDpPass.Convert(),
                StencilCompareFunction = stencilTest.FrontFunc.Convert(),
                ReadMask = (uint)stencilTest.FrontFuncMask,
                WriteMask = (uint)stencilTest.FrontMask
            };

            uid.BackFace = new StencilUid
            {
                StencilFailureOperation = stencilTest.BackSFail.Convert(),
                DepthFailureOperation = stencilTest.BackDpFail.Convert(),
                DepthStencilPassOperation = stencilTest.BackDpPass.Convert(),
                StencilCompareFunction = stencilTest.BackFunc.Convert(),
                ReadMask = (uint)stencilTest.BackFuncMask,
                WriteMask = (uint)stencilTest.BackMask
            };

            uid.StencilTestEnabled = stencilTest.TestEnable;

            UpdateStencilRefValue(stencilTest.FrontFuncRef, stencilTest.BackFuncRef);

            SignalDirty(DirtyFlags.DepthStencil);
        }

        public readonly void UpdateDepthState(DepthTestDescriptor depthTest)
        {
            ref DepthStencilUid uid = ref _currentState.DepthStencilUid;

            uid.DepthCompareFunction = depthTest.TestEnable ? depthTest.Func.Convert() : MTLCompareFunction.Always;
            uid.DepthWriteEnabled = depthTest.TestEnable && depthTest.WriteEnable;

            SignalDirty(DirtyFlags.DepthStencil);
        }

        public readonly void UpdateDepthClamp(bool clamp)
        {
            _currentState.DepthClipMode = clamp ? MTLDepthClipMode.Clamp : MTLDepthClipMode.Clip;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthClamp(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthClamp);
        }

        public readonly void UpdateDepthBias(float depthBias, float slopeScale, float clamp)
        {
            _currentState.DepthBias = depthBias;
            _currentState.SlopeScale = slopeScale;
            _currentState.Clamp = clamp;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetDepthBias(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.DepthBias);
        }

        public readonly void UpdateLogicOpState(bool enable, LogicalOp op)
        {
            _currentState.Pipeline.LogicOpEnable = enable;
            _currentState.Pipeline.LogicOp = op.Convert();

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateMultisampleState(MultisampleDescriptor multisample)
        {
            _currentState.Pipeline.AlphaToCoverageEnable = multisample.AlphaToCoverageEnable;
            _currentState.Pipeline.AlphaToOneEnable = multisample.AlphaToOneEnable;

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public void UpdateScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            for (int i = 0; i < regions.Length; i++)
            {
                Rectangle<int> region = regions[i];

                _currentState.Scissors[i] = new MTLScissorRect
                {
                    height = (ulong)region.Height,
                    width = (ulong)region.Width,
                    x = (ulong)region.X,
                    y = (ulong)region.Y
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetScissors(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Scissors);
        }

        public void UpdateViewports(ReadOnlySpan<Viewport> viewports)
        {
            static float Clamp(float value)
            {
                return Math.Clamp(value, 0f, 1f);
            }

            for (int i = 0; i < viewports.Length; i++)
            {
                Viewport viewport = viewports[i];
                // Y coordinate is inverted
                _currentState.Viewports[i] = new MTLViewport
                {
                    originX = viewport.Region.X,
                    originY = viewport.Region.Y + viewport.Region.Height,
                    width = viewport.Region.Width,
                    height = -viewport.Region.Height,
                    znear = Clamp(viewport.DepthNear),
                    zfar = Clamp(viewport.DepthFar)
                };
            }

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetViewports(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.Viewports);
        }

        public readonly void UpdateVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            for (int i = 0; i < Constants.MaxVertexBuffers; i++)
            {
                if (i < vertexBuffers.Length)
                {
                    VertexBufferDescriptor vertexBuffer = vertexBuffers[i];

                    _currentState.VertexBuffers[i] = new VertexBufferState(
                        vertexBuffer.Buffer.Handle,
                        vertexBuffer.Buffer.Offset,
                        vertexBuffer.Buffer.Size,
                        vertexBuffer.Divisor,
                        vertexBuffer.Stride);
                }
                else
                {
                    _currentState.VertexBuffers[i] = VertexBufferState.Null;
                }
            }

            // Update the buffers on the pipeline
            UpdatePipelineVertexState(_currentState.VertexBuffers, _currentState.VertexAttribs);

            SignalDirty(DirtyFlags.RenderPipeline);
        }

        public readonly void UpdateUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.UniformBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Uniforms);
        }

        public readonly void UpdateStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            foreach (BufferAssignment assignment in buffers)
            {
                BufferRange buffer = assignment.Range;
                int index = assignment.Binding;

                Auto<DisposableBuffer> mtlBuffer = buffer.Handle == BufferHandle.Null
                    ? null
                    : _bufferManager.GetBuffer(buffer.Handle, buffer.Write);

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer, ref buffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public readonly void UpdateStorageBuffers(int first, ReadOnlySpan<Auto<DisposableBuffer>> buffers)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                Auto<DisposableBuffer> mtlBuffer = buffers[i];
                int index = first + i;

                _currentState.StorageBufferRefs[index] = new BufferRef(mtlBuffer);
            }

            SignalDirty(DirtyFlags.Storages);
        }

        public void UpdateCullMode(bool enable, Face face)
        {
            bool dirtyScissor = (face == Face.FrontAndBack) != _currentState.CullBoth;

            _currentState.CullMode = enable ? face.Convert() : MTLCullMode.None;
            _currentState.CullBoth = face == Face.FrontAndBack;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetCullMode(renderCommandEncoder);
                SetScissors(renderCommandEncoder);
                return;
            }

            // Mark dirty
            SignalDirty(DirtyFlags.CullMode);

            if (dirtyScissor)
            {
                SignalDirty(DirtyFlags.Scissors);
            }
        }

        public readonly void UpdateFrontFace(FrontFace frontFace)
        {
            _currentState.Winding = frontFace.Convert();

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetFrontFace(renderCommandEncoder);
                return;
            }

            SignalDirty(DirtyFlags.FrontFace);
        }

        private readonly void UpdateStencilRefValue(int frontRef, int backRef)
        {
            _currentState.FrontRefValue = frontRef;
            _currentState.BackRefValue = backRef;

            // Inline update
            if (_pipeline.Encoders.TryGetRenderEncoder(out MTLRenderCommandEncoder renderCommandEncoder))
            {
                SetStencilRefValue(renderCommandEncoder);
            }

            SignalDirty(DirtyFlags.StencilRef);
        }

        public readonly void UpdateTextureAndSampler(ShaderStage stage, int binding, TextureBase texture, SamplerHolder samplerHolder)
        {
            if (texture != null)
            {
                _currentState.TextureRefs[binding] = new(stage, texture, samplerHolder?.GetSampler());
            }
            else
            {
                _currentState.TextureRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Textures);
        }

        public readonly void UpdateImage(ShaderStage stage, int binding, TextureBase image)
        {
            if (image is Texture view)
            {
                _currentState.ImageRefs[binding] = new(stage, view);
            }
            else
            {
                _currentState.ImageRefs[binding] = default;
            }

            SignalDirty(DirtyFlags.Images);
        }

        public readonly void UpdateTextureArray(ShaderStage stage, int binding, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateTextureArraySeparate(ShaderStage stage, int setIndex, TextureArray array)
        {
            ref EncoderState.ArrayRef<TextureArray> arrayRef = ref GetArrayRef(ref _currentState.TextureArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<TextureArray>(stage, array);

                SignalDirty(DirtyFlags.Textures);
            }
        }

        public readonly void UpdateImageArray(ShaderStage stage, int binding, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayRefs, binding, ArrayGrowthSize);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        public readonly void UpdateImageArraySeparate(ShaderStage stage, int setIndex, ImageArray array)
        {
            ref EncoderState.ArrayRef<ImageArray> arrayRef = ref GetArrayRef(ref _currentState.ImageArrayExtraRefs, setIndex - MetalRenderer.TotalSets);

            if (arrayRef.Stage != stage || arrayRef.Array != array)
            {
                arrayRef = new EncoderState.ArrayRef<ImageArray>(stage, array);

                SignalDirty(DirtyFlags.Images);
            }
        }

        private static ref EncoderState.ArrayRef<T> GetArrayRef<T>(ref EncoderState.ArrayRef<T>[] array, int index, int growthSize = 1)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            if (array.Length <= index)
            {
                Array.Resize(ref array, index + growthSize);
            }

            return ref array[index];
        }

        private readonly void SetDepthStencilState(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (DepthStencil != null)
            {
                DepthStencilUid effective = DepthStencilAttachmentState.Effective(
                    _currentState.DepthStencilUid, DepthStencil.GetHandle().PixelFormat);
                MTLDepthStencilState state = _depthStencilCache.GetOrCreate(effective);

                renderCommandEncoder.SetDepthStencilState(state);
            }
            else
            {
                renderCommandEncoder.SetDepthStencilState(_defaultState);
            }
        }

        private readonly void SetDepthClamp(MTLRenderCommandEncoder renderCommandEncoder)
        {
            renderCommandEncoder.SetDepthClipMode(_currentState.DepthClipMode);
        }

        private readonly void SetDepthBias(MTLRenderCommandEncoder renderCommandEncoder)
        {
            renderCommandEncoder.SetDepthBias(_currentState.DepthBias, _currentState.SlopeScale, _currentState.Clamp);
        }

        private unsafe void SetScissors(MTLRenderCommandEncoder renderCommandEncoder)
        {
            bool isTriangles = (_currentState.Topology == PrimitiveTopology.Triangles) ||
                               (_currentState.Topology == PrimitiveTopology.TriangleStrip);

            if (_currentState.CullBoth && isTriangles)
            {
                renderCommandEncoder.SetScissorRect(new MTLScissorRect { x = 0, y = 0, width = 0, height = 0 });
            }
            else
            {
                if (_currentState.Scissors.Length > 0)
                {
                    // Metal requires the scissor to fit the current attachment
                    // extent. Keep the guest state intact for later larger targets.
                    ulong width = ulong.MaxValue;
                    ulong height = ulong.MaxValue;
                    foreach (Texture target in _currentState.RenderTargets)
                    {
                        if (target != null)
                        {
                            MTLTexture texture = target.GetHandle();
                            width = Math.Min(width, texture.Width);
                            height = Math.Min(height, texture.Height);
                        }
                    }
                    if (_currentState.DepthStencil != null)
                    {
                        MTLTexture texture = _currentState.DepthStencil.GetHandle();
                        width = Math.Min(width, texture.Width);
                        height = Math.Min(height, texture.Height);
                    }
                    if (width == ulong.MaxValue || height == ulong.MaxValue)
                        throw new NotSupportedException("Cannot set Metal scissors without a render attachment extent.");

                    Span<MTLScissorRect> clipped = stackalloc MTLScissorRect[_currentState.Scissors.Length];
                    for (int index = 0; index < clipped.Length; index++)
                        clipped[index] = ScissorBounds.Clip(_currentState.Scissors[index], width, height);
                    fixed (MTLScissorRect* pMtlScissors = clipped)
                    {
                        renderCommandEncoder.SetScissorRects((IntPtr)pMtlScissors, (ulong)clipped.Length);
                    }
                }
            }
        }

        private readonly unsafe void SetViewports(MTLRenderCommandEncoder renderCommandEncoder)
        {
            if (_currentState.Viewports.Length > 0)
            {
                fixed (MTLViewport* pMtlViewports = _currentState.Viewports)
                {
                    renderCommandEncoder.SetViewports((IntPtr)pMtlViewports, (ulong)_currentState.Viewports.Length);
                }
            }
        }

        private readonly void UpdatePipelineVertexState(VertexBufferState[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            ApplyVertexState(ref _currentState.Pipeline, bufferDescriptors, attribDescriptors);
        }

        // Shared with SpeculativePipelines so a predicted key is built by the same code.
        internal static void ApplyVertexState(ref PipelineState pipeline, VertexBufferState[] bufferDescriptors, VertexAttribDescriptor[] attribDescriptors)
        {
            uint indexMask = 0;

            for (int i = 0; i < attribDescriptors.Length; i++)
            {
                ref VertexInputAttributeUid attrib = ref pipeline.Internal.VertexAttributes[i];

                if (attribDescriptors[i].IsZero)
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << (int)Constants.ZeroBufferIndex;
                    attrib.BufferIndex = Constants.ZeroBufferIndex;
                    attrib.Offset = 0;
                }
                else
                {
                    attrib.Format = attribDescriptors[i].Format.Convert();
                    indexMask |= 1u << attribDescriptors[i].BufferIndex;
                    attrib.BufferIndex = (ulong)attribDescriptors[i].BufferIndex;
                    attrib.Offset = (ulong)attribDescriptors[i].Offset;
                }
            }

            for (int i = 0; i < bufferDescriptors.Length; i++)
            {
                ref VertexInputLayoutUid layout = ref pipeline.Internal.VertexBindings[i];

                if ((indexMask & (1u << i)) != 0)
                {
                    layout.Stride = (uint)bufferDescriptors[i].Stride;

                    if (layout.Stride == 0)
                    {
                        layout.Stride = 1;
                        layout.StepFunction = MTLVertexStepFunction.Constant;
                        layout.StepRate = 0;
                    }
                    else
                    {
                        if (bufferDescriptors[i].Divisor > 0)
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerInstance;
                            layout.StepRate = (uint)bufferDescriptors[i].Divisor;
                        }
                        else
                        {
                            layout.StepFunction = MTLVertexStepFunction.PerVertex;
                            layout.StepRate = 1;
                        }
                    }
                }
                else
                {
                    layout = new();
                }
            }

            ref VertexInputLayoutUid zeroBufLayout = ref pipeline.Internal.VertexBindings[(int)Constants.ZeroBufferIndex];

            // Zero buffer
            if ((indexMask & (1u << (int)Constants.ZeroBufferIndex)) != 0)
            {
                zeroBufLayout.Stride = 1;
                zeroBufLayout.StepFunction = MTLVertexStepFunction.Constant;
                zeroBufLayout.StepRate = 0;
            }
            else
            {
                zeroBufLayout = new();
            }

            pipeline.VertexAttributeDescriptionsCount = (uint)attribDescriptors.Length;
            pipeline.VertexBindingDescriptionsCount = Constants.ZeroBufferIndex + 1; // TODO: move this out?
        }

        private readonly void SetVertexBuffers(VertexBufferState[] bufferStates, ref readonly RenderEncoderBindings bindings)
        {
            for (int i = 0; i < bufferStates.Length; i++)
            {
                (MTLBuffer mtlBuffer, int offset) = bufferStates[i].GetVertexBuffer(_bufferManager, _pipeline.Cbs);

                if (mtlBuffer.NativePtr != IntPtr.Zero)
                {
                    bindings.VertexBuffers.Add(new BufferResource(mtlBuffer, (ulong)offset, (ulong)i));
                }
            }

            Auto<DisposableBuffer> autoZeroBuffer = _zeroBuffer == BufferHandle.Null
                ? null
                : _bufferManager.GetBuffer(_zeroBuffer, false);

            if (autoZeroBuffer == null)
            {
                return;
            }

            MTLBuffer zeroMtlBuffer = autoZeroBuffer.Get(_pipeline.Cbs).Value;
            bindings.VertexBuffers.Add(new BufferResource(zeroMtlBuffer, 0, Constants.ZeroBufferIndex));
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForBuffer(ref BufferRef buffer)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            BufferRange? range = buffer.Range;
            Auto<DisposableBuffer> autoBuffer = buffer.Buffer;

            if (autoBuffer != null)
            {
                int offset = 0;
                MTLBuffer mtlBuffer;

                if (range.HasValue)
                {
                    offset = range.Value.Offset;
                    mtlBuffer = autoBuffer.Get(_pipeline.Cbs, offset, range.Value.Size, range.Value.Write).Value;
                }
                else
                {
                    mtlBuffer = autoBuffer.Get(_pipeline.Cbs).Value;
                }

                gpuAddress = mtlBuffer.GpuAddress + (ulong)offset;
                nativePtr = mtlBuffer.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTexture(ref TextureRef texture)
        {
            TextureBase storage = texture.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                if (storage is TextureBuffer textureBuffer)
                {
                    textureBuffer.RebuildStorage(false);
                }

                MTLTexture mtlTexture = storage.GetHandle();

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForImage(ref ImageRef image)
        {
            Texture storage = image.Storage;

            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (storage != null)
            {
                MTLTexture mtlTexture = storage.GetHandle();

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        private readonly (ulong gpuAddress, IntPtr nativePtr) AddressForTextureBuffer(ref TextureBuffer bufferTexture)
        {
            ulong gpuAddress = 0;
            IntPtr nativePtr = IntPtr.Zero;

            if (bufferTexture != null)
            {
                bufferTexture.RebuildStorage(false);

                MTLTexture mtlTexture = bufferTexture.GetHandle();

                gpuAddress = mtlTexture.GpuResourceID._impl;
                nativePtr = mtlTexture.NativePtr;
            }

            return (gpuAddress, nativePtr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, MTLRenderStages stages, ref readonly RenderEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, stages));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddResource(IntPtr resourcePointer, MTLResourceUsage usage, ref readonly ComputeEncoderBindings bindings)
        {
            if (resourcePointer != IntPtr.Zero)
            {
                bindings.Resources.Add(new Resource(new MTLResource(resourcePointer), usage, 0));
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly RenderEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer vertArgBuffer = default;
            ScopedTemporaryBuffer fragArgBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                vertArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong));
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.FragArgumentBufferSizes[setIndex] * sizeof(ulong));
            }

            Span<ulong> vertResourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            Span<ulong> fragResourceIds = stackalloc ulong[program.FragArgumentBufferSizes[setIndex]];

            int vertResourceIdIndex = 0;
            int fragResourceIdIndex = 0;
            // The identity is only set when the diagnostics were on at program creation; testing it first keeps
            // this per-draw path from reading the environment.
            bool captureCubeBindings = setIndex == Constants.TexturesSetIndex &&
                program.CubeDiagnosticIdentity != null && CubeBindingDiagnostics.Enabled;
            List<Dictionary<string, object>> cubeBindingRecords = captureCubeBindings ? [] : null;
            List<Dictionary<string, object>> cubeEmittedSlots = captureCubeBindings ? [] : null;
            int expectedFragmentSlotOffset = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            MTLRenderStages renderStages = 0;

                            if ((segment.Stages & ResourceStages.Vertex) != 0)
                            {
                                vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                vertResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageVertex;
                            }

                            if ((segment.Stages & ResourceStages.Fragment) != 0)
                            {
                                fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                fragResourceIdIndex++;

                                renderStages |= MTLRenderStages.RenderStageFragment;
                            }

                            // The layout coalesces bindings without retaining its write bit.
                            // Conservatively declare possible writes, as the compute path does.
                            AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                        }
                        break;
                    case Constants.TexturesSetIndex:
                    {
                        int expectedSegmentFirstSlot = expectedFragmentSlotOffset;
                        int expectedPointersPerBinding = segment.Type == ResourceType.TextureAndSampler ? 2 : 1;
                        bool captureFragmentSegment = captureCubeBindings &&
                            (segment.Stages & ResourceStages.Fragment) != 0;
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref texture);

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                    vertResourceIdIndex++;

                                    // Argument slots follow the shader layout, including absent samplers.
                                    if (segment.Type == ResourceType.TextureAndSampler)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        vertResourceIdIndex++;
                                    }

                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    int actualTextureSlot = fragResourceIdIndex;
                                    fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                    fragResourceIdIndex++;
                                    if (captureCubeBindings)
                                    {
                                        cubeEmittedSlots.Add(CubeBindingDiagnostics.EmittedSlot(
                                            actualTextureSlot, index, null, "texture", gpuAddress));
                                    }

                                    int? actualSamplerSlot = null;
                                    ulong? samplerGpuResourceId = null;

                                    if (segment.Type == ResourceType.TextureAndSampler)
                                    {
                                        ulong samplerGpuAddress = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        actualSamplerSlot = fragResourceIdIndex;
                                        samplerGpuResourceId = texture.Sampler != null ? samplerGpuAddress : null;
                                        fragResourceIds[fragResourceIdIndex] = samplerGpuAddress;
                                        fragResourceIdIndex++;
                                        if (captureCubeBindings)
                                        {
                                            cubeEmittedSlots.Add(CubeBindingDiagnostics.EmittedSlot(
                                                actualSamplerSlot.Value, index, null, "sampler", samplerGpuAddress));
                                        }
                                    }

                                    if (captureFragmentSegment)
                                    {
                                        int expectedFirstSlot = expectedSegmentFirstSlot + i * expectedPointersPerBinding;
                                        bool snapshotReplacement = _snapshotLease != null &&
                                            ReferenceEquals(texture.Storage, _snapshotLease.GetUnsafe().Texture);
                                        TextureBase originalStorage = snapshotReplacement
                                            ? _snapshotOriginal.Storage
                                            : texture.Storage;
                                        cubeBindingRecords.Add(CubeBindingDiagnostics.BindingRecord(
                                            (int)setIndex, segment, index, null, null,
                                            expectedFirstSlot, expectedPointersPerBinding,
                                            expectedPointersPerBinding == 2 ? expectedFirstSlot + 1 : null,
                                            actualTextureSlot, gpuAddress, originalStorage, texture.Storage, snapshotReplacement,
                                            expectedPointersPerBinding == 2, actualSamplerSlot, samplerGpuResourceId));
                                    }

                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }

                                AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                Auto<DisposableSampler>[] samplers = new Auto<DisposableSampler>[textures.Length];
                                int[] diagnosticTextureSlots = captureFragmentSegment ? new int[textures.Length] : null;
                                ulong[] diagnosticTextureIds = captureFragmentSegment ? new ulong[textures.Length] : null;
                                int[] diagnosticSamplerSlots = captureFragmentSegment ? new int[textures.Length] : null;
                                ulong[] diagnosticSamplerIds = captureFragmentSegment ? new ulong[textures.Length] : null;

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref texture);

                                    samplers[i] = texture.Sampler;

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        int actualTextureSlot = fragResourceIdIndex;
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        if (captureCubeBindings)
                                        {
                                            cubeEmittedSlots.Add(CubeBindingDiagnostics.EmittedSlot(
                                                actualTextureSlot, binding, i, "texture", gpuAddress));
                                        }
                                        if (captureFragmentSegment)
                                        {
                                            diagnosticTextureSlots[i] = actualTextureSlot;
                                            diagnosticTextureIds[i] = gpuAddress;
                                        }

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }

                                if (segment.Type == ResourceType.TextureAndSampler)
                                {
                                    for (int i = 0; i < samplers.Length; i++)
                                    {
                                        Auto<DisposableSampler> sampler = samplers[i];
                                        ulong gpuAddress = 0;

                                        if (sampler != null)
                                        {
                                            gpuAddress = sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl;
                                        }

                                        if ((segment.Stages & ResourceStages.Vertex) != 0)
                                        {
                                            vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                            vertResourceIdIndex++;
                                        }

                                        if ((segment.Stages & ResourceStages.Fragment) != 0)
                                        {
                                            int actualSamplerSlot = fragResourceIdIndex;
                                            fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                            fragResourceIdIndex++;
                                            if (captureCubeBindings)
                                            {
                                                cubeEmittedSlots.Add(CubeBindingDiagnostics.EmittedSlot(
                                                    actualSamplerSlot, binding, i,
                                                    "sampler", gpuAddress));
                                            }
                                            if (captureFragmentSegment)
                                            {
                                                diagnosticSamplerSlots[i] = actualSamplerSlot;
                                                diagnosticSamplerIds[i] = gpuAddress;
                                            }
                                        }
                                    }
                                }

                                if (captureFragmentSegment)
                                {
                                    for (int i = 0; i < textures.Length; i++)
                                    {
                                        TextureRef texture = textures[i];
                                        bool samplerWasNull = samplers[i] == null;
                                        bool snapshotReplacement = _snapshotLease != null &&
                                            ReferenceEquals(texture.Storage, _snapshotLease.GetUnsafe().Texture);
                                        Dictionary<string, object> record = CubeBindingDiagnostics.BindingRecord(
                                            (int)setIndex, segment, binding, i, textures.Length,
                                            expectedSegmentFirstSlot + i, expectedPointersPerBinding,
                                            expectedPointersPerBinding == 2
                                                ? expectedSegmentFirstSlot + segment.Count + i
                                                : null,
                                            diagnosticTextureSlots[i], diagnosticTextureIds[i],
                                            texture.Storage, texture.Storage, snapshotReplacement,
                                            expectedPointersPerBinding == 2,
                                            expectedPointersPerBinding == 2 ? diagnosticSamplerSlots[i] : null,
                                            expectedPointersPerBinding == 2 && !samplerWasNull ? diagnosticSamplerIds[i] : null);
                                        record["samplerWasNull"] = samplerWasNull;
                                        cubeBindingRecords.Add(record);
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;

                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        int actualTextureSlot = fragResourceIdIndex;
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        if (captureFragmentSegment)
                                        {
                                            cubeEmittedSlots.Add(CubeBindingDiagnostics.EmittedSlot(
                                                actualTextureSlot, binding, i, "texture-buffer", gpuAddress));
                                            cubeBindingRecords.Add(CubeBindingDiagnostics.BindingRecord(
                                                (int)setIndex, segment, binding, i, bufferTextures.Length,
                                                expectedSegmentFirstSlot + i * expectedPointersPerBinding,
                                                expectedPointersPerBinding, null, actualTextureSlot, gpuAddress,
                                                bufferTexture, bufferTexture, snapshotReplacement: false, samplerExpected: false,
                                                actualSamplerSlot: null, samplerGpuResourceId: null));
                                        }

                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read, renderStages, in bindings);
                                }
                            }
                        }
                        if ((segment.Stages & ResourceStages.Fragment) != 0)
                        {
                            expectedFragmentSlotOffset += segment.Count * expectedPointersPerBinding;
                        }
                        break;
                    }
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                MTLRenderStages renderStages = 0;

                                if ((segment.Stages & ResourceStages.Vertex) != 0)
                                {
                                    vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                    vertResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageVertex;
                                }

                                if ((segment.Stages & ResourceStages.Fragment) != 0)
                                {
                                    fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                    fragResourceIdIndex++;
                                    renderStages |= MTLRenderStages.RenderStageFragment;
                                }

                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    MTLRenderStages renderStages = 0;

                                    if ((segment.Stages & ResourceStages.Vertex) != 0)
                                    {
                                        vertResourceIds[vertResourceIdIndex] = gpuAddress;
                                        vertResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageVertex;
                                    }

                                    if ((segment.Stages & ResourceStages.Fragment) != 0)
                                    {
                                        fragResourceIds[fragResourceIdIndex] = gpuAddress;
                                        fragResourceIdIndex++;
                                        renderStages |= MTLRenderStages.RenderStageFragment;
                                    }

                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, renderStages, in bindings);
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                vertArgBuffer.Holder.SetDataUnchecked(vertArgBuffer.Offset, MemoryMarshal.AsBytes(vertResourceIds));
                MTLBuffer mtlVertArgBuffer = _bufferManager.GetBuffer(vertArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.VertexBuffers.Add(new BufferResource(mtlVertArgBuffer, (uint)vertArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }

            if (program.FragArgumentBufferSizes[setIndex] > 0)
            {
                fragArgBuffer.Holder.SetDataUnchecked(fragArgBuffer.Offset, MemoryMarshal.AsBytes(fragResourceIds));
                MTLBuffer mtlFragArgBuffer = _bufferManager.GetBuffer(fragArgBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.FragmentBuffers.Add(new BufferResource(mtlFragArgBuffer, (uint)fragArgBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }

            if (captureCubeBindings && cubeBindingRecords.Count != 0)
            {
                CubeBindingDiagnostics.RecordArgumentBuffer(program, _cubeDiagnosticDrawContext,
                    _pipeline.Cbs.CommandBuffer.NativePtr, _currentState.Pipeline.Internal.Id0,
                    _currentState.Pipeline.Internal.Id1, (int)setIndex,
                    program.FragArgumentBufferSizes[setIndex], fragResourceIdIndex,
                    cubeBindingRecords, cubeEmittedSlots);
            }
        }

        private readonly void UpdateAndBind(Program program, uint setIndex, ref readonly ComputeEncoderBindings bindings)
        {
            ResourceBindingSegment[] bindingSegments = program.BindingSegments[setIndex];

            if (bindingSegments.Length == 0)
            {
                return;
            }

            ScopedTemporaryBuffer argBuffer = default;

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer = _bufferManager.ReserveOrCreate(_pipeline.Cbs, program.ArgumentBufferSizes[setIndex] * sizeof(ulong));
            }

            Span<ulong> resourceIds = stackalloc ulong[program.ArgumentBufferSizes[setIndex]];
            int resourceIdIndex = 0;

            foreach (ResourceBindingSegment segment in bindingSegments)
            {
                int binding = segment.Binding;
                int count = segment.Count;

                switch (setIndex)
                {
                    case Constants.ConstantBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.UniformBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                bindings.Resources.Add(new Resource(new MTLResource(nativePtr), MTLResourceUsage.Read, 0));
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.StorageBuffersSetIndex:
                        for (int i = 0; i < count; i++)
                        {
                            int index = binding + i;

                            ref BufferRef buffer = ref _currentState.StorageBufferRefs[index];
                            (ulong gpuAddress, IntPtr nativePtr) = AddressForBuffer(ref buffer);

                            if ((segment.Stages & ResourceStages.Compute) != 0)
                            {
                                AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                resourceIds[resourceIdIndex] = gpuAddress;
                                resourceIdIndex++;
                            }
                        }
                        break;
                    case Constants.TexturesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref TextureRef texture = ref _currentState.TextureRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref texture);

                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                    resourceIds[resourceIdIndex] = gpuAddress;
                                    resourceIdIndex++;

                                    if (segment.Type == ResourceType.TextureAndSampler)
                                    {
                                        resourceIds[resourceIdIndex] = texture.Sampler != null
                                            ? texture.Sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        else
                        {
                            TextureArray textureArray = _currentState.TextureArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferTexture)
                            {
                                TextureRef[] textures = textureArray.GetTextureRefs();
                                Auto<DisposableSampler>[] samplers = new Auto<DisposableSampler>[textures.Length];

                                for (int i = 0; i < textures.Length; i++)
                                {
                                    TextureRef texture = textures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref texture);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;

                                        samplers[i] = texture.Sampler;
                                    }
                                }

                                if (segment.Type == ResourceType.TextureAndSampler)
                                {
                                    foreach (Auto<DisposableSampler> sampler in samplers)
                                    {
                                        resourceIds[resourceIdIndex] = sampler != null
                                            ? sampler.Get(_pipeline.Cbs).Value.GpuResourceID._impl
                                            : 0;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferTextures = textureArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferTextures.Length; i++)
                                {
                                    TextureBuffer bufferTexture = bufferTextures[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref bufferTexture);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                    case Constants.ImagesSetIndex:
                        if (!segment.IsArray)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                int index = binding + i;

                                ref ImageRef image = ref _currentState.ImageRefs[index];
                                (ulong gpuAddress, IntPtr nativePtr) = AddressForImage(ref image);

                                if ((segment.Stages & ResourceStages.Compute) != 0)
                                {
                                    AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                    resourceIds[resourceIdIndex] = gpuAddress;
                                    resourceIdIndex++;
                                }
                            }
                        }
                        else
                        {
                            ImageArray imageArray = _currentState.ImageArrayRefs[binding].Array;

                            if (segment.Type != ResourceType.BufferImage)
                            {
                                TextureRef[] images = imageArray.GetTextureRefs();

                                for (int i = 0; i < images.Length; i++)
                                {
                                    TextureRef image = images[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTexture(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                            else
                            {
                                TextureBuffer[] bufferImages = imageArray.GetBufferTextureRefs();

                                for (int i = 0; i < bufferImages.Length; i++)
                                {
                                    TextureBuffer image = bufferImages[i];
                                    (ulong gpuAddress, IntPtr nativePtr) = AddressForTextureBuffer(ref image);

                                    if ((segment.Stages & ResourceStages.Compute) != 0)
                                    {
                                        AddResource(nativePtr, MTLResourceUsage.Read | MTLResourceUsage.Write, in bindings);
                                        resourceIds[resourceIdIndex] = gpuAddress;
                                        resourceIdIndex++;
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            if (program.ArgumentBufferSizes[setIndex] > 0)
            {
                argBuffer.Holder.SetDataUnchecked(argBuffer.Offset, MemoryMarshal.AsBytes(resourceIds));
                MTLBuffer mtlArgBuffer = _bufferManager.GetBuffer(argBuffer.Handle, false).Get(_pipeline.Cbs).Value;
                bindings.Buffers.Add(new BufferResource(mtlArgBuffer, (uint)argBuffer.Range.Offset, SetIndexToBindingIndex(setIndex)));
            }
        }

        private static uint SetIndexToBindingIndex(uint setIndex)
        {
            return setIndex switch
            {
                Constants.ConstantBuffersSetIndex => Constants.ConstantBuffersIndex,
                Constants.StorageBuffersSetIndex => Constants.StorageBuffersIndex,
                Constants.TexturesSetIndex => Constants.TexturesIndex,
                Constants.ImagesSetIndex => Constants.ImagesIndex,
                _ => throw new NotImplementedException()
            };
        }

        private readonly void SetCullMode(MTLRenderCommandEncoder renderCommandEncoder)
        {
            renderCommandEncoder.SetCullMode(_currentState.CullMode);
        }

        private readonly void SetFrontFace(MTLRenderCommandEncoder renderCommandEncoder)
        {
            renderCommandEncoder.SetFrontFacingWinding(_currentState.Winding);
        }

        private readonly void SetStencilRefValue(MTLRenderCommandEncoder renderCommandEncoder)
        {
            renderCommandEncoder.SetStencilReferenceValues((uint)_currentState.FrontRefValue, (uint)_currentState.BackRefValue);
        }
    }
}
