#!/usr/bin/env bash
# Builds the app with SwiftPM (Command Line Tools are enough; no Xcode
# license needed) and wraps the binary in build/Toolbox.app.
#
#   CONFIG=debug|release   (default debug)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${CONFIG:-debug}"
APP="$ROOT/build/Toolbox.app"
unset DEVELOPER_DIR

# The Command Line Tools lack SwiftUI's macro plugin; borrow Xcode's copy.
SWIFT_FLAGS=()
XCODE_PLUGINS="/Applications/Xcode.app/Contents/Developer/Platforms/MacOSX.platform/Developer/usr/lib/swift/host/plugins"
if [[ -d "$XCODE_PLUGINS" ]]; then
  SWIFT_FLAGS+=(-Xswiftc -plugin-path -Xswiftc "$XCODE_PLUGINS")
fi

# Code signing. The Keychain remembers "Always Allow" per code signature and
# an ad-hoc signature changes with every build, so prefer a real identity:
#   TOOLBOX_SIGN_IDENTITY=<name or SHA-1>   explicit choice
#   otherwise the first of: Developer ID Application, Apple Development,
#   Toolbox Local Signing (from `make signing`); ad hoc as a last resort.
pick_identity() {
  if [[ -n "${TOOLBOX_SIGN_IDENTITY:-}" ]]; then
    printf '%s\t%s\n' "$TOOLBOX_SIGN_IDENTITY" "$TOOLBOX_SIGN_IDENTITY"
    return
  fi
  local identities pattern line
  identities="$(security find-identity -v -p codesigning 2>/dev/null || true)"
  for pattern in "Developer ID Application:" "Apple Development:" "Toolbox Local Signing"; do
    line="$(printf '%s\n' "$identities" | grep -F "$pattern" | head -1 || true)"
    if [[ -n "$line" ]]; then
      # lines look like:  1) <sha1> "<name>"
      printf '%s\t%s\n' "$(awk '{print $2}' <<<"$line")" "$(sed -E 's/^[^"]*"(.*)"[^"]*$/\1/' <<<"$line")"
      return
    fi
  done
  printf -- '-\tad-hoc\n'
}
IFS=$'\t' read -r SIGN_ID SIGN_NAME < <(pick_identity)
SIGN_FLAGS=(--force --sign "$SIGN_ID")
case "$SIGN_NAME" in
  ad-hoc) ;;
  *"Developer ID Application"*) SIGN_FLAGS+=(--timestamp --options runtime) ;;  # what notarization needs
  *) SIGN_FLAGS+=(--timestamp=none) ;;
esac

cd "$ROOT/App"
swift build -c "$CONFIG" --product Toolbox "${SWIFT_FLAGS[@]}"
BIN_DIR="$(swift build -c "$CONFIG" --show-bin-path)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
cp "$BIN_DIR/Toolbox" "$APP/Contents/MacOS/Toolbox"
# SwiftPM resource bundles (e.g. SwiftTerm's) live next to the binary.
for bundle in "$BIN_DIR"/*.bundle; do
  [[ -e "$bundle" ]] && cp -R "$bundle" "$APP/Contents/Resources/"
done
# Package products built as dynamic libraries (RoyalVNCKit) are loaded via
# @rpath; ship them in Frameworks and point the executable there.
for dylib in "$BIN_DIR"/*.dylib; do
  [[ -e "$dylib" ]] || continue
  cp "$dylib" "$APP/Contents/Frameworks/"
  codesign "${SIGN_FLAGS[@]}" "$APP/Contents/Frameworks/$(basename "$dylib")" >/dev/null
done
install_name_tool -add_rpath "@executable_path/../Frameworks" "$APP/Contents/MacOS/Toolbox" 2>/dev/null || true
cp "$ROOT/App/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>Toolbox</string>
  <key>CFBundleIdentifier</key><string>com.dobbs.Toolbox</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>Toolbox</string>
  <key>CFBundleDisplayName</key><string>Toolbox</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSSupportsAutomaticTermination</key><false/>
  <key>NSSupportsSuddenTermination</key><false/>
</dict>
</plist>
PLIST

codesign "${SIGN_FLAGS[@]}" "$APP" >/dev/null
if [[ "$SIGN_ID" == "-" ]]; then
  echo "warning: signed ad hoc; the Keychain will ask about the vault key again after every rebuild." >&2
  echo "         Run 'make signing' once to give the app a stable identity." >&2
else
  echo "signed with: $SIGN_NAME"
fi
echo "ok: $APP"
