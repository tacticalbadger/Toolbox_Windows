#!/usr/bin/env bash
# Starts or stops the local test servers the fixture points at:
#   SSH + SFTP echo server on 127.0.0.1:2222 (matt / secret; any public key)
#   IronRDP test server   on 127.0.0.1:3390 (matt / secret)
#   FTP server            on 127.0.0.1:2121 (matt / secret)
#   FTPS (explicit TLS)   on 127.0.0.1:2122 (matt / secret, self-signed)
#
#   tools/e2e/servers.sh start [fixture-dir] [--exit-after <secs>]
#   tools/e2e/servers.sh stop
# --exit-after makes shells log out on their own (automatic pane close).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CORE="$ROOT/core"
export PATH="$HOME/.cargo/bin:$PATH"
unset DEVELOPER_DIR

case "${1:-}" in
  start)
    shift
    DIR="$ROOT/build/e2e"
    if [[ $# -gt 0 && "$1" != --* ]]; then DIR="$1"; shift; fi
    [[ -f "$DIR/host_key" ]] || { echo "no fixture at $DIR; run tools/e2e/make-fixture.sh first" >&2; exit 1; }
    (cd "$CORE" && cargo build -q --examples --features test-support)
    BIN="$CORE/target/debug/examples"
    pkill -f "examples/echo_server" 2>/dev/null || true
    pkill -f "examples/rdp_server" 2>/dev/null || true
    pkill -f "examples/ftp_server" 2>/dev/null || true
    mkdir -p "$DIR/ftp-root/docs"
    echo "Toolbox test FTP server" > "$DIR/ftp-root/readme.txt"
    echo "nested file" > "$DIR/ftp-root/docs/notes.txt"
    nohup "$BIN/echo_server" --key "$DIR/host_key" --port 2222 "$@" > "$DIR/echo_server.log" 2>&1 &
    nohup "$BIN/rdp_server" > "$DIR/rdp_server.log" 2>&1 &
    nohup "$BIN/ftp_server" --port 2121 --root "$DIR/ftp-root" > "$DIR/ftp_server.log" 2>&1 &
    nohup "$BIN/ftp_server" --port 2122 --root "$DIR/ftp-root" --tls > "$DIR/ftps_server.log" 2>&1 &
    up() { nc -z -w 1 127.0.0.1 "$1" 2>/dev/null && echo up || echo DOWN; }
    for _ in $(seq 1 30); do
      [[ "$(up 2222)$(up 3390)$(up 2121)$(up 2122)" == "upupupup" ]] && break
      sleep 1
    done
    echo "ssh echo: $(up 2222)   rdp: $(up 3390)   ftp: $(up 2121)   ftps: $(up 2122)"
    ;;
  stop)
    pkill -f "examples/echo_server" 2>/dev/null || true
    pkill -f "examples/rdp_server" 2>/dev/null || true
    pkill -f "examples/ftp_server" 2>/dev/null || true
    echo "test servers stopped"
    ;;
  *)
    echo "usage: $0 start [fixture-dir] [--exit-after <secs>] | stop" >&2
    exit 2
    ;;
esac
