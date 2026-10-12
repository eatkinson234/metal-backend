using Avalonia.Controls;
using Ryujinx.Ava.Common;
using System;

namespace Ryujinx.Ava.UI.Helpers
{
    /// <summary>
    /// Window rules for full screen and for a Smash Couch session (the launcher's controller bridge is active).
    /// Pure functions of their inputs so CPU tests can check them; the callers own the side effects.
    /// </summary>
    internal static class CouchWindowPolicy
    {
        // Same test as CouchBridge.Enabled in Ryujinx.Input.SDL2: the launcher (and the rig) always pass the
        // bridge socket, and nothing else sets it.
        public static readonly bool CouchMode = Environment.GetEnvironmentVariable("SMASH_COUCH_SOCKET") is { Length: > 0 };

        // Longer than the AppKit full-screen animation (about 0.5-0.9 s), so a second press cannot land mid-transition.
        public const int FullscreenToggleDelayMs = 1000;

        private const ulong NSEventTypeKeyDown = 10;
        private const ulong NSEventModifierFlagFunction = 1UL << 23;
        private const ushort KeyCodeF = 3; // kVK_ANSI_F

        public readonly record struct Chrome(bool IsFullScreen, bool ExtendsContentIntoTitleBar, bool? ShowMenuAndStatusBar);

        public enum EscapeAction
        {
            None,
            LeaveFullScreen,
            ShowExitPrompt,
        }

        /// <summary>
        /// Chrome to apply after the real window state changed, or null when nothing should change: only entering or
        /// leaving full screen changes the chrome (minimizing and zooming keep it, as before).
        /// ShowMenuAndStatusBar is null when the menu should keep its current value (no game running).
        /// </summary>
        public static Chrome? ChromeFor(WindowState state, bool wasFullScreen, bool gameRunning, bool showTitleBar, bool couchMode)
        {
            if (state is WindowState.Minimized)
            {
                return null;
            }

            bool fullScreen = state is WindowState.FullScreen;

            if (fullScreen == wasFullScreen)
            {
                return null;
            }

            bool? menu = gameRunning ? MenuAndStatusBarVisible(!fullScreen, couchMode) : null;

            return new Chrome(fullScreen, fullScreen || !showTitleBar, menu);
        }

        /// <summary>
        /// A couch session never shows the Ryujinx menu or status bar: Options could save a configuration the
        /// launcher then refuses at the next launch, and the menu puts Stop and Settings one click away.
        /// </summary>
        public static bool MenuAndStatusBarVisible(bool requested, bool couchMode) => requested && !couchMode;

        /// <summary>
        /// Full screen goes to Normal; every other state enters full screen. The state is the real window's (two-way
        /// binding), so a zoomed window (Magnet, a title-bar double-click, or the frame AppKit restores when leaving full
        /// screen) is Maximized here; upstream's "anything but Normal goes to Normal" made that press, and --fullscreen
        /// at startup, un-zoom the window instead. AppKit enters full screen from a zoomed window directly.
        /// </summary>
        public static WindowState NextFullscreenState(WindowState current) =>
            current is WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

        public static bool ToggleDebounced(long nowMs, long lastToggleMs) => nowMs - lastToggleMs < FullscreenToggleDelayMs;

        /// <summary>
        /// Escape leaves full screen. It only offers to stop the game where a confirmation will be asked:
        /// with the exit prompt off, or in a couch session, it never stops emulation.
        /// </summary>
        public static EscapeAction ForEscape(WindowState state, bool gameRunning, bool confirmExit, bool couchMode)
        {
            if (state is WindowState.FullScreen)
            {
                return EscapeAction.LeaveFullScreen;
            }

            return gameRunning && confirmExit && !couchMode ? EscapeAction.ShowExitPrompt : EscapeAction.None;
        }

        /// <summary>
        /// Single-key hotkeys that change emulation state (pacing, pause, resolution) or show the menu stay off in a
        /// couch session; screenshot, mute and volume stay available.
        /// </summary>
        public static bool AllowsHotkey(KeyboardHotkeyState state, bool couchMode) => !couchMode || state switch
        {
            KeyboardHotkeyState.ToggleVSyncMode or
            KeyboardHotkeyState.ShowUI or
            KeyboardHotkeyState.Pause or
            KeyboardHotkeyState.ResScaleUp or
            KeyboardHotkeyState.ResScaleDown or
            KeyboardHotkeyState.CustomVSyncIntervalIncrement or
            KeyboardHotkeyState.CustomVSyncIntervalDecrement => false,
            _ => true,
        };

        // F9 (docked mode) and Delete (cancel the shader cache load) are one key each; a couch session keeps the
        // docked mode the launcher pinned and always loads the cache.
        public static bool AllowsDockedModeToggle(bool couchMode) => !couchMode;

        public static bool AllowsCacheLoadCancel(bool couchMode) => !couchMode;

        /// <summary>
        /// Fn/Globe+F, the standard macOS full-screen shortcut. Avalonia drops the fn modifier (the key arrives as a
        /// plain F), so the decision uses the AppKit event that is being dispatched.
        /// </summary>
        public static bool IsGlobeF(ulong eventType, ulong modifierFlags, ushort keyCode)
        {
            const ulong otherModifiers = (1UL << 17) | (1UL << 18) | (1UL << 19) | (1UL << 20); // shift, control, option, command

            return eventType == NSEventTypeKeyDown &&
                keyCode == KeyCodeF &&
                (modifierFlags & NSEventModifierFlagFunction) != 0 &&
                (modifierFlags & otherModifiers) == 0;
        }
    }
}
