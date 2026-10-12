using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // A guest draw that samples a texture which is also bound as one of its
    // render targets is a feedback loop. Guest GPUs define the sampled result
    // only for data written before the draw (texture-barrier rule: a draw does
    // not reliably observe its own writes, except a fragment re-reading the old
    // value of the texel it is about to write). Ending the Metal pass stores
    // every earlier write; a copy of the sampled subresource taken then holds
    // exactly the data every defined guest result may depend on. Binding the
    // copy instead of the live attachment therefore preserves the guest result
    // and removes the Metal attachment/sample hazard the guard rejects.
    //
    // Scope: non-array sampled textures (set 2) whose view is a single-sample
    // 2D view of one slice. Images (possibly written), texture arrays, buffer
    // textures and unknown identities are left to RenderFeedbackGuard.
    [SupportedOSPlatform("macos")]
    sealed class FeedbackCopyTexture : TextureBase
    {
        internal FeedbackCopyTexture(MTLDevice device, MetalRenderer renderer, Pipeline pipeline,
            TextureCreateInfo info, MTLTexture view, MTLPixelFormat format) : base(device, renderer, pipeline, info)
        {
            MtlTexture = view;
            MtlFormat = format;
        }
    }

    [SupportedOSPlatform("macos")]
    sealed class FeedbackCopyPool : IDisposable
    {
        internal readonly record struct Key(MTLPixelFormat Format, ulong Width, ulong Height, ulong Levels);

        private const int MaxFreePerKey = 4;
        private const MTLStorageMode PrivateStorage = (MTLStorageMode)2;

        private readonly MTLDevice _device;
        private readonly Dictionary<Key, Stack<MTLTexture>> _free = new();
        private readonly object _lock = new();
        private bool _disposed;

        internal FeedbackCopyPool(MTLDevice device)
        {
            _device = device;
        }

        internal MTLTexture Rent(Key key)
        {
            lock (_lock)
            {
                if (_free.TryGetValue(key, out Stack<MTLTexture> stack) && stack.Count > 0)
                {
                    return stack.Pop();
                }
            }

            MTLTextureDescriptor descriptor = new()
            {
                PixelFormat = key.Format,
                TextureType = MTLTextureType.Type2D,
                Width = key.Width,
                Height = key.Height,
                MipmapLevelCount = key.Levels,
                Usage = MTLTextureUsage.Unknown,
                StorageMode = PrivateStorage,
            };
            MTLTexture texture = _device.NewTexture(descriptor);
            descriptor.Dispose();
            if (texture.NativePtr == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Feedback copy allocation failed for {key}.");
            }
            return texture;
        }

        // Called once the command buffers that used the texture have completed.
        internal void Return(Key key, MTLTexture texture)
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    if (!_free.TryGetValue(key, out Stack<MTLTexture> stack))
                    {
                        _free[key] = stack = new Stack<MTLTexture>();
                    }
                    if (stack.Count < MaxFreePerKey)
                    {
                        stack.Push(texture);
                        return;
                    }
                }
            }
            texture.Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                foreach (Stack<MTLTexture> stack in _free.Values)
                {
                    while (stack.Count > 0)
                    {
                        stack.Pop().Dispose();
                    }
                }
                _free.Clear();
            }
        }
    }

    // Owned through Auto<> so the copies outlive every command buffer that
    // encoded them; disposal returns the copy roots to the pool.
    [SupportedOSPlatform("macos")]
    sealed class FeedbackCopyLease : IDisposable
    {
        private readonly FeedbackCopyPool _pool;
        private readonly List<(FeedbackCopyPool.Key Key, MTLTexture Texture)> _roots = new(2);
        private readonly List<FeedbackCopyTexture> _views = new(2);
        private int _disposed;

        internal FeedbackCopyLease(FeedbackCopyPool pool)
        {
            _pool = pool;
        }

        internal void AddRoot(FeedbackCopyPool.Key key, MTLTexture texture)
        {
            _roots.Add((key, texture));
            Bytes += texture.AllocatedSize;
        }

        // Device memory held by this lease's copies until its command buffers complete.
        internal ulong Bytes { get; private set; }

        internal void AddView(FeedbackCopyTexture view) => _views.Add(view);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (FeedbackCopyTexture view in _views)
            {
                view.Release();
            }
            _views.Clear();
            foreach ((FeedbackCopyPool.Key key, MTLTexture texture) in _roots)
            {
                _pool.Return(key, texture);
            }
            _roots.Clear();
        }
    }

    // Distinct-signature accounting for feedback copies, guard stops and skipped
    // draws. Each new signature is logged once when first seen; every count is
    // logged in a final summary, so a new class is never hidden behind a global
    // throttle.
    [SupportedOSPlatform("macos")]
    static class FeedbackStats
    {
        private static readonly Dictionary<string, long> _counts = new();
        private static readonly object _lock = new();
        private static long _copies;
        private static long _skipped;

        internal static long Copies => Interlocked.Read(ref _copies);
        internal static long Skipped => Interlocked.Read(ref _skipped);

        private static readonly Regex _volatileFields = new(
            @"(draw|barrier|stop)=[^;]*;?|(View|Root|Buffer|GpuId) = \d+, ",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string Normalize(string message) => _volatileFields.Replace(message, string.Empty);

        internal static void CountCopy(string signature)
        {
            Interlocked.Increment(ref _copies);
            Count("copy;" + signature);
        }

        internal static void CountStop(string message) => Count("stop;" + Normalize(message));

        internal static void CountSkip(string message)
        {
            Interlocked.Increment(ref _skipped);
            Count("skip;" + Normalize(message));
        }

        private static void Count(string signature)
        {
            long count;
            lock (_lock)
            {
                _counts.TryGetValue(signature, out count);
                _counts[signature] = ++count;
            }
            Interlocked.Increment(ref _events);
            if (count == 1)
            {
                string message = $"[CouchNativeFeedback] NEW CLASS;{signature}";
                Logger.Warning?.PrintMsg(LogClass.Gpu, message);
                Console.Error.WriteLine(message);
            }
            else if (!signature.StartsWith("copy;", StringComparison.Ordinal) && (count & (count - 1)) == 0)
            {
                // Rejections and failures stay visible as they repeat.
                string message = $"[CouchNativeFeedback] REPEAT;count={count};{signature}";
                Logger.Warning?.PrintMsg(LogClass.Gpu, message);
                Console.Error.WriteLine(message);
            }
        }

        private static long _events;
        private static long _loggedEvents;

        // Periodic summary so counts survive a killed session; quiet when unchanged.
        internal static void LogSummaryIfChanged(string reason)
        {
            long events = Interlocked.Read(ref _events);
            if (Interlocked.Exchange(ref _loggedEvents, events) != events)
            {
                LogSummary(reason);
            }
        }

        internal static void LogSummary(string reason)
        {
            List<KeyValuePair<string, long>> entries;
            lock (_lock)
            {
                entries = new List<KeyValuePair<string, long>>(_counts);
            }
            string header = $"[CouchNativeFeedback] SUMMARY;reason={reason};copies={Copies};skipped={Skipped};classes={entries.Count}";
            Logger.Info?.PrintMsg(LogClass.Gpu, header);
            Console.Error.WriteLine(header);
            foreach (KeyValuePair<string, long> entry in entries)
            {
                string line = $"[CouchNativeFeedback] CLASS;count={entry.Value};{entry.Key}";
                Logger.Info?.PrintMsg(LogClass.Gpu, line);
                Console.Error.WriteLine(line);
            }
        }
    }
}
