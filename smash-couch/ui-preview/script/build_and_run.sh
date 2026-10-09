#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
MODE="${1:-run}"
case "$MODE" in run|--verify) ;; *) echo 'Supported modes: run, --verify' >&2; exit 2 ;; esac
if /usr/bin/pgrep -x SmashCouchPreview >/dev/null; then
  echo 'Close the owned Smash Couch Preview window normally, then run again.' >&2
  exit 1
fi
"$PROJECT_DIR/scripts/build.sh"
/usr/bin/open -n "$PROJECT_DIR/dist/Smash Couch Design Preview.app" --args --scene empty
case "$MODE" in
  run) ;;
  --verify) /usr/bin/pgrep -x SmashCouchPreview >/dev/null ;;
esac
