using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // Strict private-prototype diagnostic. This detects unsupported feedback; it
    // does not implement framebuffer fetch, a copy/ping-pong path or range proofs.
    [SupportedOSPlatform("macos")]
    static class RenderFeedbackGuard
    {
        internal readonly record struct Identity(
            IntPtr View, IntPtr Root, IntPtr Buffer, ulong GpuId,
            ulong Level, ulong Slice, ulong Levels, ulong Layers,
            MTLPixelFormat Format, MTLTextureType Type, string Fault)
        {
            public bool Known => View != IntPtr.Zero && Root != IntPtr.Zero && Fault == null;
        }

        internal readonly record struct BindingEvidence(
            int Set,
            ResourceBindingSegment Segment,
            int ActualBinding,
            int GroupedOffset,
            int? ArrayIndex,
            int? ArrayLength)
        {
            public string Label =>
                $"set={Set};segmentBinding={Segment.Binding};segmentCount={Segment.Count};" +
                $"segmentIsArray={Segment.IsArray};stages={Segment.Stages};kind={Segment.Type};" +
                (Segment.IsArray
                    ? $"actualBinding={ActualBinding};arrayIndex={ArrayIndex};arrayLength={ArrayLength}"
                    : $"actualBinding={ActualBinding};groupedOffset={GroupedOffset}");
        }

        internal readonly record struct AttachmentEvidence(
            Texture Texture,
            Identity Identity,
            string Role,
            string LoadPolicy,
            string StorePolicy,
            string ResolvePolicy);

        internal sealed record FailureEvidence(
            Relation Relation,
            BindingEvidence Binding,
            TextureBase SampledTexture,
            Identity ShaderIdentity,
            AttachmentEvidence Attachment);

        internal sealed class FeedbackGuardException : NotSupportedException
        {
            internal FailureEvidence Evidence { get; }

            internal FeedbackGuardException(string message, FailureEvidence evidence) : base(message)
            {
                Evidence = evidence;
            }
        }

        internal enum Relation { Disjoint, PotentialAlias, Unknown }

        // All Metal wrappers in this method are borrowed. Do not Dispose them.
        internal static Identity ReadIdentity(MTLTexture texture)
        {
            if (texture.NativePtr == IntPtr.Zero)
            {
                return new Identity(default, default, default, 0, 0, 0, 0, 0, 0, 0, "zero native handle");
            }

            MTLTexture current = texture;
            Span<IntPtr> visited = stackalloc IntPtr[32];
            ulong level = 0;
            ulong slice = 0;
            for (int depth = 0; depth < visited.Length; depth++)
            {
                IntPtr pointer = current.NativePtr;
                if (visited[..depth].Contains(pointer))
                {
                    return Unknown(texture, "parent cycle");
                }
                visited[depth] = pointer;

                MTLTexture parent = current.ParentTexture;
                if (parent.NativePtr == IntPtr.Zero)
                {
                    IntPtr buffer = current.Buffer.NativePtr;
                    IntPtr legacyRoot = current.RootResource.NativePtr;
                    // RootResource has erased the resource type. If its result is
                    // not explained by the texture/buffer chain, fail conservatively.
                    if (legacyRoot != IntPtr.Zero && legacyRoot != pointer && legacyRoot != buffer)
                    {
                        return Unknown(texture, "unresolved legacy root resource");
                    }
                    return new Identity(texture.NativePtr, pointer, buffer, texture.GpuResourceID._impl,
                        level, slice, texture.MipmapLevelCount, texture.ArrayLength,
                        texture.PixelFormat, texture.TextureType, null);
                }

                try
                {
                    level = checked(level + current.ParentRelativeLevel);
                    slice = checked(slice + current.ParentRelativeSlice);
                }
                catch (OverflowException)
                {
                    return Unknown(texture, "view range overflow");
                }
                current = parent;
            }
            return Unknown(texture, "parent depth exceeds 32");
        }

        private static Identity Unknown(MTLTexture texture, string fault) =>
            new(texture.NativePtr, default, default, 0, 0, 0, 0, 0, 0, 0, fault);

        internal static Relation Compare(Identity left, Identity right)
        {
            if (!left.Known || !right.Known)
            {
                return Relation.Unknown;
            }
            if (left.View == right.View || left.Root == right.Root ||
                (left.Buffer != IntPtr.Zero && left.Buffer == right.Buffer))
            {
                return Relation.PotentialAlias;
            }
            // Source allocation contract: device-created textures, drawable views
            // and buffer textures. No heap aliasing or imported shared allocations.
            return Relation.Disjoint;
        }

        // A conservative read-only classifier, not a replacement for Validate.
        // Check every active sampled binding and attachment before considering a
        // copy. Any other alias or unknown identity leaves the rejecting guard in
        // charge. The shader/layout gate is also enforced by the caller.
        internal static FailureEvidence FindRecoveredDepthAlias(EncoderState state, ResourceBindingSegment[][] sets)
        {
            if (sets == null || sets.Length != MetalRenderer.TotalSets ||
                sets[0] == null || sets[1] == null || sets[2] == null || sets[3] == null ||
                sets[1].Length != 0 || sets[3].Length != 0 ||
                !ReadOnlyUniformLayout(sets[0]) ||
                !ReadOnlyTextureLayout(sets[2]) ||
                state.DepthStencil is not Texture depth)
            {
                return null;
            }

            Identity depthId = ReadIdentity(depth.GetHandle());
            if (!depthId.Known) return null;
            List<AttachmentEvidence> attachments = new(Constants.MaxColorAttachments + 1);
            for (int index = 0; index < state.RenderTargets.Length; index++)
            {
                Texture target = state.RenderTargets[index];
                if (target == null) continue;
                Identity identity = ReadIdentity(target.GetRenderAttachmentHandle());
                if (!identity.Known || Compare(identity, depthId) != Relation.Disjoint) return null;
                attachments.Add(new(target, identity, $"color[{index}]", "Load", "Store", "None"));
            }
            AttachmentEvidence depthAttachment = new(depth, depthId,
                $"depth/stencil:{depth.MtlFormat}", "Depth=Load;Stencil=Load", "Depth=Store;Stencil=Store", "None");
            attachments.Add(depthAttachment);

            FailureEvidence found = null;
            foreach (ResourceBindingSegment segment in sets[2])
            {
                for (int index = 0; index < segment.Count; index++)
                {
                    TextureBase sampled = state.TextureRefs[segment.Binding + index].Storage;
                    if (sampled == null) continue;
                    Identity sampledId = ReadIdentity(sampled.GetHandle());
                    if (!sampledId.Known) return null;
                    foreach (AttachmentEvidence attachment in attachments)
                    {
                        Relation relation = Compare(sampledId, attachment.Identity);
                        if (relation == Relation.Unknown) return null;
                        if (relation == Relation.Disjoint) continue;
                        // Position does not change the copy contract. Scan the entire
                        // segment before returning: a second alias, a color alias or
                        // an unknown later identity must still reject preparation.
                        if (found != null || attachment.Texture != depth) return null;
                        found = new FailureEvidence(relation,
                            new BindingEvidence(2, segment, segment.Binding + index, index, null, null),
                            sampled, sampledId, attachment);
                    }
                }
            }
            return found;
        }

        private static bool ReadOnlyTextureLayout(ResourceBindingSegment[] segments)
        {
            if (segments.Length is < 1 or > 2) return false;
            bool sawVertex = false;
            bool sawFragment = false;
            foreach (ResourceBindingSegment segment in segments)
            {
                if (segment.IsArray || segment.Type != ResourceType.TextureAndSampler ||
                    segment.Count is < 1 or > Constants.MaxTexturesPerStage) return false;
                if (segment.Stages == ResourceStages.Vertex && segment.Binding == 0 &&
                    !sawVertex && !sawFragment)
                    sawVertex = true;
                else if (segment.Stages == ResourceStages.Fragment && segment.Binding == 128 && !sawFragment)
                    sawFragment = true;
                else return false;
            }
            return true;
        }

        private static bool ReadOnlyUniformLayout(ResourceBindingSegment[] segments)
        {
            if (segments.Length is < 1 or > Constants.MaxUniformBufferBindings) return false;
            int previousEnd = 0;
            foreach (ResourceBindingSegment segment in segments)
            {
                // Write flags are checked on immutable active usages by Program.
                // Segmentation only coalesces adjacent scalar usages of one type
                // and stage mask. Recheck bounds and non-overlap here.
                if (segment.Binding < previousEnd || segment.Binding >= Constants.MaxUniformBufferBindings ||
                    segment.Count < 1 || segment.Count > Constants.MaxUniformBufferBindings - segment.Binding ||
                    segment.Type != ResourceType.UniformBuffer || segment.IsArray ||
                    ((segment.Stages & ResourceStages.Compute) != 0 &&
                     (segment.Binding != 0 || segment.Count != 1)) ||
                    !DepthSnapshotProgramPolicy.AllowsUniformStages(segment.Stages))
                    return false;
                previousEnd = segment.Binding + segment.Count;
            }
            return true;
        }

        // Rig-only: reject every Nth validated draw so the skip path can be exercised
        // without waiting for a real alias. Read once per process; launchers never set it.
        private static readonly long ForcedStopInterval =
            long.TryParse(Environment.GetEnvironmentVariable("SMASH_COUCH_TEST_FEEDBACK_SKIP_EVERY"), out long every) && every > 0 ? every : 0;
        private static long _validations;

        internal static void Validate(EncoderState state, ResourceBindingSegment[][] sets, string context)
        {
            try
            {
                if (ForcedStopInterval > 0 && Interlocked.Increment(ref _validations) % ForcedStopInterval == 0)
                    Fail(context, "result=ForcedTestStop");
                ValidateCore(state, sets, context);
            }
            catch (FeedbackGuardException exception)
            {
                // Diagnostics are best effort and must never replace the guard stop.
                NativeFeedbackDiagnostics.TryCapture(state, sets, context, exception);
                throw;
            }
        }

        internal static void StopSnapshotFailure(EncoderState state, ResourceBindingSegment[][] sets,
            string context, FailureEvidence evidence, Exception failure)
        {
            string message = $"[CouchNativeFeedback] STOP BEFORE DRAW;{context};snapshot-failed=" +
                $"{failure.GetType().Name}:{failure.Message};{evidence.Binding.Label}";
            Report(message);
            FeedbackGuardException stop = new(message, evidence);
            NativeFeedbackDiagnostics.TryCapture(state, sets, context, stop);
            throw stop;
        }

        private static void ValidateCore(EncoderState state, ResourceBindingSegment[][] sets, string context)
        {
            List<AttachmentEvidence> attachments = new(Constants.MaxColorAttachments + 2);
            for (int i = 0; i < state.RenderTargets.Length; i++)
            {
                Texture texture = state.RenderTargets[i];
                if (texture != null)
                {
                    AddAttachment(
                        texture,
                        texture.GetRenderAttachmentHandle(),
                        $"color[{i}]",
                        state.ClearLoadAction ? "Clear" : "Load",
                        "Store",
                        attachments,
                        context);
                }
            }
            if (state.DepthStencil is Texture depth)
            {
                MTLTexture texture = depth.GetHandle();
                (string load, string store) = DepthStencilPolicy(depth.MtlFormat);
                AddAttachment(depth, texture, $"depth/stencil:{depth.MtlFormat}", load, store, attachments, context);
            }

            // Revisit all active bindings on every draw, including unchanged state.
            // The prepass resource list contains only dirty categories and is NOT
            // a complete snapshot. Read current texture handles without rebuilding
            // them again: a rebuild here would invalidate just-written GPU IDs.
            for (int set = 0; set < sets.Length; set++)
            {
                foreach (ResourceBindingSegment segment in sets[set])
                {
                    if ((segment.Stages & (ResourceStages.Vertex | ResourceStages.Fragment)) == 0)
                    {
                        continue;
                    }
                    if (set != Constants.TexturesSetIndex && set != Constants.ImagesSetIndex)
                    {
                        if (set > Constants.ImagesSetIndex)
                        {
                            Fail(context,
                                $"unknown extra resource set={set};segmentBinding={segment.Binding};" +
                                $"segmentCount={segment.Count};segmentIsArray={segment.IsArray}");
                        }
                        continue;
                    }

                    if (!segment.IsArray)
                    {
                        for (int i = 0; i < segment.Count; i++)
                        {
                            int binding = checked(segment.Binding + i);
                            TextureBase storage = set == Constants.TexturesSetIndex
                                ? state.TextureRefs[binding].Storage : state.ImageRefs[binding].Storage;
                            BindingEvidence bindingEvidence = new(set, segment, binding, i, null, null);
                            CheckStorage(storage, bindingEvidence, attachments, context);
                        }
                    }
                    else if (set == Constants.TexturesSetIndex)
                    {
                        string label = SegmentLabel(set, segment);
                        TextureArray array = state.TextureArrayRefs[segment.Binding].Array;
                        if (array == null) Fail(context, $"{label};missing texture array");
                        if (segment.Type == ResourceType.BufferTexture)
                        {
                            CheckBuffers(array.GetBufferTextureRefs(), set, segment, attachments, context);
                        }
                        else
                        {
                            CheckTextures(array.GetTextureRefs(), set, segment, attachments, context);
                        }
                    }
                    else
                    {
                        string label = SegmentLabel(set, segment);
                        ImageArray array = state.ImageArrayRefs[segment.Binding].Array;
                        if (array == null) Fail(context, $"{label};missing image array");
                        if (segment.Type == ResourceType.BufferImage)
                        {
                            CheckBuffers(array.GetBufferTextureRefs(), set, segment, attachments, context);
                        }
                        else
                        {
                            CheckTextures(array.GetTextureRefs(), set, segment, attachments, context);
                        }
                    }
                }
            }
        }

        private static string SegmentLabel(int set, ResourceBindingSegment segment) =>
            $"set={set};segmentBinding={segment.Binding};segmentCount={segment.Count};" +
            $"segmentIsArray={segment.IsArray};stages={segment.Stages};kind={segment.Type}";

        private static (string Load, string Store) DepthStencilPolicy(MTLPixelFormat format) => format switch
        {
            MTLPixelFormat.Depth16Unorm or MTLPixelFormat.Depth32Float =>
                ("Depth=Load;Stencil=NotApplicable", "Depth=Store;Stencil=NotApplicable"),
            MTLPixelFormat.Stencil8 =>
                ("Depth=NotApplicable;Stencil=Load", "Depth=NotApplicable;Stencil=Store"),
            MTLPixelFormat.Depth24UnormStencil8 or MTLPixelFormat.Depth32FloatStencil8 =>
                ("Depth=Load;Stencil=Load", "Depth=Store;Stencil=Store"),
            _ => ("Unsupported", "Unsupported"),
        };

        private static void AddAttachment(
            Texture managedTexture,
            MTLTexture texture,
            string role,
            string loadPolicy,
            string storePolicy,
            List<AttachmentEvidence> attachments,
            string context)
        {
            Identity identity = ReadIdentity(texture);
            if (!identity.Known) Fail(context, $"attachment={role};identity={identity}");
            attachments.Add(new AttachmentEvidence(
                managedTexture, identity, role, loadPolicy, storePolicy, "None"));
        }

        private static void CheckTextures(
            TextureRef[] textures,
            int set,
            ResourceBindingSegment segment,
            List<AttachmentEvidence> attachments,
            string context)
        {
            string label = SegmentLabel(set, segment);
            if (textures == null) Fail(context, $"{label};wrong array texture kind");
            for (int i = 0; i < textures.Length; i++)
            {
                BindingEvidence binding = new(set, segment, segment.Binding, -1, i, textures.Length);
                CheckStorage(textures[i].Storage, binding, attachments, context);
            }
        }

        private static void CheckBuffers(
            TextureBuffer[] textures,
            int set,
            ResourceBindingSegment segment,
            List<AttachmentEvidence> attachments,
            string context)
        {
            string label = SegmentLabel(set, segment);
            if (textures == null) Fail(context, $"{label};wrong array buffer kind");
            for (int i = 0; i < textures.Length; i++)
            {
                BindingEvidence binding = new(set, segment, segment.Binding, -1, i, textures.Length);
                CheckStorage(textures[i], binding, attachments, context);
            }
        }

        private static void CheckStorage(
            TextureBase texture,
            BindingEvidence binding,
            List<AttachmentEvidence> attachments,
            string context)
        {
            // Explicitly unbound slots contain no resource to alias. A non-null
            // wrapper with an invalid/zero native handle is an unknown and fails.
            if (texture == null) return;
            Identity shader = ReadIdentity(texture.GetHandle());
            if (!shader.Known) Fail(context, $"{binding.Label};shader={shader}");
            foreach (AttachmentEvidence attachment in attachments)
            {
                Relation relation = Compare(shader, attachment.Identity);
                if (relation != Relation.Disjoint)
                {
                    FailureEvidence evidence = new(
                        relation, binding, texture, shader, attachment);
                    Fail(
                        context,
                        $"result={relation};{binding.Label};attachment={attachment.Role};" +
                        $"shader={shader};target={attachment.Identity}",
                        evidence);
                }
            }
        }

        private static void Fail(string context, string detail, FailureEvidence evidence = null)
        {
            string message = $"[CouchNativeFeedback] STOP BEFORE DRAW;{context};{detail}";
            Report(message);
            throw new FeedbackGuardException(message, evidence);
        }

        private static long _stops;
        private static long _skipped;

        // A recurring effect can be rejected every frame. Keep the first stops and
        // then one in a thousand so the log stays readable and cheap.
        private static void Report(string message)
        {
            FeedbackStats.CountStop(message);
            long stop = Interlocked.Increment(ref _stops);
            if (stop > 16 && stop % 1000 != 0) return;
            message = $"{message};stop={stop}";
            Logger.Error?.PrintMsg(LogClass.Gpu, message);
            Console.Error.WriteLine(message);
        }

        internal static void CountSkippedDraw(FeedbackGuardException failure)
        {
            FeedbackStats.CountSkip(failure.Message);
            long skipped = Interlocked.Increment(ref _skipped);
            if (skipped == 1 || skipped % 1000 == 0)
            {
                string message = $"[CouchNativeFeedback] SKIPPED DRAW;skipped={skipped}";
                Logger.Warning?.PrintMsg(LogClass.Gpu, message);
                Console.Error.WriteLine(message);
            }
        }
    }
}
