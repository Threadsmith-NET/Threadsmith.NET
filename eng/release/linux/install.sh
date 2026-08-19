#!/bin/sh
set -eu
prefix=${THREADSMITH_INSTALL_PREFIX:-/opt/threadsmith}
bin_dir=${THREADSMITH_BIN_DIR:-/usr/local/bin}
marker="$prefix/.threadsmith-install-root"
[ ! -L "$prefix" ] || { echo "Refusing symbolic-link install prefix" >&2; exit 1; }
if [ -e "$prefix" ] && [ ! -f "$marker" ] && [ -n "$(find "$prefix" -mindepth 1 -maxdepth 1 -print -quit)" ]; then echo "Install prefix is not owned by Threadsmith" >&2; exit 1; fi
mkdir -p "$prefix" "$bin_dir"
if [ -e "$bin_dir/threadsmith" ] && [ "$(readlink "$bin_dir/threadsmith" 2>/dev/null || true)" != "$prefix/Threadsmith.App" ]; then echo "Launcher is not owned by Threadsmith" >&2; exit 1; fi
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
find "$prefix" -mindepth 1 -maxdepth 1 ! -name '.threadsmith-install-root' -exec rm -rf -- {} +
cp -R "$script_dir"/. "$prefix"/
printf '%s\n' 'net.threadsmith.cli' > "$marker"
ln -sfn "$prefix/Threadsmith.App" "$bin_dir/threadsmith"
