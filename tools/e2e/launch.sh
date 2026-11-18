#!/usr/bin/env bash
# Relaunches build/Toolbox.app against the fixture, with the Touch ID step
# skipped for the run and a separate Keychain item for the vault key:
#   tools/e2e/launch.sh [--fixture dir] [app args…]
#   tools/e2e/launch.sh --connect echo --connect echo-1p
# App args: --connect <id>, --files <id> (repeatable), --expand-all,
# --demo-move, --demo-close, --edit-connection <id|new>, --edit-provider <id|new>,
# --set-folder-credential <path>, --unlock-vault.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="$ROOT/build/e2e"
if [[ "${1:-}" == "--fixture" ]]; then DIR="$2"; shift 2; fi
[[ -f "$DIR/store.json" ]] || { echo "no fixture at $DIR; run tools/e2e/make-fixture.sh first" >&2; exit 1; }
[[ -d "$ROOT/build/Toolbox.app" ]] || { echo "no app bundle; run make app first" >&2; exit 1; }

pkill -x Toolbox 2>/dev/null || true
sleep 1
open -n "$ROOT/build/Toolbox.app" \
  --env TOOLBOX_STORE_PATH="$DIR/store.json" \
  --env TOOLBOX_KNOWN_HOSTS="$DIR/known_hosts" \
  --env TOOLBOX_VAULT_PATH="$DIR/vault.json" \
  --env TOOLBOX_KEYCHAIN_SERVICE=com.dobbs.Toolbox.vault-key.e2e \
  --args "$@" -vaultRequiresAuth NO
echo "launched with: $*"
