// r16-ui full-screen and couch-safety rules: CouchWindowPolicy (linked from src), the Metal DrawableSizeRequest
// (linked from src) and the Avalonia 11.0.13 binding behaviour the two-way WindowState sync depends on.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;
using Ryujinx.Ava.Common;
using Ryujinx.Ava.UI.Helpers;
using Ryujinx.Graphics.Metal;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using Chrome = Ryujinx.Ava.UI.Helpers.CouchWindowPolicy.Chrome;
using Escape = Ryujinx.Ava.UI.Helpers.CouchWindowPolicy.EscapeAction;

static class CouchWindowTests
{
    static int _checks;
    static int _failures;

    static void Check(bool ok, string label)
    {
        _checks++;
        if (!ok) _failures++;
        Console.WriteLine((ok ? "OK   " : "FAIL ") + label);
    }

    static readonly WindowState[] States = [WindowState.Normal, WindowState.Minimized, WindowState.Maximized, WindowState.FullScreen];

    // Stand-ins for MainWindowViewModel (WindowState, IsFullScreen, chrome) and MainWindow (WindowStateProperty).
    sealed class Vm : INotifyPropertyChanged
    {
        WindowState _state;
        public WindowState WindowState { get => _state; set { if (_state == value) return; _state = value; Raise(nameof(WindowState)); } }
        public bool IsFullScreen, TitleExtends, Menu = true, GameRunning = true, Couch;
        public int ChromeApplications;
        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // Same steps as MainWindowViewModel.ApplyWindowStateChrome.
        public void ApplyWindowStateChrome(WindowState state)
        {
            if (CouchWindowPolicy.ChromeFor(state, IsFullScreen, GameRunning, true, Couch) is not { } c) return;
            ChromeApplications++;
            IsFullScreen = c.IsFullScreen;
            TitleExtends = c.ExtendsContentIntoTitleBar;
            if (c.ShowMenuAndStatusBar is bool m) Menu = CouchWindowPolicy.MenuAndStatusBarVisible(m, Couch);
        }

        // Same steps as MainWindowViewModel.ToggleFullscreen, without the debounce.
        public void ToggleFullscreen() => WindowState = CouchWindowPolicy.NextFullscreenState(WindowState);
    }

    sealed class FakeWindow : AvaloniaObject
    {
        public static readonly StyledProperty<WindowState> WindowStateProperty = Window.WindowStateProperty.AddOwner<FakeWindow>();
        public WindowState WindowState { get => GetValue(WindowStateProperty); set => SetValue(WindowStateProperty, value); }
    }

    static BindingBase CompiledWindowStateBinding(Vm vm, BindingMode mode)
    {
        // What the XAML compiler emits for WindowState="{Binding WindowState, Mode=...}" with compiled bindings.
        CompiledBindingPath path = new CompiledBindingPathBuilder()
            .Property(new ClrPropertyInfo(nameof(Vm.WindowState), o => ((Vm)o).WindowState, (o, v) => ((Vm)o).WindowState = (WindowState)v, typeof(WindowState)),
                PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
            .Build();

        return new CompiledBindingExtension(path) { Mode = mode, Source = vm };
    }

    // MainWindow: the binding from MainWindow.axaml plus the observer from the MainWindow constructor.
    static (Vm Vm, FakeWindow Window) Wire(BindingMode mode, bool couch, bool compiled = true)
    {
        Vm vm = new() { Couch = couch, Menu = CouchWindowPolicy.MenuAndStatusBarVisible(true, couch) };
        FakeWindow window = new();
        window.Bind(FakeWindow.WindowStateProperty, compiled ? CompiledWindowStateBinding(vm, mode) : new Binding(nameof(Vm.WindowState)) { Source = vm, Mode = mode });
        window.GetPropertyChangedObservable(FakeWindow.WindowStateProperty)
            .Subscribe(new Observer(change => vm.ApplyWindowStateChrome((WindowState)change.NewValue!)));
        return (vm, window);
    }

    sealed class Observer(Action<AvaloniaPropertyChangedEventArgs> next) : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs value) => next(value);
        public void OnError(Exception error) => throw error;
        public void OnCompleted() { }
    }

    static bool Consistent(Vm vm, FakeWindow window, bool fullScreen) =>
        window.WindowState is WindowState.FullScreen == fullScreen &&
        vm.WindowState == window.WindowState &&
        vm.IsFullScreen == fullScreen &&
        vm.TitleExtends == fullScreen &&
        vm.Menu == CouchWindowPolicy.MenuAndStatusBarVisible(!fullScreen, vm.Couch);

    static void AvaloniaBinding()
    {
        Check(Window.WindowStateProperty.GetMetadata(typeof(Window)).DefaultBindingMode == BindingMode.OneWay,
            "avalonia: Window.WindowState binds one-way by default (why the old binding went stale)");

        foreach (bool compiled in new[] { true, false })
        {
            string kind = compiled ? "compiled" : "reflection";

            // The old XAML: a native change (Window.HandleWindowStateChanged sets the property) never reaches the VM.
            (Vm oneWayVm, FakeWindow oneWay) = Wire(BindingMode.OneWay, couch: true, compiled);
            oneWay.WindowState = WindowState.FullScreen;
            Check(oneWayVm.WindowState == WindowState.Normal, $"{kind} one-way: native enter leaves the VM stale (the reported desync)");
            oneWayVm.ToggleFullscreen();
            Check(oneWay.WindowState == WindowState.FullScreen && oneWayVm.WindowState == WindowState.FullScreen,
                $"{kind} one-way: first Ctrl+Cmd+F after a native enter is a visible no-op");

            (Vm vm, FakeWindow window) = Wire(BindingMode.TwoWay, couch: true, compiled);
            Check(Consistent(vm, window, false), $"{kind} two-way: starts windowed and consistent");

            window.WindowState = WindowState.FullScreen; // green button / Mission Control / AXFullScreen / Fn+F via AppKit
            Check(vm.WindowState == WindowState.FullScreen && Consistent(vm, window, true), $"{kind} two-way: native enter reaches the VM and the chrome");

            vm.ToggleFullscreen(); // Ctrl+Cmd+F, F11, Option-Return, Escape
            Check(window.WindowState == WindowState.Normal && Consistent(vm, window, false), $"{kind} two-way: first toggle after a native enter leaves full screen");

            window.SetCurrentValue(FakeWindow.WindowStateProperty, WindowState.FullScreen);
            Check(Consistent(vm, window, true), $"{kind} two-way: SetCurrentValue from the platform also reaches the VM");

            window.WindowState = WindowState.Normal; // native exit
            Check(Consistent(vm, window, false), $"{kind} two-way: native exit reaches the VM and the chrome");

            vm.ToggleFullscreen();
            Check(window.WindowState == WindowState.FullScreen && Consistent(vm, window, true), $"{kind} two-way: binding still alive after local sets (VM -> window)");

            // A refused toggle: AppKit ignores the exit during its animation and reports full screen again at the end.
            vm.ToggleFullscreen();
            window.WindowState = WindowState.FullScreen;
            Check(vm.WindowState == WindowState.FullScreen && Consistent(vm, window, true), $"{kind} two-way: a refused toggle converges on the real state");

            int before = vm.ChromeApplications;
            window.WindowState = WindowState.FullScreen;
            vm.WindowState = WindowState.FullScreen;
            Check(vm.ChromeApplications == before, $"{kind} two-way: equal values cause no chrome re-application (no feedback loop)");

            window.WindowState = WindowState.Minimized;
            Check(vm.IsFullScreen && vm.TitleExtends && vm.WindowState == WindowState.Minimized, $"{kind} two-way: minimizing keeps the chrome");

            // A zoomed windowed game (Magnet, title-bar double-click, Option-green button): Avalonia reports Maximized
            // whenever -[NSWindow isZoomed], and the first shortcut must enter full screen, not un-zoom.
            (Vm zoomVm, FakeWindow zoomed) = Wire(BindingMode.TwoWay, couch: true, compiled);
            zoomed.WindowState = WindowState.Maximized; // native zoom
            Check(zoomVm.WindowState == WindowState.Maximized && Consistent(zoomVm, zoomed, false), $"{kind} two-way: native zoom reaches the VM");
            zoomVm.ToggleFullscreen();
            Check(zoomed.WindowState == WindowState.FullScreen && Consistent(zoomVm, zoomed, true), $"{kind} two-way: first toggle from a zoomed window enters full screen");
            zoomVm.ToggleFullscreen();
            Check(zoomed.WindowState == WindowState.Normal && Consistent(zoomVm, zoomed, false), $"{kind} two-way: toggle leaves full screen");
            zoomed.WindowState = WindowState.Maximized; // windowDidExitFullScreen: AppKit restored the zoomed frame
            Check(zoomVm.WindowState == WindowState.Maximized && Consistent(zoomVm, zoomed, false), $"{kind} two-way: the restored zoomed frame reaches the VM");
            zoomVm.ToggleFullscreen();
            Check(zoomed.WindowState == WindowState.FullScreen && Consistent(zoomVm, zoomed, true), $"{kind} two-way: every cycle re-enters full screen with one press");

            // --fullscreen (SwitchToGameControl / ShowLoading) when the window starts zoomed (window_maximized, or a
            // frame clamped to a small display).
            (Vm startVm, FakeWindow start) = Wire(BindingMode.TwoWay, couch: true, compiled);
            startVm.WindowState = WindowState.Maximized;
            if (startVm.WindowState is not WindowState.FullScreen)
            {
                startVm.ToggleFullscreen();
            }
            Check(start.WindowState == WindowState.FullScreen && Consistent(startVm, start, true), $"{kind} two-way: --fullscreen from a zoomed start opens full screen");
        }

        (Vm desk, FakeWindow deskWindow) = Wire(BindingMode.TwoWay, couch: false);
        deskWindow.WindowState = WindowState.FullScreen;
        Check(!desk.Menu && Consistent(desk, deskWindow, true), "outside couch: full screen hides the menu");
        deskWindow.WindowState = WindowState.Normal;
        Check(desk.Menu && Consistent(desk, deskWindow, false), "outside couch: leaving full screen shows the menu again (upstream behaviour)");
        deskWindow.WindowState = WindowState.Maximized;
        Check(desk.ChromeApplications == 2, "outside couch: zooming a windowed game does not touch the chrome");
    }

    static void ChromeTable()
    {
        foreach (WindowState state in States)
        foreach (bool was in new[] { false, true })
        foreach (bool running in new[] { false, true })
        foreach (bool titleBar in new[] { false, true })
        foreach (bool couch in new[] { false, true })
        {
            Chrome? c = CouchWindowPolicy.ChromeFor(state, was, running, titleBar, couch);
            bool fs = state is WindowState.FullScreen;
            string label = $"chrome {state} was={was} running={running} titleBar={titleBar} couch={couch}";

            if (state is WindowState.Minimized || fs == was)
            {
                Check(c is null, label + " -> unchanged");
                continue;
            }

            bool? menu = running ? !fs && !couch : null;
            Check(c is { } v && v.IsFullScreen == fs && v.ExtendsContentIntoTitleBar == (fs || !titleBar) && v.ShowMenuAndStatusBar == menu,
                label + $" -> fs={fs} extends={fs || !titleBar} menu={menu?.ToString() ?? "keep"}");
        }

        Check(!CouchWindowPolicy.MenuAndStatusBarVisible(true, true) && !CouchWindowPolicy.MenuAndStatusBarVisible(false, true),
            "couch: the menu and status bar never show");
        Check(CouchWindowPolicy.MenuAndStatusBarVisible(true, false) && !CouchWindowPolicy.MenuAndStatusBarVisible(false, false),
            "outside couch: the menu follows the request");
    }

    static void Toggle()
    {
        Check(CouchWindowPolicy.NextFullscreenState(WindowState.Normal) == WindowState.FullScreen, "toggle: Normal -> FullScreen");
        Check(CouchWindowPolicy.NextFullscreenState(WindowState.FullScreen) == WindowState.Normal, "toggle: FullScreen -> Normal");
        Check(CouchWindowPolicy.NextFullscreenState(WindowState.Maximized) == WindowState.FullScreen,
            "toggle: Maximized (zoomed window) -> FullScreen, not an un-zoom (upstream went to Normal)");
        Check(CouchWindowPolicy.NextFullscreenState(WindowState.Minimized) == WindowState.FullScreen,
            "toggle: Minimized -> FullScreen (--fullscreen while minimized during load, as the one-way VM did)");
        foreach (WindowState state in States)
        {
            Check((CouchWindowPolicy.NextFullscreenState(state) is WindowState.FullScreen) != (state is WindowState.FullScreen),
                $"toggle: {state} -> {(state is WindowState.FullScreen ? "leaves" : "enters")} full screen");
        }
        Check(CouchWindowPolicy.FullscreenToggleDelayMs >= 900, "toggle: debounce covers the AppKit animation (>= 900 ms)");
        Check(CouchWindowPolicy.ToggleDebounced(10_000 + CouchWindowPolicy.FullscreenToggleDelayMs - 1, 10_000), "toggle: a press inside the debounce is ignored");
        Check(!CouchWindowPolicy.ToggleDebounced(10_000 + CouchWindowPolicy.FullscreenToggleDelayMs, 10_000), "toggle: a press after the debounce is taken");
        long now = Environment.TickCount64;
        Check(!CouchWindowPolicy.ToggleDebounced(now, now - CouchWindowPolicy.FullscreenToggleDelayMs),
            "toggle: the first toggle (--fullscreen right after the window opens) is never debounced");
    }

    static void EscapeTable()
    {
        foreach (WindowState state in States)
        foreach (bool running in new[] { false, true })
        foreach (bool confirm in new[] { false, true })
        foreach (bool couch in new[] { false, true })
        {
            Escape action = CouchWindowPolicy.ForEscape(state, running, confirm, couch);
            Escape expected = state is WindowState.FullScreen ? Escape.LeaveFullScreen
                : running && confirm && !couch ? Escape.ShowExitPrompt : Escape.None;
            Check(action == expected, $"escape {state} running={running} confirm={confirm} couch={couch} -> {expected}");
        }

        // The cases that ended couch matches before: windowed, exit prompt off.
        Check(CouchWindowPolicy.ForEscape(WindowState.Normal, true, false, true) == Escape.None, "escape: couch, windowed, prompt off never stops the game");
        Check(CouchWindowPolicy.ForEscape(WindowState.Normal, true, false, false) == Escape.None, "escape: prompt off never stops the game outside couch either");
    }

    static void Hotkeys()
    {
        KeyboardHotkeyState[] blocked =
        [
            KeyboardHotkeyState.ToggleVSyncMode, KeyboardHotkeyState.ShowUI, KeyboardHotkeyState.Pause,
            KeyboardHotkeyState.ResScaleUp, KeyboardHotkeyState.ResScaleDown,
            KeyboardHotkeyState.CustomVSyncIntervalIncrement, KeyboardHotkeyState.CustomVSyncIntervalDecrement,
        ];

        foreach (KeyboardHotkeyState state in Enum.GetValues<KeyboardHotkeyState>())
        {
            bool expected = !blocked.Contains(state);
            Check(CouchWindowPolicy.AllowsHotkey(state, true) == expected, $"hotkey couch {state} -> {(expected ? "allowed" : "blocked")}");
            Check(CouchWindowPolicy.AllowsHotkey(state, false), $"hotkey outside couch {state} -> allowed");
        }

        Check(!CouchWindowPolicy.AllowsDockedModeToggle(true) && CouchWindowPolicy.AllowsDockedModeToggle(false), "F9 docked toggle: off in couch only");
        Check(!CouchWindowPolicy.AllowsCacheLoadCancel(true) && CouchWindowPolicy.AllowsCacheLoadCancel(false), "Delete cache-load cancel: off in couch only");
    }

    static void GlobeF()
    {
        const ulong fn = 1UL << 23, shift = 1UL << 17, control = 1UL << 18, option = 1UL << 19, command = 1UL << 20, caps = 1UL << 16;
        const ulong keyDown = 10, keyUp = 11;

        Check(CouchWindowPolicy.IsGlobeF(keyDown, fn, 3), "globe-f: fn+F toggles");
        Check(CouchWindowPolicy.IsGlobeF(keyDown, fn | caps, 3), "globe-f: fn+F with caps lock on toggles");
        Check(!CouchWindowPolicy.IsGlobeF(keyDown, 0, 3), "globe-f: plain F does not");
        Check(!CouchWindowPolicy.IsGlobeF(keyUp, fn, 3), "globe-f: key up does not");
        Check(!CouchWindowPolicy.IsGlobeF(keyDown, fn, 4), "globe-f: fn+H does not");
        Check(!CouchWindowPolicy.IsGlobeF(keyDown, control | command, 3), "globe-f: Ctrl+Cmd+F is left to its key binding");
        foreach (ulong extra in new[] { shift, control, option, command })
        {
            Check(!CouchWindowPolicy.IsGlobeF(keyDown, fn | extra, 3), $"globe-f: fn+F with modifier 0x{extra:x} does not");
        }
    }

    static void DrawableSize()
    {
        DrawableSizeRequest request = new();
        Check(!request.TryTake(out _, out _), "drawable: nothing pending at start");

        request.Request(1920, 1080);
        request.Request(3840, 2160);
        Check(request.TryTake(out int w, out int h) && w == 3840 && h == 2160, "drawable: the latest request wins");
        Check(!request.TryTake(out _, out _), "drawable: a request is taken once");

        request.Request(0, 2160);
        Check(!request.TryTake(out _, out _) && !request.TryTake(out _, out _), "drawable: a zero side is dropped, not left pending");
        request.Request(int.MaxValue, int.MaxValue);
        Check(request.TryTake(out w, out h) && w == int.MaxValue && h == int.MaxValue, "drawable: full int range round-trips");
        request.Request(1, 1);
        Check(request.TryTake(out w, out h) && w == 1 && h == 1, "drawable: smallest size round-trips");

        // A UI thread resizing while the render thread presents: every taken pair must be one request.
        (int taken, int torn, bool ordered, int last) = Race(request.Request, (out int tw, out int th) => request.TryTake(out tw, out th));
        Check(taken > 1_000, $"drawable: the render thread raced the UI thread ({taken} takes)");
        Check(torn == 0, $"drawable: no torn width/height pair under a concurrent writer ({torn} torn of {taken})");
        Check(ordered, "drawable: requests are taken in order, none twice");
        Check(last == RaceRequests, "drawable: the last request is never lost");

        // For comparison only (not a check): the two plain int fields the Metal window used before.
        LegacyHandoff legacy = new();
        (int legacyTaken, int legacyTorn, _, int legacyLast) = Race(legacy.Set, legacy.Take);
        Console.WriteLine($"INFO drawable: old two-field handoff took {legacyTaken}, torn {legacyTorn}, last width {legacyLast} of {RaceRequests}");
    }

    const int RaceRequests = 500_000;

    delegate bool TakeSize(out int width, out int height);

    static int Expected(int width) => width * 3 + 1;

    static (int Taken, int Torn, bool Ordered, int Last) Race(Action<int, int> request, TakeSize take)
    {
        // Warm both paths so neither thread starts late on a JIT compile.
        request(1, Expected(1));
        take(out _, out _);

        int taken = 0, torn = 0, last = 0;
        bool ordered = true;
        using Barrier start = new(2);
        using ManualResetEventSlim done = new(false);
        Thread writer = new(() =>
        {
            start.SignalAndWait();
            for (int i = 1; i <= RaceRequests; i++)
            {
                request(i, Expected(i));
                Thread.SpinWait(20);
            }
            done.Set();
        });
        writer.Start();
        start.SignalAndWait();
        while (true)
        {
            bool finished = done.IsSet;
            while (take(out int w, out int h))
            {
                taken++;
                if (h != Expected(w)) torn++;
                if (w <= last) ordered = false;
                last = w;
            }
            if (finished) break;
        }
        writer.Join();
        return (taken, torn, ordered, last);
    }

    sealed class LegacyHandoff
    {
        int _width, _height;

        public void Set(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public bool Take(out int width, out int height)
        {
            width = height = 0;
            if (_width == 0 || _height == 0) return false;
            width = _width;
            height = _height;
            _width = 0;
            _height = 0;
            return true;
        }
    }

    static int Main()
    {
        AvaloniaBinding();
        ChromeTable();
        Toggle();
        EscapeTable();
        Hotkeys();
        GlobeF();
        DrawableSize();

        Console.WriteLine($"RESULT checks={_checks} failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }
}
