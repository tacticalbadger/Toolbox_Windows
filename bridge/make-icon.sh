#!/usr/bin/env bash
# Renders the app icon with bridge/make-icon.swift and produces
#   App/Resources/AppIcon.icns                      (SwiftPM bundle)
#   App/Resources/Assets.xcassets/AppIcon.appiconset (Xcode)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RES="$ROOT/App/Resources"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
unset DEVELOPER_DIR

swift "$ROOT/bridge/make-icon.swift" "$TMP/AppIcon-1024.png" >/dev/null
mkdir -p "$TMP/AppIcon.iconset" "$RES/Assets.xcassets/AppIcon.appiconset"
while read -r size name; do
  sips -z "$size" "$size" "$TMP/AppIcon-1024.png" --out "$TMP/AppIcon.iconset/$name.png" >/dev/null
done <<'SIZES'
16 icon_16x16
32 icon_16x16@2x
32 icon_32x32
64 icon_32x32@2x
128 icon_128x128
256 icon_128x128@2x
256 icon_256x256
512 icon_256x256@2x
512 icon_512x512
1024 icon_512x512@2x
SIZES
iconutil -c icns "$TMP/AppIcon.iconset" -o "$RES/AppIcon.icns"
cp "$TMP/AppIcon.iconset/"*.png "$RES/Assets.xcassets/AppIcon.appiconset/"
echo "ok: $RES/AppIcon.icns"
