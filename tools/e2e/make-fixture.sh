#!/usr/bin/env bash
# Creates a throwaway Toolbox setup for end-to-end runs: SSH keys for the
# test servers, a known_hosts file, a fake `op` CLI, and a store with
# connections for the local echo (SSH 2222) and RDP (3390) servers plus a
# Royal-style folder tree. Nothing here touches the real app data; the app
# is pointed at it with the TOOLBOX_* environment overrides (see env.sh).
#
#   tools/e2e/make-fixture.sh [dir]     default: build/e2e (gitignored)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="${1:-$ROOT/build/e2e}"
mkdir -p "$DIR"
chmod 700 "$DIR"

[[ -f "$DIR/host_key" ]] || ssh-keygen -q -t ed25519 -N "" -C toolbox-e2e-host -f "$DIR/host_key"
[[ -f "$DIR/user_key" ]] || ssh-keygen -q -t ed25519 -N "" -C toolbox-e2e-user -f "$DIR/user_key"
printf '[127.0.0.1]:2222 %s\n' "$(cut -d' ' -f1,2 "$DIR/host_key.pub")" > "$DIR/known_hosts"

# Fake 1Password CLI: enough of `op item list` / `op item get` for the
# provider sync and credential resolution. Calls are logged to op.log.
cat > "$DIR/op" <<'FAKEOP'
#!/bin/sh
echo "$@" >> "$(dirname "$0")/op.log"
case "$1 $2" in
  "item list")
    echo '[{"id":"i1","title":"Echo login","vault":{"id":"v","name":"work"},"category":"LOGIN","additional_information":"matt"},
           {"id":"i2","title":"Lab SSH key","vault":{"id":"v","name":"work"},"category":"SSH_KEY"}]' ;;
  "item get")
    echo '{"id":"i1","fields":[{"id":"username","purpose":"USERNAME","value":"matt"},{"id":"password","purpose":"PASSWORD","value":"secret"}]}' ;;
  *) echo "[ERROR] unexpected arguments: $*" >&2; exit 1 ;;
esac
FAKEOP
chmod +x "$DIR/op"

# Store document (version 2). Ids are stable so launch args can name them.
sed -e "s|@DIR@|$DIR|g" > "$DIR/store.json" <<'STORE'
{
  "version": 2,
  "connections": [
    {"id":"echo","name":"jump01 (echo, key file)","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"matt","auth":{"KeyFile":{"path":"@DIR@/user_key"}},"folder":"Jump Boxes","tags":[],"source":null},
    {"id":"echo-1p","name":"Local echo via 1Password","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"echo-folder","name":"jump02 (folder credential)","kind":"Ssh","host":"127.0.0.1","port":2222,"username":"","auth":"FolderCredential","folder":"Jump Boxes","tags":[],"source":null},
    {"id":"rdp-local","name":"Local RDP (test server)","kind":"Rdp","host":"127.0.0.1","port":3390,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"vnc-local","name":"Local VNC (nothing listening)","kind":"Vnc","host":"127.0.0.1","port":5900,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":null,"tags":[],"source":null},
    {"id":"sftp-local","name":"Files over SFTP (echo server)","kind":"Sftp","host":"127.0.0.1","port":2222,"username":"matt","auth":{"KeyFile":{"path":"@DIR@/user_key"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ftp-local","name":"Files over FTP (plain)","kind":"Ftp","host":"127.0.0.1","port":2121,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ftps-local","name":"Files over FTPS (self-signed)","kind":"Ftps","host":"127.0.0.1","port":2122,"username":"","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"File Transfer","tags":[],"source":null},
    {"id":"ad:dc01","name":"DC01","kind":"Rdp","host":"dc01.corp.example","port":3389,"username":"matt","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"AAN/CORP-AD/Domain Controllers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:sql01","name":"SQL01","kind":"Rdp","host":"sql01.corp.example","port":3389,"username":"matt","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"AAN/CORP-AD/Datacenter Servers/Database Servers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:web01","name":"WEB01","kind":"Ssh","host":"web01.corp.example","port":22,"username":"matt","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"AAN/CORP-AD/Datacenter Servers/Web Servers","tags":["ad"],"source":"ad-fake"},
    {"id":"ad:mac01","name":"MAC-01","kind":"Vnc","host":"mac-01.corp.example","port":5900,"username":"matt","auth":{"Credential":{"credential_id":"op-fake:i1"}},"folder":"AAN/CORP-AD/Workstations","tags":["ad"],"source":"ad-fake"}
  ],
  "credentials": [
    {"id":"op-fake:i1","name":"Echo login","username":"matt","kind":{"OnePassword":{"item_id":"i1","account":"work","op_path":"@DIR@/op"}},"folder":"AAN/1Password/work","has_private_key":false,"tags":["login"],"source":"op-fake"},
    {"id":"op-fake:i2","name":"Lab SSH key","username":"","kind":{"OnePassword":{"item_id":"i2","account":"work","op_path":"@DIR@/op"}},"folder":"AAN/1Password/work","has_private_key":true,"tags":["ssh_key"],"source":"op-fake"},
    {"id":"folder-user","name":"Folder user (asks)","username":"folderuser","kind":"Prompt","folder":"Jump Boxes","has_private_key":false,"tags":[],"source":null},
    {"id":"ad-fake#bind","name":"CORP-AD bind account","username":"CORP\\svc-toolbox","kind":"Vault","folder":"AAN/CORP-AD","has_private_key":false,"tags":["provider-account"],"source":null}
  ],
  "providers": [
    {"id":"op-fake","name":"1Password","kind":{"OnePassword":{"op_path":"@DIR@/op","account":"work","vaults":["work"]}},"folder":"AAN/1Password","auto_refresh_minutes":60,"last_sync_epoch":null,"last_status":null},
    {"id":"ad-fake","name":"CORP-AD","kind":{"ActiveDirectory":{"server":"dc01.corp.example","search_base":"DC=corp,DC=example","bind_credential_id":"ad-fake#bind","admin_credential_id":"op-fake:i1","user_credential_id":null,"allow_plain_ldap":false,"accept_invalid_certs":false}},"folder":"AAN/CORP-AD","auto_refresh_minutes":0,"last_sync_epoch":null,"last_status":"fixture: never synced"}
  ],
  "folders": ["AAN", "File Transfer", "Jump Boxes"],
  "folder_credentials": {"Jump Boxes": "folder-user"}
}
STORE
chmod 600 "$DIR/store.json"
rm -f "$DIR/vault.json"

cat > "$DIR/env.sh" <<ENV
export TOOLBOX_STORE_PATH="$DIR/store.json"
export TOOLBOX_KNOWN_HOSTS="$DIR/known_hosts"
export TOOLBOX_VAULT_PATH="$DIR/vault.json"
export TOOLBOX_KEYCHAIN_SERVICE="com.dobbs.Toolbox.vault-key.e2e"
ENV

echo "fixture ready: $DIR"
echo "next: tools/e2e/servers.sh start && tools/e2e/launch.sh --connect echo --connect echo-1p && tools/e2e/capture.sh"
