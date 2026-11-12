#!/usr/bin/env bash
# POSIX twin of build.ps1: builds libdshmobile_native.so for arm64-v8a / x86_64 with the pinned NDK, 16 KB aligned,
# and checks alignment + exports. Output: artifacts/native/dshmobile-native/<abi>/libdshmobile_native.so.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
ndk_version=27.0.12077973
api=29
prop() {
  local v=""
  [[ -f "$repo/native/local.properties" ]] && v="$(sed -n "s/^$1=//p" "$repo/native/local.properties" | head -1)"
  # java.util.Properties escapes: '\:' -> ':', '\\' -> '\'
  local bs='\'
  v="${v//${bs}:/:}"; printf '%s' "${v//${bs}${bs}/${bs}}"
}
ndk="$(prop ndk.dir)"; ndk="${ndk:-${ANDROID_NDK_HOME:-}}"
if [[ -z "$ndk" ]]; then sdk="$(prop sdk.dir)"; sdk="${sdk:-${ANDROID_HOME:-}}"; [[ -n "$sdk" ]] && ndk="$sdk/ndk/$ndk_version"; fi
[[ -n "$ndk" && -d "$ndk" ]] || { echo "NDK $ndk_version not found (ndk.dir / ANDROID_NDK_HOME / sdk.dir)" >&2; exit 1; }
case "$(uname -s)" in Linux) tag=linux-x86_64; exe= ;; Darwin) tag=darwin-x86_64; exe= ;; *) tag=windows-x86_64; exe=.exe ;; esac
bin="$ndk/toolchains/llvm/prebuilt/$tag/bin"
exports="dshm_abi_version dshm_pty_spawn dshm_pty_resize dshm_read dshm_write dshm_close dshm_wait dshm_kill"
for pair in arm64-v8a:aarch64-linux-android x86_64:x86_64-linux-android; do
  abi="${pair%%:*}"; triple="${pair#*:}"
  out="$repo/artifacts/native/dshmobile-native/$abi/libdshmobile_native.so"
  mkdir -p "$(dirname "$out")"
  "$bin/clang$exe" --target="$triple$api" -std=c11 -O2 -fPIC -shared -fvisibility=hidden \
    -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 \
    -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -Wl,-z,relro -Wl,-z,now -Wl,-z,noexecstack \
    -Wl,--build-id=sha1 -Wl,-soname,libdshmobile_native.so -o "$out" "$here/src/dshm_native.c"
  "$bin/llvm-readelf$exe" -lW "$out" | awk '$1=="LOAD"{ a=$NF; sub(/^0x/,"",a); if (length(a) < 4 || (length(a) == 4 && a < "4000")) { print "LOAD aligned below 16 KB: " $0; bad=1 } n++ } END { exit (bad || n==0) }' \
    || { echo "$abi: 16 KB alignment check failed" >&2; exit 1; }
  syms="$("$bin/llvm-readelf$exe" --dyn-syms -W "$out")"
  for e in $exports; do grep -qE "[[:space:]]$e([[:space:]]|$)" <<<"$syms" || { echo "$abi: export $e missing" >&2; exit 1; }; done
  echo "libdshmobile_native.so [$abi] 16 KB aligned, $(wc -w <<<"$exports") exports"
done
