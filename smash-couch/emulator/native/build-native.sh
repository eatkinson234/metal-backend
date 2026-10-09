#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$ROOT/build"
if [ -e "$OUTPUT" ]; then
  echo 'Preserve existing native output; use a fresh source copy.' >&2
  exit 1
fi
mkdir "$OUTPUT"
xcrun --sdk macosx clang++ -std=c++17 -fobjc-arc -fblocks -pthread -Wall -Wextra \
  -dynamiclib -target arm64-apple-macosx14.0 \
  -isysroot "$(xcrun --sdk macosx --show-sdk-path)" \
  -install_name @rpath/libSmashFramePhase.dylib \
  "$ROOT/present_buffer_observer_v1.mm" "$ROOT/presented_handler_bridge.mm" \
  -framework Metal -framework QuartzCore -framework Foundation \
  -o "$OUTPUT/libSmashFramePhase.dylib"
