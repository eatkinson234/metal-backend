#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$ROOT/build/launcher-output"
APP="$OUTPUT/Smash Couch Smooth.app"
EXE="$OUTPUT/SmashCouchNativeMetal"
OBJ="$OUTPUT/SocketSender.o"

if [ -e "$APP" ] || [ -e "$EXE" ] || [ -e "$OBJ" ]; then
  echo 'Launcher build output already exists; preserve and inspect it before rebuilding.' >&2
  exit 1
fi
mkdir -p "$OUTPUT/module-cache" "$OUTPUT/tmp"
TMPDIR="$OUTPUT/tmp" xcrun clang -mmacosx-version-min=14.0 -c \
  "$ROOT/Sources/SocketSender.c" -o "$OBJ"
TMPDIR="$OUTPUT/tmp" xcrun swiftc -sdk /Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk -O -swift-version 5 -target arm64-apple-macos14.0 \
  -module-cache-path "$OUTPUT/module-cache" \
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
  "$OBJ" -o "$EXE" \
  -framework AppKit -framework SwiftUI -framework GameController \
  -framework CoreHaptics -framework Security

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$EXE" "$APP/Contents/MacOS/SmashCouchNativeMetal"

# App icon: render LogoMark (the same view the header uses) into an .icns.
ICON="$OUTPUT/icon"
mkdir -p "$ICON/AppIcon.iconset"
TMPDIR="$OUTPUT/tmp" xcrun swiftc -parse-as-library -sdk /Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk -O -swift-version 5 \
  -target arm64-apple-macos14.0 -module-cache-path "$OUTPUT/module-cache" \
  "$ROOT/Sources/LogoMark.swift" "$ROOT/Tools/RenderIcon.swift" -o "$ICON/RenderIcon"
"$ICON/RenderIcon" "$ICON/icon-1024.png" 1024
for size in 16 32 128 256 512; do
  /usr/bin/sips -Z "$size" "$ICON/icon-1024.png" --out "$ICON/AppIcon.iconset/icon_${size}x${size}.png" >/dev/null
  /usr/bin/sips -Z "$((size * 2))" "$ICON/icon-1024.png" --out "$ICON/AppIcon.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
/usr/bin/iconutil -c icns "$ICON/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
/usr/bin/python3 - "$APP" <<'PY'
import plistlib, sys
from pathlib import Path

app = Path(sys.argv[1])
plist = app / 'Contents' / 'Info.plist'
plist.write_bytes(plistlib.dumps({
    'CFBundleExecutable': 'SmashCouchNativeMetal',
    'CFBundleIdentifier': 'local.smash.couch.couch55-r1',
    'CFBundleName': 'Smash Couch',
    'CFBundleDisplayName': 'Smash Couch',
    'CFBundlePackageType': 'APPL',
    'CFBundleVersion': '58.2.0',
    'CFBundleShortVersionString': '58.2.0',
    'LSMinimumSystemVersion': '14.0',
    'NSHighResolutionCapable': True,
    'GCSupportsControllerUserInteraction': True,
    'LSApplicationCategoryType': 'public.app-category.games',
    'CFBundleIconFile': 'AppIcon',
}))
PY
/usr/bin/codesign --force --sign - --identifier local.smash.couch.couch55-r1 "$APP"
/usr/bin/codesign --verify --deep --strict "$APP"
echo "Built Smash Couch Native Metal launcher: $APP"
