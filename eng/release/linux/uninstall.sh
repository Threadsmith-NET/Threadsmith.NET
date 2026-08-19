#!/bin/sh
set -eu
prefix=${THREADSMITH_INSTALL_PREFIX:-/opt/threadsmith}
bin_dir=${THREADSMITH_BIN_DIR:-/usr/local/bin}
marker="$prefix/.threadsmith-install-root"
[ ! -L "$prefix" ] && [ "$(cat "$marker" 2>/dev/null || true)" = 'net.threadsmith.cli' ] || { echo "Install root is not owned by Threadsmith" >&2; exit 1; }
[ "$(readlink "$bin_dir/threadsmith" 2>/dev/null || true)" = "$prefix/Threadsmith.App" ] && rm -f -- "$bin_dir/threadsmith"
rm -rf -- "$prefix"
