using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    // The refresh timing of the screen showing the game, for refresh-anchored presents. AppKit is read
    // on the main thread only: the UI host calls SampleOnMainThread a few times a second with the NSView
    // that hosts the CAMetalLayer; the render thread reads the newest immutable sample. NSScreen
    // minimum/maximumRefreshInterval and displayUpdateGranularity are macOS 12+ (checked per call).
    [SupportedOSPlatform("macos")]
    public static partial class DisplayRefreshSampler
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        internal sealed record Sample(long SampledTicks, uint ScreenNumber, double MinimumInterval,
            double MaximumInterval, double Granularity, bool FullScreen);

        private static Sample _latest;
        private static nint _screenNumberKey;

        internal static Sample Latest => Volatile.Read(ref _latest);

        [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint sel_registerName(string name);

        [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint objc_getClass(string name);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nint SendPointer(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nint SendPointer(nint receiver, nint selector, nint argument);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint SendString(nint receiver, nint selector, string argument);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial double SendDouble(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial uint SendUInt(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial nuint SendNUInt(nint receiver, nint selector);

        [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
        private static partial byte SendRespondsTo(nint receiver, nint selector, nint query);

        private static readonly nint _window = sel_registerName("window");
        private static readonly nint _screen = sel_registerName("screen");
        private static readonly nint _styleMask = sel_registerName("styleMask");
        private static readonly nint _respondsToSelector = sel_registerName("respondsToSelector:");
        private static readonly nint _minimumRefreshInterval = sel_registerName("minimumRefreshInterval");
        private static readonly nint _maximumRefreshInterval = sel_registerName("maximumRefreshInterval");
        private static readonly nint _displayUpdateGranularity = sel_registerName("displayUpdateGranularity");
        private static readonly nint _deviceDescription = sel_registerName("deviceDescription");
        private static readonly nint _objectForKey = sel_registerName("objectForKey:");
        private static readonly nint _unsignedIntValue = sel_registerName("unsignedIntValue");

        private const nuint FullScreenStyle = 1 << 14; // NSWindowStyleMaskFullScreen

        // Main thread only. A missing window or screen, an old macOS or an ARM64 mismatch publishes an
        // unusable sample, which keeps the CPU present schedule.
        public static void SampleOnMainThread(nint nsView)
        {
            Sample sample = new(Stopwatch.GetTimestamp(), 0, 0, 0, 0, false);
            try
            {
                nint window = nsView != 0 && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? SendPointer(nsView, _window) : 0;
                nint screen = window != 0 ? SendPointer(window, _screen) : 0;
                if (screen != 0 && SendRespondsTo(screen, _respondsToSelector, _minimumRefreshInterval) != 0 &&
                    SendRespondsTo(screen, _respondsToSelector, _maximumRefreshInterval) != 0)
                {
                    double granularity = SendRespondsTo(screen, _respondsToSelector, _displayUpdateGranularity) != 0
                        ? SendDouble(screen, _displayUpdateGranularity) : 0;
                    sample = sample with
                    {
                        ScreenNumber = ScreenNumber(screen),
                        MinimumInterval = SendDouble(screen, _minimumRefreshInterval),
                        MaximumInterval = SendDouble(screen, _maximumRefreshInterval),
                        Granularity = granularity,
                        FullScreen = (SendNUInt(window, _styleMask) & FullScreenStyle) != 0,
                    };
                }
            }
            catch (Exception)
            {
                // Keep the unusable sample.
            }
            Volatile.Write(ref _latest, sample);
        }

        private static uint ScreenNumber(nint screen)
        {
            if (_screenNumberKey == 0)
            {
                // Owned (+1) and kept for the process lifetime.
                nint allocated = SendPointer(objc_getClass("NSString"), sel_registerName("alloc"));
                _screenNumberKey = SendString(allocated, sel_registerName("initWithUTF8String:"), "NSScreenNumber");
            }
            nint description = SendPointer(screen, _deviceDescription);
            nint number = description != 0 && _screenNumberKey != 0 ? SendPointer(description, _objectForKey, _screenNumberKey) : 0;
            return number != 0 ? SendUInt(number, _unsignedIntValue) : 0;
        }
    }
}
