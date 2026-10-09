#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$PROJECT_DIR/build"
APP_DIR="$PROJECT_DIR/dist/Smash Couch Design Preview.app"
mkdir -p "$BUILD_DIR" "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" "$PROJECT_DIR/work/evidence" "$PROJECT_DIR/work/preview-data"
swift_sources=()
while IFS= read -r path; do swift_sources+=("$path"); done < <(find "$PROJECT_DIR/Sources" -name '*.swift' -not -path '*/Runtime/*' -type f | sort)
xcrun swiftc -sdk /Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk -O -swift-version 5 -target arm64-apple-macos14.0 "${swift_sources[@]}" -o "$BUILD_DIR/SmashCouchPreview" -framework AppKit -framework SwiftUI
cp "$BUILD_DIR/SmashCouchPreview" "$APP_DIR/Contents/MacOS/SmashCouchPreview"
cp "$PROJECT_DIR/Assets/arena.png" "$APP_DIR/Contents/Resources/arena.png"
python3 - "$PROJECT_DIR" "$APP_DIR" <<'PY'
import plistlib,sys
from pathlib import Path
root,app=map(Path,sys.argv[1:])
metadata=dict(CFBundleExecutable='SmashCouchPreview',CFBundleIdentifier='local.smash.couch.design-preview-final',CFBundleName='Smash Couch Design Preview',CFBundleDisplayName='Smash Couch Design Preview',CFBundlePackageType='APPL',CFBundleShortVersionString='2.0.0-preview',CFBundleVersion='3',LSMinimumSystemVersion='14.0',NSHighResolutionCapable=True,GCSupportsControllerUserInteraction=True,NSPrincipalClass='NSApplication',SmashCouchPreviewOnly=True,SmashCouchPreviewRoot=str(root/'work/preview-data'),SmashCouchEvidenceRoot=str(root/'work/evidence'))
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(metadata))
PY
/usr/bin/codesign --force --sign - "$APP_DIR"
/usr/bin/codesign --verify --strict "$APP_DIR"
echo "Built $APP_DIR"
