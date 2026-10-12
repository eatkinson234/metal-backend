using Avalonia.Controls;
using Avalonia.Threading;
using Ryujinx.Ava.UI.ViewModels;
using Ryujinx.Ava.UI.Windows;
using Ryujinx.Ava.Utilities.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using ObjC = Ryujinx.Common.Helper.ObjectiveC;

namespace Ryujinx.Ava.UI.Helpers
{
    /// <summary>
    /// Rig-only full-screen self-test. Inert unless SMASH_COUCH_FULLSCREEN_SELFTEST is "1" (test, then close the window
    /// normally) or "keep" (test, keep playing). Some seconds after the game is on screen it posts in-process AppKit key
    /// events (Fn+F, Control-Command-F, Escape, a plain F, and in a couch session F9/F4/F5/F1), toggles the NSWindow
    /// directly like an Accessibility client and zooms it like a title-bar double-click, waits for each transition and
    /// logs the window's full-screen bit, the view-model state, the chrome and the drawable size. It ends with one line
    /// "SMASH_FULLSCREEN_SELFTEST result=PASS|FAIL ..." on stderr and in the log. Posting events to the own process
    /// needs no Accessibility permission.
    /// </summary>
    internal sealed partial class CouchFullscreenSelfTest : IDisposable
    {
        private const string Prefix = "SMASH_FULLSCREEN_SELFTEST";
        private const int DefaultDelaySeconds = 25;
        private const int PollMs = 100;
        private const int TransitionTimeoutMs = 6000;
        // Longer than the toggle debounce, so every step starts from a settled window.
        private const int SettleMs = CouchWindowPolicy.FullscreenToggleDelayMs + 500;
        private const int KeyHoldMs = 150;

        private const ulong FlagControl = 1UL << 18;
        private const ulong FlagCommand = 1UL << 20;
        private const ulong FlagFunction = 1UL << 23;
        private const ulong StyleMaskFullScreen = 1UL << 14;

        private const ushort KeyF = 3;
        private const ushort KeyEscape = 53;
        private const ushort KeyF1 = 122;
        private const ushort KeyF4 = 118;
        private const ushort KeyF5 = 96;
        private const ushort KeyF9 = 101;

        private static int _started;

        private readonly MainWindow _window;
        private readonly bool _closeWhenDone;
        private readonly int _delaySeconds;
        private readonly List<string> _failed = [];
        private int _steps;
        private volatile bool _cancelled;

        public static CouchFullscreenSelfTest TryStart(MainWindow window)
        {
            // Default-off: no thread, no native call.
            string mode = Environment.GetEnvironmentVariable("SMASH_COUCH_FULLSCREEN_SELFTEST");

            if (mode is not ("1" or "keep") || !OperatingSystem.IsMacOS() || Interlocked.Exchange(ref _started, 1) != 0)
            {
                return null;
            }

            int delay = int.TryParse(Environment.GetEnvironmentVariable("SMASH_COUCH_FULLSCREEN_SELFTEST_DELAY_S"), out int value)
                ? Math.Clamp(value, 5, 300)
                : DefaultDelaySeconds;

            return new CouchFullscreenSelfTest(window, mode == "1", delay);
        }

        [SupportedOSPlatform("macos")]
        private CouchFullscreenSelfTest(MainWindow window, bool closeWhenDone, int delaySeconds)
        {
            _window = window;
            _closeWhenDone = closeWhenDone;
            _delaySeconds = delaySeconds;

            new Thread(Run) { IsBackground = true, Name = "Couch full-screen self-test" }.Start();
        }

        public void Dispose()
        {
            _cancelled = true;
        }

        private readonly record struct Snapshot(
            bool HasWindow, bool NsFullScreen, bool KeyWindow, bool AppActive,
            WindowState VmState, WindowState WindowState, bool IsFullScreen, bool Menu, bool TitleExtends,
            bool Running, bool Docked, bool Paused, string VSync,
            int DrawableWidth, int DrawableHeight, int ExpectedWidth, int ExpectedHeight, double ContentsScale, double BackingScale)
        {
            public override string ToString() =>
                $"ns_fullscreen={NsFullScreen} vm={VmState} window={WindowState} is_fullscreen={IsFullScreen} menu={Menu} " +
                $"title_extends={TitleExtends} running={Running} key={KeyWindow} active={AppActive} docked={Docked} paused={Paused} " +
                $"vsync={VSync} drawable={DrawableWidth}x{DrawableHeight} expected={ExpectedWidth}x{ExpectedHeight} " +
                $"contents_scale={ContentsScale} backing_scale={BackingScale}";
        }

        [SupportedOSPlatform("macos")]
        private void Run()
        {
            try
            {
                Report($"armed couch={CouchWindowPolicy.CouchMode} delay_s={_delaySeconds} close_when_done={_closeWhenDone}");

                if (!WaitUntil(GameOnScreen, TimeSpan.Zero, TimeSpan.FromMinutes(10)))
                {
                    Finish("game-not-on-screen");
                    return;
                }

                Thread.Sleep(TimeSpan.FromSeconds(_delaySeconds));

                // The loading screen (shader cache) briefly follows the first renderer switch; frames, and with them
                // drawable resizes, only flow once it has stayed away.
                if (!WaitUntil(GameOnScreen, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(10)))
                {
                    Finish("game-not-presenting");
                    return;
                }

                Snapshot start = OnUi(Read);
                Report($"start {start}");

                if (!start.KeyWindow)
                {
                    // The posted key events go to the key window; a rig launch is normally already frontmost.
                    OnUi(Activate);
                    Thread.Sleep(SettleMs);
                    Report($"activated {OnUi(Read)}");
                }

                if (!start.NsFullScreen)
                {
                    Transition("native-enter-initial", ToggleNative, true);
                }

                Transition("fn-f", () => Press(KeyF, FlagFunction), false);
                if (CouchWindowPolicy.ForEscape(WindowState.Normal, true, ConfigurationState.Instance.ShowConfirmExit, CouchWindowPolicy.CouchMode)
                    is CouchWindowPolicy.EscapeAction.None)
                {
                    Unchanged("escape-windowed", () => Press(KeyEscape, 0));
                }
                else
                {
                    Report("step=escape-windowed skipped (Escape would open the exit prompt)");
                }
                Unchanged("plain-f", () => Press(KeyF, 0));
                Transition("ctrl-cmd-f", () => Press(KeyF, FlagControl | FlagCommand), true);
                Transition("escape-fullscreen", () => Press(KeyEscape, 0), false);
                Transition("native-enter", ToggleNative, true);
                Transition("ctrl-cmd-f-after-native", () => Press(KeyF, FlagControl | FlagCommand), false);
                Transition("native-enter-2", ToggleNative, true);
                Transition("native-exit", ToggleNative, false);
                Transition("fn-f-after-native", () => Press(KeyF, FlagFunction), true);

                // A zoomed windowed game is Maximized: one press must enter full screen (not un-zoom), also after
                // AppKit restores the zoomed frame on the way out.
                Transition("ctrl-cmd-f-before-zoom", () => Press(KeyF, FlagControl | FlagCommand), false);
                Transition("native-zoom", ZoomNative, false, WindowState.Maximized);
                Transition("ctrl-cmd-f-from-zoomed", () => Press(KeyF, FlagControl | FlagCommand), true);
                Transition("ctrl-cmd-f-exit-zoomed", () => Press(KeyF, FlagControl | FlagCommand), false);
                Transition("fn-f-from-zoomed", () => Press(KeyF, FlagFunction), true);

                if (CouchWindowPolicy.CouchMode)
                {
                    Unchanged("f9-docked", () => Press(KeyF9, FlagFunction));
                    Unchanged("f4-show-ui", () => Press(KeyF4, FlagFunction));
                    Unchanged("f5-pause", () => Press(KeyF5, FlagFunction));
                    Unchanged("f1-vsync", () => Press(KeyF1, FlagFunction));
                }

                Finish(null);
            }
            catch (Exception e)
            {
                if (!_cancelled)
                {
                    Finish($"exception:{e.GetType().Name}:{e.Message}");
                }
            }
        }

        private bool GameOnScreen()
        {
            return OnUi(() => _window.ViewModel.AppHost != null && _window.ViewModel.IsGameRunning &&
                !_window.ViewModel.ShowLoadProgress && RenderView() != nint.Zero);
        }

        /// <summary>
        /// A transition step: the action must end in the expected full-screen state with every view consistent, and in
        /// <paramref name="expectVm"/> when it is given.
        /// </summary>
        [SupportedOSPlatform("macos")]
        private void Transition(string name, Action action, bool expectFullScreen, WindowState? expectVm = null)
        {
            ThrowIfCancelled();
            _steps++;

            Stopwatch watch = Stopwatch.StartNew();
            action();

            Snapshot snapshot = default;
            string problems = "timeout";

            while (watch.ElapsedMilliseconds < TransitionTimeoutMs)
            {
                Thread.Sleep(PollMs);
                snapshot = OnUi(Read);
                problems = Problems(snapshot, expectFullScreen, expectVm);

                if (problems.Length == 0)
                {
                    break;
                }
            }

            long reachedMs = watch.ElapsedMilliseconds;

            // Still consistent once the animation is surely over (AppKit reports a refused toggle only at its end).
            Thread.Sleep(SettleMs);
            snapshot = OnUi(Read);
            problems = Problems(snapshot, expectFullScreen, expectVm);

            Record(name, problems, $"expect_fullscreen={expectFullScreen} expect_vm={expectVm?.ToString() ?? "-"} reached_ms={reachedMs} {snapshot}");
        }

        /// <summary>A negative step: after the key, the window, the chrome and the emulation state must not change.</summary>
        [SupportedOSPlatform("macos")]
        private void Unchanged(string name, Action action)
        {
            ThrowIfCancelled();
            _steps++;

            Snapshot before = OnUi(Read);
            action();
            Thread.Sleep(SettleMs);
            Snapshot after = OnUi(Read);

            List<string> problems = [];
            string state = Problems(after, before.NsFullScreen);

            if (state.Length != 0)
            {
                problems.Add(state);
            }

            if (after.Docked != before.Docked)
            {
                problems.Add("docked-changed");
            }

            if (after.Paused != before.Paused)
            {
                problems.Add("pause-changed");
            }

            if (after.VSync != before.VSync)
            {
                problems.Add("vsync-changed");
            }

            Record(name, string.Join(',', problems), $"expect_unchanged_fullscreen={before.NsFullScreen} {after}");
        }

        private static string Problems(Snapshot s, bool expectFullScreen, WindowState? expectVm = null)
        {
            List<string> problems = [];

            if (!s.HasWindow)
            {
                problems.Add("no-nswindow");
            }

            if (s.NsFullScreen != expectFullScreen)
            {
                problems.Add("nswindow");
            }

            if ((s.VmState is WindowState.FullScreen) != expectFullScreen)
            {
                problems.Add("vm-state");
            }

            if (s.WindowState != s.VmState)
            {
                problems.Add("window-vs-vm");
            }

            if (expectVm is { } vmState && s.VmState != vmState)
            {
                problems.Add($"vm-not-{vmState}");
            }

            if (s.IsFullScreen != expectFullScreen)
            {
                problems.Add("is-fullscreen");
            }

            if (s.Menu != CouchWindowPolicy.MenuAndStatusBarVisible(!expectFullScreen, CouchWindowPolicy.CouchMode))
            {
                problems.Add("menu");
            }

            if (s.TitleExtends != (expectFullScreen || !ConfigurationState.Instance.ShowTitleBar))
            {
                problems.Add("title-bar");
            }

            if (!s.Running)
            {
                problems.Add("not-running");
            }

            if (Math.Abs(s.DrawableWidth - s.ExpectedWidth) > 2 || Math.Abs(s.DrawableHeight - s.ExpectedHeight) > 2)
            {
                problems.Add("drawable-size");
            }

            if (Math.Abs(s.ContentsScale - s.BackingScale) > 0.001)
            {
                problems.Add("contents-scale");
            }

            return string.Join(',', problems);
        }

        private void Record(string name, string problems, string details)
        {
            bool ok = problems.Length == 0;

            if (!ok)
            {
                _failed.Add(name);
            }

            Report($"step={name} ok={ok} problems={(ok ? "-" : problems)} {details}");
        }

        private void Finish(string error)
        {
            bool pass = error == null && _failed.Count == 0;

            Report($"result={(pass ? "PASS" : "FAIL")} steps={_steps} failed={(_failed.Count == 0 ? "-" : string.Join(',', _failed))}" +
                (error == null ? string.Empty : $" error={error}"));

            if (_closeWhenDone && !_cancelled)
            {
                try
                {
                    // The normal close path (stop, save caches, exit), as DiagnosticPreloadClose uses it.
                    Dispatcher.UIThread.Post(() =>
                    {
                        _window.ViewModel.IsClosing = true;
                        _window.Close();
                    });
                }
                catch (Exception e)
                {
                    Report($"close-request-failed error={e.GetType().Name}");
                }
            }
        }

        private static void Report(string message)
        {
            Logger.Notice.Print(LogClass.Application, $"{Prefix} {message}");

            try
            {
                Console.Error.WriteLine($"{Prefix} {message}");
            }
            catch
            {
                // stderr closed: the log line above is enough.
            }
        }

        private void ThrowIfCancelled()
        {
            if (_cancelled)
            {
                throw new OperationCanceledException();
            }
        }

        /// <summary>Waits until the condition has held for <paramref name="stableFor"/> (sampled every 500 ms).</summary>
        private bool WaitUntil(Func<bool> condition, TimeSpan stableFor, TimeSpan timeout)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Stopwatch held = null;

            while (watch.Elapsed < timeout)
            {
                ThrowIfCancelled();

                if (!condition())
                {
                    held = null;
                }
                else if ((held ??= Stopwatch.StartNew()).Elapsed >= stableFor)
                {
                    return true;
                }

                Thread.Sleep(500);
            }

            return false;
        }

        private static T OnUi<T>(Func<T> read) => Dispatcher.UIThread.Invoke(read);

        private static void OnUi(Action action) => Dispatcher.UIThread.Invoke(action);

        // UI thread only.
        private nint RenderView() => _window.ViewModel.AppHost?.RendererHost?.EmbeddedWindow?.MacOSRenderView ?? nint.Zero;

        // UI thread only.
        [SupportedOSPlatform("macos")]
        private nint NsWindow()
        {
            nint view = RenderView();

            return view == nint.Zero ? nint.Zero : Native.Send(view, Native.Sel("window"));
        }

        // UI thread only.
        [SupportedOSPlatform("macos")]
        private Snapshot Read()
        {
            MainWindowViewModel vm = _window.ViewModel;
            nint window = NsWindow();
            nint view = RenderView();
            nint app = Native.Send(Native.Class("NSApplication"), Native.Sel("sharedApplication"));

            bool nsFullScreen = false, key = false;
            double backing = 0, contentsScale = 0;
            int drawableWidth = 0, drawableHeight = 0, expectedWidth = 0, expectedHeight = 0;

            if (window != nint.Zero)
            {
                nsFullScreen = ((ulong)Native.Send(window, Native.Sel("styleMask")) & StyleMaskFullScreen) != 0;
                key = Native.SendByte(window, Native.Sel("isKeyWindow")) != 0;
                backing = Native.SendDouble(window, Native.Sel("backingScaleFactor"));

                nint layer = Native.Send(view, Native.Sel("layer"));
                Native.CGRect bounds = Native.SendRect(view, Native.Sel("bounds"));
                expectedWidth = (int)(bounds.Width * backing);
                expectedHeight = (int)(bounds.Height * backing);

                if (layer != nint.Zero)
                {
                    Native.CGSize drawable = Native.SendSize(layer, Native.Sel("drawableSize"));
                    drawableWidth = (int)drawable.Width;
                    drawableHeight = (int)drawable.Height;
                    contentsScale = Native.SendDouble(layer, Native.Sel("contentsScale"));
                }
            }

            AppHost host = vm.AppHost;

            return new Snapshot(
                window != nint.Zero, nsFullScreen, key, Native.SendByte(app, Native.Sel("isActive")) != 0,
                vm.WindowState, _window.WindowState, vm.IsFullScreen, vm.ShowMenuAndStatusBar, _window.TitleBar.ExtendsContentIntoTitleBar,
                vm.IsGameRunning && host != null, ConfigurationState.Instance.System.EnableDockedMode.Value,
                host?.Device?.System?.IsPaused ?? false, host?.Device?.VSyncMode.ToString() ?? "none",
                drawableWidth, drawableHeight, expectedWidth, expectedHeight, contentsScale, backing);
        }

        // UI thread only.
        [SupportedOSPlatform("macos")]
        private void Activate()
        {
            nint app = Native.Send(Native.Class("NSApplication"), Native.Sel("sharedApplication"));
            Native.SendVoid(app, Native.Sel("activateIgnoringOtherApps:"), 1);

            nint window = NsWindow();

            if (window != nint.Zero)
            {
                Native.SendVoid(window, Native.Sel("makeKeyAndOrderFront:"), nint.Zero);
            }
        }

        /// <summary>What an Accessibility client setting AXFullScreen ends up doing: -[NSWindow toggleFullScreen:].</summary>
        [SupportedOSPlatform("macos")]
        private void ToggleNative()
        {
            OnUi(() =>
            {
                nint window = NsWindow();

                if (window != nint.Zero)
                {
                    Native.SendVoid(window, Native.Sel("toggleFullScreen:"), nint.Zero);
                }
            });
        }

        /// <summary>
        /// What a title-bar double-click or Option-click on the green button does: -[NSWindow zoom:]. It toggles, so a
        /// window that is already zoomed is left as it is.
        /// </summary>
        [SupportedOSPlatform("macos")]
        private void ZoomNative()
        {
            OnUi(() =>
            {
                nint window = NsWindow();

                if (window != nint.Zero && Native.SendByte(window, Native.Sel("isZoomed")) == 0)
                {
                    Native.SendVoid(window, Native.Sel("zoom:"), nint.Zero);
                }
            });
        }

        /// <summary>Key down, a short hold (the hotkeys are polled each frame), key up, through the app's event queue.</summary>
        [SupportedOSPlatform("macos")]
        private static void Press(ushort keyCode, ulong flags)
        {
            OnUi(() => PostKey(keyCode, flags, true));
            Thread.Sleep(KeyHoldMs);
            OnUi(() => PostKey(keyCode, flags, false));
        }

        // UI thread only.
        [SupportedOSPlatform("macos")]
        private static void PostKey(ushort keyCode, ulong flags, bool down)
        {
            nint cgEvent = Native.CGEventCreateKeyboardEvent(nint.Zero, keyCode, (byte)(down ? 1 : 0));

            if (cgEvent == nint.Zero)
            {
                throw new InvalidOperationException("CGEventCreateKeyboardEvent failed");
            }

            try
            {
                Native.CGEventSetFlags(cgEvent, flags);

                // The NSEvent keeps its own reference to the CGEvent.
                nint nsEvent = Native.Send(Native.Class("NSEvent"), Native.Sel("eventWithCGEvent:"), cgEvent);

                if (nsEvent == nint.Zero)
                {
                    throw new InvalidOperationException("eventWithCGEvent: failed");
                }

                nint app = Native.Send(Native.Class("NSApplication"), Native.Sel("sharedApplication"));
                Native.SendVoid(app, Native.Sel("postEvent:atStart:"), nsEvent, 0);
            }
            finally
            {
                Native.CFRelease(cgEvent);
            }
        }

        [SupportedOSPlatform("macos")]
        private static partial class Native
        {
            private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
            private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

            [StructLayout(LayoutKind.Sequential)]
            public readonly struct CGSize
            {
                public readonly double Width;
                public readonly double Height;
            }

            [StructLayout(LayoutKind.Sequential)]
            public readonly struct CGRect
            {
                public readonly double X;
                public readonly double Y;
                public readonly double Width;
                public readonly double Height;
            }

            public static nint Class(string name) => new ObjC.Object(name).ObjPtr;

            public static nint Sel(string name) => sel_registerName(name);

            [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
            private static partial nint sel_registerName(string name);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial nint Send(nint receiver, nint selector);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial nint Send(nint receiver, nint selector, nint argument);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial byte SendByte(nint receiver, nint selector);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial double SendDouble(nint receiver, nint selector);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial CGSize SendSize(nint receiver, nint selector);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial CGRect SendRect(nint receiver, nint selector);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial void SendVoid(nint receiver, nint selector, nint argument);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial void SendVoid(nint receiver, nint selector, byte argument);

            [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
            public static partial void SendVoid(nint receiver, nint selector, nint argument, byte flag);

            [LibraryImport(CoreGraphics)]
            public static partial nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, byte keyDown);

            [LibraryImport(CoreGraphics)]
            public static partial void CGEventSetFlags(nint cgEvent, ulong flags);

            [LibraryImport(CoreFoundation)]
            public static partial void CFRelease(nint cf);
        }
    }
}
