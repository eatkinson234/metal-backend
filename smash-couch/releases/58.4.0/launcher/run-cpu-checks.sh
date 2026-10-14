#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
BUILD="$ROOT/build"
mkdir -p "$BUILD/module-cache" "$BUILD/tmp"
export TMPDIR="$BUILD/tmp"
export CLANG_MODULE_CACHE_PATH="$BUILD/module-cache"

xcrun clang -mmacosx-version-min=14.0 -c "$ROOT/Sources/SocketSender.c" -o "$BUILD/SocketSender.o"

xcrun swiftc -O -swift-version 5 -target arm64-apple-macos14.0 -module-cache-path "$BUILD/module-cache" -emit-executable \
  "$ROOT/Sources/LobbyGate.swift" \
  "$ROOT/Sources/NativeLobbyConfiguration.swift" \
  "$ROOT/Sources/NativePreviewEnvironment.swift" \
  "$ROOT/Sources/NativePreviewSessionClaim.swift" \
  "$ROOT/Sources/SlotBook.swift" \
  "$ROOT/Sources/InputTiming.swift" \
  "$ROOT/Sources/InputSampling.swift" \
  "$ROOT/Sources/NativeSelfTelemetrySupervisor.swift" \
  "$ROOT/Sources/NativeLobbyLifecycle.swift" \
  "$ROOT/Sources/LobbyWindowChrome.swift" \
  "$ROOT/Tests/SelfTelemetrySelfTest.swift" \
  "$ROOT/Tests/LobbySelfTest.swift" \
  -framework AppKit -framework Security \
  -o "$BUILD/LobbySelfTest"

"$BUILD/LobbySelfTest" "$ROOT"

xcrun swiftc -sdk /Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk -O -swift-version 5 -target arm64-apple-macos14.0 -module-cache-path "$BUILD/module-cache" -emit-executable \
  "$ROOT/Sources/LobbyGate.swift" \
  "$ROOT/Sources/NativeLobbyConfiguration.swift" \
  "$ROOT/Sources/NativePreviewEnvironment.swift" \
  "$ROOT/Sources/NativePreviewSessionClaim.swift" \
  "$ROOT/Sources/SlotBook.swift" \
  "$ROOT/Sources/InputTiming.swift" \
  "$ROOT/Sources/InputSampling.swift" \
  "$ROOT/Sources/NativeSelfTelemetrySupervisor.swift" \
  "$ROOT/Sources/NativeLobbyLifecycle.swift" \
  "$ROOT/Sources/LobbyWindowChrome.swift" \
  "$ROOT/Sources/LogoMark.swift" \
  "$ROOT/Sources/CouchDesign.swift" \
  "$ROOT/Sources/NativeLobbyApp.swift" \
  "$BUILD/SocketSender.o" \
  -framework AppKit -framework SwiftUI -framework GameController \
  -framework CoreHaptics -framework Security \
  -o "$BUILD/NativeLobbyCompileOnly"

# One layout and press-to-join (58.3): the D-pad slot choice, layout picker, LB/RB layout cycling and the
# player-count stepper are gone for good. Any of them coming back in shipped code fails the checks.
if grep -n -E 'D-pad|LB / RB|LayoutPicker|ControllerLayout|PlayerCountStepper|selectionByConnection|setLayout|setCapacity|is occupied|Choose P1' \
    "$ROOT/Sources/"*.swift "$ROOT/Preview/"*.swift; then
  echo 'FAIL retired lobby controls are still referenced (lines above)' >&2
  exit 1
fi
echo 'PASS no D-pad slot choice, layout picker, LB/RB layout cycling or player-count stepper remains'
