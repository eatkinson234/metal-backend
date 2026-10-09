# Smash Couch light UI preview

Light Home layout and a controller-first preset chooser with explicit Jump, Quick attack and Special rows. Families: Auto, Xbox, PlayStation, Switch and Other. Main presets: Smash standard, A-to-jump and Brawlhalla-style; older layouts remain under More layouts.

The preview uses simulated players. Play records an intent and cannot start a game. No live controller discovery, production socket, emulator or production launch lock is constructed by its preview runtime. `Sources/Runtime/CouchModel.swift` retains the earlier live model and is excluded from the build. All changes are local to the preview's workspace data.

Build and software checks:

```sh
./scripts/test.sh
./scripts/build.sh
```

The scripts require Apple Command Line Tools and the pinned macOS 26.5 SDK. The result is `dist/Smash Couch Design Preview.app`. The recorded revision passed 169 software checks and native UI inspection. No new build or runtime test was run for publication. Physical controllers, live integration, TV and installation remain pending.

Xbox A-to-jump maps A or Y to jump, X to quick attack, and B to special. Brawlhalla-style maps A to jump, X to quick attack, and B or Y to special. Controller-family labels adapt to the selected pad. These are Smash button mappings; no full Brawlhalla-mechanics parity is claimed.

Keyboard: Command-1 Home, Command-2 Controllers, Command-comma Settings, Tab/Shift-Tab focus, Space/Return activate, Escape back. Preview Scenes and Simulated Controller menus expose software states for review.

Original artwork: [arena.png](Assets/arena.png), with [generation prompt](Assets/arena-prompt.txt). It does not depict actual gameplay. Ryujinx-derived code retains [its license](LICENSE-Ryujinx.txt).
