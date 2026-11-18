#!/usr/bin/env bash
# Lists Toolbox's windows, screenshots each on-screen one, and reports any
# system dialog (Keychain, Touch ID) that is up. Needs Screen Recording
# permission for the terminal running it.
#   tools/e2e/capture.sh [out-dir] [prefix]
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="${1:-$ROOT/build/e2e/shots}"
PREFIX="${2:-shot}"
mkdir -p "$OUT"
PID="$(pgrep -x Toolbox | head -1)" || { echo "Toolbox is not running" >&2; exit 1; }

n=0
while read -r line; do
  echo "$line"
  case "$line" in *"onscreen=1"*|*"onscreen=true"*) ;; *) continue ;; esac
  n=$((n + 1))
  id="$(sed -E 's/.*id=([0-9]+).*/\1/' <<<"$line")"
  screencapture -x -l "$id" "$OUT/$PREFIX-$n.png" && echo "   -> $OUT/$PREFIX-$n.png"
done < <(swift "$HERE/winpid.swift" "$PID" 2>/dev/null)
swift "$HERE/dialogs.swift" 2>/dev/null
