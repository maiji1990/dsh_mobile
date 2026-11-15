#!/usr/bin/env bash
# Builds libdshmobile-link.so (aarch64, glibc) for the guest Debian (ADR-0019).
#
# Linux only: an aarch64 host uses its own gcc; an x86_64 host needs the cross toolchain
#   sudo apt-get install gcc-aarch64-linux-gnu libc6-dev-arm64-cross binutils-aarch64-linux-gnu
# Output: artifacts/native/link-shim/libdshmobile-link.so. To ship it, copy it to
# src/DshMobile.Android/Assets/dshmobile/ and update the SHA-256 in src/DshMobile.Android/Assets/README.md and
# UpstreamPinnedValuesTests (the bytes depend on the compiler version, so the shipped binary is pinned, not rebuilt
# by native/build-native.*).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/artifacts/native/link-shim"
mkdir -p "$out"

if [ "$(uname -m)" = "aarch64" ]; then
  cc=gcc; readelf=readelf
else
  cc=aarch64-linux-gnu-gcc; readelf=aarch64-linux-gnu-readelf
fi
command -v "$cc" >/dev/null || { echo "missing $cc (see header)" >&2; exit 1; }

# -s: keep only the dynamic symbols. -ffile-prefix-map: no build paths in the binary.
"$cc" -shared -fPIC -O2 -Wall -Wextra -Werror -s -ffile-prefix-map="$here"=. \
  -Wl,--build-id=none -Wl,-soname,libdshmobile-link.so \
  -o "$out/libdshmobile-link.so" "$here/libdshmobile-link.c" -ldl

# The guest is Debian trixie (glibc 2.41): refuse a binary that needs a newer glibc symbol.
max=$("$readelf" -V --wide "$out/libdshmobile-link.so" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)
case "$("$readelf" -h "$out/libdshmobile-link.so" | grep Machine)" in *AArch64*) ;; *) echo "not AArch64" >&2; exit 1 ;; esac
if [ "$(printf '%s\n' "$max" GLIBC_2.41 | sort -V | tail -1)" != GLIBC_2.41 ]; then
  echo "needs $max (> GLIBC_2.41)" >&2; exit 1
fi
if grep -qi dshbox "$out/libdshmobile-link.so"; then echo "old name in binary" >&2; exit 1; fi

echo "compiler: $("$cc" --version | head -1)"
echo "max glibc symbol version: $max"
sha256sum "$out/libdshmobile-link.so"
