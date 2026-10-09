using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // Auto<DepthFeedbackSnapshot> holds the native objects through the command
    // buffer fence. A sampled source can have three different native objects:
    // its swizzled sampling view, its identity copy view, and the parent root.
    // Keep every distinct handle alive until the copy/draw command buffer fence.
    [SupportedOSPlatform("macos")]
    internal sealed class DepthFeedbackSnapshot : IDisposable
    {
        // Isolated fixture seam. Unset for ordinary renderer-created programs;
        // lets the fixture fail after command-buffer ownership transfer and
        // verify that the source/root/view survive until fence consumption.
        internal static Action<object> BeforeCopyTestHook;
        internal static Action<object> AfterCopyTestHook;

        private Texture _snapshot;
        private readonly List<MTLTexture> _retainedSourceHandles = new(3);
        private int _disposed;

        internal Texture Texture => _snapshot;

        internal DepthFeedbackSnapshot(Texture snapshot, MTLTexture borrowedSourceSampling,
            MTLTexture borrowedSourceIdentity, IntPtr borrowedRoot)
        {
            if (borrowedSourceSampling.NativePtr == IntPtr.Zero ||
                borrowedSourceIdentity.NativePtr == IntPtr.Zero || borrowedRoot == IntPtr.Zero)
                throw new ArgumentException("Depth snapshot source handles must be valid.", nameof(borrowedSourceSampling));

            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            try
            {
                RetainUnique(borrowedSourceSampling);
                RetainUnique(borrowedSourceIdentity);
                RetainUnique(new MTLTexture(borrowedRoot));
            }
            catch
            {
                try { ReleaseSourceHandles(); }
                catch { }
                _snapshot = null;
                throw;
            }
        }

        private void RetainUnique(MTLTexture handle)
        {
            if (handle.NativePtr == IntPtr.Zero)
                throw new ArgumentException("Depth snapshot source handle is invalid.", nameof(handle));

            foreach (MTLTexture retained in _retainedSourceHandles)
            {
                if (retained.NativePtr == handle.NativePtr)
                    return;
            }

            ObjectiveC.IntPtr_objc_msgSend(handle.NativePtr, "retain");
            _retainedSourceHandles.Add(handle);
        }

        private Exception ReleaseSourceHandles()
        {
            Exception failure = null;
            for (int index = _retainedSourceHandles.Count - 1; index >= 0; index--)
            {
                MTLTexture handle = _retainedSourceHandles[index];
                _retainedSourceHandles.RemoveAt(index);
                try
                {
                    handle.Dispose();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
            return failure;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Exception failure = null;
            try
            {
                _snapshot?.Release();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                _snapshot = null;
                Exception sourceFailure = ReleaseSourceHandles();
                failure ??= sourceFailure;
            }
            if (failure != null) throw failure;
        }
    }
}
