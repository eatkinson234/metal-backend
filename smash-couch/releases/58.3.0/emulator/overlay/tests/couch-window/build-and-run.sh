#!/bin/bash
# Offline build + run of the couch-window harness with the pinned SDK and the .53 package closure (same settings as
# compile-check.sh), at low priority. CPU only: nothing is launched, rendered or installed.
# Refuses while the user's launcher or game is running.
# Usage: build-and-run.sh <run-label>
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
K="$(cd "$HERE/../../../.." && pwd)"
SDK="/Users/eatkinson234/Desktop/Smash Couch/work/controller-launcher/dotnet"
PREP="$K/shutdown-cancellation-implementation-r1/full-app-build-r1/preparation"
LABEL="$1"
pgrep -f 'Smash Couch Smooth.app/Contents/MacOS/SmashCouchNativeMetal' >/dev/null && { echo "user launcher open; not building"; exit 3; }
pgrep -f 'Smash Couch/Runtime/.*/MacOS/Ryujinx' >/dev/null && { echo "user game running; not building"; exit 3; }
mkdir -p "$HERE/runs" /private/tmp/smash-couch-window-tmp
export HOME="$HOME" LANG=en_US.UTF-8 TMPDIR=/private/tmp/smash-couch-window-tmp PATH="$SDK:/usr/bin:/bin:/usr/sbin:/sbin" \
  DOTNET_ROOT="$SDK" DOTNET_CLI_HOME="$HERE/obj/dotnet-home" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 \
  DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1 NUGET_PACKAGES="$PREP/nuget-packages" UseSharedCompilation=false
nice -n 15 "$SDK/dotnet" build "$HERE/CouchWindowTests.csproj" -c Release -r osx-arm64 --disable-build-servers \
  -p:RestoreConfigFile="$PREP/nuget.offline.config" -p:UseSharedCompilation=false -p:NodeReuse=false \
  > "$HERE/runs/$LABEL-build.log" 2>&1 || { echo "build failed"; grep -E ': error ' "$HERE/runs/$LABEL-build.log" | sort -u | head -30; tail -5 "$HERE/runs/$LABEL-build.log"; exit 1; }
nice -n 15 "$SDK/dotnet" "$HERE/bin/Release/net9.0/osx-arm64/CouchWindowTests.dll" > "$HERE/runs/$LABEL-run.log" 2>&1
rc=$?
grep -E "^(FAIL|RESULT)" "$HERE/runs/$LABEL-run.log" | head -60
echo "exit $rc"; exit $rc
