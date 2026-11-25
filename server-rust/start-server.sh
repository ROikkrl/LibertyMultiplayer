#!/usr/bin/env bash
set -euo pipefail

cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
umask 077
export LF_BIND="${LF_BIND:-0.0.0.0}"
export LF_PORT="${LF_PORT:-24840}"
export LF_KEY_FILE="${LF_KEY_FILE:-session.key}"
export LF_LOG_FILE="${LF_LOG_FILE:-server.log}"

if [[ ! -e "$LF_KEY_FILE" ]]; then
    (set -o noclobber; od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$LF_KEY_FILE")
    printf '\n' >> "$LF_KEY_FILE"
    printf 'Created session key in %s\n' "$LF_KEY_FILE"
fi

binary=./bin/liberty-friends-server
if [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 && -f "$binary" ]]; then
    chmod u+x "$binary"
    exec "$binary"
fi

if ! command -v cargo >/dev/null 2>&1; then
    echo "Install Cargo first: sudo apt update && sudo apt install cargo" >&2
    exit 1
fi
cargo build --release --locked
exec ./target/release/liberty-friends-server
