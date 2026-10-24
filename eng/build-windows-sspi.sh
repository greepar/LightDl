#!/bin/sh
# Rebuild the two tiny SSPI compatibility archives using the pinned Zig toolset.
set -eu
zig_exe=$1
output_root=$2
work_dir=$(mktemp -d)
trap 'rm -rf "$work_dir"' EXIT
for arch in x64 arm64; do
  case "$arch" in x64) triple=x86_64-windows-gnu;; arm64) triple=aarch64-windows-gnu;; esac
  "$zig_exe" cc -target "$triple" -O2 -c eng/msquic-sspi.c -o "$work_dir/sspi-$arch.o"
  mkdir -p "$output_root/win-$arch"
  # ar replaces matching members, so remove the obsolete import descriptors first.
  rm -f "$output_root/win-$arch/secur32-extra.a"
  "$zig_exe" ar rcs "$output_root/win-$arch/secur32-extra.a" "$work_dir/sspi-$arch.o"
done
