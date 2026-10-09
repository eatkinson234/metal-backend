# Source and build instructions

## Reconstruct the exact emulator source

Use a fresh directory. Run these commands from the repository root:

```sh
git clone https://github.com/skybuleli/Ryubing.git /path/to/ryubing53
git -C /path/to/ryubing53 checkout --detach eb6b0e9adca0c2f010fa6ac093c806c4db199283
git -C /path/to/ryubing53 apply --check "$PWD/smash-couch/emulator/native-metal53.patch"
git -C /path/to/ryubing53 apply "$PWD/smash-couch/emulator/native-metal53.patch"
python3 smash-couch/emulator/verify-source.py /path/to/ryubing53
```

`overlay/` exposes the same 109 changed/added files for inspection. The patch contains their complete delta against the pinned upstream, including file modes. The verifier checks every source file and membership, ignoring only `.git`, `bin` and `obj`, as the original build inventory did. A source match does not prove binary identity or runtime behavior.

## Build boundary

The recorded emulator build used .NET SDK 9.0.305, Release/osx-arm64 self-contained publish, one MSBuild worker, and a frozen 165-package offline dependency closure. Its native bridge was built from the four files in `emulator/native/`. The included bridge build script creates a fresh local output and does not load the library or run a GPU workload.

The original exact build/package procedure also has machine-bound guards, offline dependencies, packaging inputs and signed artifact receipts that remain local. This repository is a source snapshot, not a portable installer or a claim that those original commands can run unchanged on another Mac. A new build needs dependency preparation, proper native-library placement, macOS bundle fix-up/signing and its own runtime acceptance. Do not copy private keys, firmware, games, saves or caches into this repository to satisfy those dependencies.

## Launcher

From `smash-couch/launcher`:

```sh
./run-cpu-checks.sh
./build-launcher.sh
```

These create local test/build outputs and require Apple Command Line Tools on an arm64 Mac. The launcher build refuses an existing output. The tests create synthetic artifacts under their local build/test directories. The original serializer round-trip evidence is recorded separately; it requires the actual `.53` runtime and is not rerun by these commands. The installer source is included for review, but its machine-specific release manifest and signed payload are intentionally absent. Do not execute it as a release installation from this snapshot.

## UI preview

From `smash-couch/ui-preview`:

```sh
./scripts/test.sh
./scripts/build.sh
```

The existing scripts pin the macOS 26.5 SDK because the original machine's macOS 27 SDK exposed SwiftUI macros without the corresponding compiler plugin. They build a separate ad-hoc-signed `dist/Smash Couch Design Preview.app`. `script/build_and_run.sh` additionally opens that preview. It does not launch an emulator.

## Preserved fallback

The user's existing `.36` installation, R3 launcher, Desktop shortcut and current progress were left intact. Rollback from a future `.53` installation must retain both progress folders; it must not copy older saves over newer progress or purge original caches. No installation or rollback action is part of this GitHub update.
