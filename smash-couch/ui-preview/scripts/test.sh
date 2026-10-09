#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SDK_DIR=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk
mkdir -p "$PROJECT_DIR/build" "$PROJECT_DIR/work/evidence"
xcrun swiftc -sdk "$SDK_DIR" -swift-version 5 "$PROJECT_DIR/Sources/Models/ControllerProfiles.swift" "$PROJECT_DIR/Sources/Models/SlotBook.swift" "$PROJECT_DIR/Sources/Models/Configuration.swift" "$PROJECT_DIR/Tests/SlotTests.swift" -o "$PROJECT_DIR/build/SlotTests"
"$PROJECT_DIR/build/SlotTests"
xcrun swiftc -sdk "$SDK_DIR" -swift-version 5 "$PROJECT_DIR/Sources/Models/ControllerProfiles.swift" "$PROJECT_DIR/Sources/Models/SlotBook.swift" "$PROJECT_DIR/Sources/Models/Configuration.swift" "$PROJECT_DIR/Sources/Models/LobbyStore.swift" "$PROJECT_DIR/Sources/Models/LauncherPresentation.swift" "$PROJECT_DIR/Tests/PresentationTests.swift" -o "$PROJECT_DIR/build/PresentationTests" -framework AppKit -framework SwiftUI
"$PROJECT_DIR/build/PresentationTests" --scene empty

xcrun swiftc -sdk "$SDK_DIR" -swift-version 5 "$PROJECT_DIR/Sources/Models/ControllerProfiles.swift" "$PROJECT_DIR/Sources/Models/SlotBook.swift" "$PROJECT_DIR/Sources/Models/Configuration.swift" "$PROJECT_DIR/Tests/ControllerPresetTests.swift" -o "$PROJECT_DIR/build/ControllerPresetTests"
"$PROJECT_DIR/build/ControllerPresetTests"
