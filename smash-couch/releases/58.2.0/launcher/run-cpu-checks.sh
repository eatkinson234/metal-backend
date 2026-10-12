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
  "$ROOT/Sources/ControllerLayouts.swift" \
  "$ROOT/Sources/SlotBook.swift" \
  "$ROOT/Sources/NativeSelfTelemetrySupervisor.swift" \
  "$ROOT/Sources/NativeLobbyLifecycle.swift" \
  "$ROOT/Tests/SelfTelemetrySelfTest.swift" \
  "$ROOT/Tests/LobbySelfTest.swift" \
  -framework Security \
  -o "$BUILD/LobbySelfTest"

"$BUILD/LobbySelfTest" "$ROOT"

xcrun swiftc -sdk /Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk -O -swift-version 5 -target arm64-apple-macos14.0 -module-cache-path "$BUILD/module-cache" -emit-executable \
  "$ROOT/Sources/LobbyGate.swift" \
  "$ROOT/Sources/NativeLobbyConfiguration.swift" \
  "$ROOT/Sources/NativePreviewEnvironment.swift" \
  "$ROOT/Sources/NativePreviewSessionClaim.swift" \
  "$ROOT/Sources/ControllerLayouts.swift" \
  "$ROOT/Sources/SlotBook.swift" \
  "$ROOT/Sources/NativeSelfTelemetrySupervisor.swift" \
  "$ROOT/Sources/NativeLobbyLifecycle.swift" \
  "$ROOT/Sources/LogoMark.swift" \
  "$ROOT/Sources/CouchDesign.swift" \
  "$ROOT/Sources/NativeLobbyApp.swift" \
  "$BUILD/SocketSender.o" \
  -framework AppKit -framework SwiftUI -framework GameController \
  -framework CoreHaptics -framework Security \
  -o "$BUILD/NativeLobbyCompileOnly"
