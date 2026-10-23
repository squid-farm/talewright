#!/bin/sh
set -eu
base=$(CDPATH= cd -P "$(dirname "$0")" && pwd)
case "$(uname -s):$(uname -m)" in
    Linux:x86_64) runtime=linux-x64 ;;
    Linux:aarch64|Linux:arm64) runtime=linux-arm64 ;;
    Darwin:x86_64) runtime=osx-x64 ;;
    Darwin:arm64) runtime=osx-arm64 ;;
    *)
        printf '%s\n' "Unsupported platform. This package supports Linux/macOS x64 and ARM64; use start.cmd on Windows." >&2
        exit 1
        ;;
esac
executable="$base/$runtime/Talewright.Host.Desktop"
if [ ! -x "$executable" ]; then
    chmod u+x "$executable"
fi
exec "$executable" "$@"
