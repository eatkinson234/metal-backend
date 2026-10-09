#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$ROOT/build/launcher-output"
APP="$OUTPUT/Smash Couch Native Metal 53 Preview.app"
EXE="$OUTPUT/SmashCouchNativeMetal"
OBJ="$OUTPUT/SocketSender.o"

if [ -e "$APP" ] || [ -e "$EXE" ] || [ -e "$OBJ" ]; then
  echo 'Launcher build output already exists; preserve and inspect it before rebuilding.' >&2
  exit 1
fi
mkdir -p "$OUTPUT/module-cache" "$OUTPUT/tmp"
TMPDIR="$OUTPUT/tmp" xcrun clang -mmacosx-version-min=14.0 -c \
  "$ROOT/Sources/SocketSender.c" -o "$OBJ"
TMPDIR="$OUTPUT/tmp" xcrun swiftc -O -swift-version 5 -target arm64-apple-macos14.0 \
  -module-cache-path "$OUTPUT/module-cache" \
  "$ROOT/Sources/LobbyGate.swift" \
  "$ROOT/Sources/NativeLobbyConfiguration.swift" \
  "$ROOT/Sources/NativePreviewEnvironment.swift" \
  "$ROOT/Sources/NativePreviewSessionClaim.swift" \
  "$ROOT/Sources/SlotBook.swift" \
  "$ROOT/Sources/NativeSelfTelemetrySupervisor.swift" \
  "$ROOT/Sources/NativeLobbyLifecycle.swift" \
  "$ROOT/Sources/NativeLobbyApp.swift" \
  "$OBJ" -o "$EXE" \
  -framework AppKit -framework SwiftUI -framework GameController \
  -framework CoreHaptics -framework Security

mkdir -p "$APP/Contents/MacOS"
cp "$EXE" "$APP/Contents/MacOS/SmashCouchNativeMetal"
/usr/bin/python3 - "$APP" <<'PY'
import plistlib, sys
from pathlib import Path

app = Path(sys.argv[1])
plist = app / 'Contents' / 'Info.plist'
plist.write_bytes(plistlib.dumps({
    'CFBundleExecutable': 'SmashCouchNativeMetal',
    'CFBundleIdentifier': 'local.smash.couch.native-metal53-preview-r1',
    'CFBundleName': 'Smash Couch Native Metal 53 Preview',
    'CFBundleDisplayName': 'Smash Couch Native Metal 53 Preview',
    'CFBundlePackageType': 'APPL',
    'CFBundleVersion': '53.2.0',
    'CFBundleShortVersionString': '53.2.0',
    'LSMinimumSystemVersion': '14.0',
    'NSHighResolutionCapable': True,
    'GCSupportsControllerUserInteraction': True,
    'LSApplicationCategoryType': 'public.app-category.games',
}))
PY
/usr/bin/codesign --force --sign - --identifier local.smash.couch.native-metal53-preview-r1 "$APP"
/usr/bin/codesign --verify --deep --strict "$APP"
echo "Built Smash Couch Native Metal launcher: $APP"
