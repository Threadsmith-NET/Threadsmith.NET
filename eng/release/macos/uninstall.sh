#!/bin/sh
set -eu
pkgutil --pkg-info net.threadsmith.cli >/dev/null
[ "$(readlink /usr/local/bin/threadsmith 2>/dev/null || true)" = '/usr/local/lib/threadsmith/Threadsmith.App' ] && rm -f /usr/local/bin/threadsmith
rm -rf /usr/local/lib/threadsmith
pkgutil --forget net.threadsmith.cli >/dev/null
