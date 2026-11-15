#!/usr/bin/env bash
# POSIX twin of build.ps1 (ADR-0014): builds PRoot 5.1.107.90 (unbundled loader) + libtalloc + libandroid-shmem from
# the pinned sources in native/proot/sources.lock with the pinned NDK, for arm64-v8a / x86_64, as drop-ins for the
# DSHBox jniLibs. Output: artifacts/native/proot/<abi>/{libproot.so,libproot-loader.so,libtalloc.so,libandroid-shmem.so}
# + artifacts/native/proot/SHA256SUMS. Same flags and checks as build.ps1; keep the two in step.
#   PROOT_SOURCE_CACHE=<dir>  archive cache (default native/.generated/proot/src-cache)
#   PROOT_OFFLINE=1           never download
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
ndk_version=27.0.12077973
api=24   # termux-packages' target API (parity with the DSHBox binaries)
# Git Bash / MSYS would rewrite these guest-side absolute paths into Windows paths.
export MSYS2_ARG_CONV_EXCL='-DPROOT_UNBUNDLE_LOADER=;-D_PATH_TMP='
prop() {
  local v=""
  [[ -f "$repo/native/local.properties" ]] && v="$(sed -n "s/^$1=//p" "$repo/native/local.properties" | head -1)"
  local bs='\'
  v="${v//${bs}:/:}"; printf '%s' "${v//${bs}${bs}/${bs}}"
}
ndk="$(prop ndk.dir)"; ndk="${ndk:-${ANDROID_NDK_HOME:-}}"
if [[ -z "$ndk" ]]; then sdk="$(prop sdk.dir)"; sdk="${sdk:-${ANDROID_HOME:-}}"; [[ -n "$sdk" ]] && ndk="$sdk/ndk/$ndk_version"; fi
[[ -n "$ndk" && -d "$ndk" ]] || { echo "NDK $ndk_version not found (ndk.dir / ANDROID_NDK_HOME / sdk.dir)" >&2; exit 1; }
case "$(uname -s)" in Linux) tag=linux-x86_64; exe= ;; Darwin) tag=darwin-x86_64; exe= ;; *) tag=windows-x86_64; exe=.exe ;; esac
bin="$ndk/toolchains/llvm/prebuilt/$tag/bin"
clang="$bin/clang$exe"; readelf="$bin/llvm-readelf$exe"; nm="$bin/llvm-nm$exe"; strip="$bin/llvm-strip$exe"
for t in "$clang" "$readelf" "$nm" "$strip"; do [[ -x "$t" ]] || { echo "missing NDK tool: $t" >&2; exit 1; }; done
die() { echo "$*" >&2; exit 1; }
sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
quiet() { local log; log="$(mktemp)"; if ! "$@" >"$log" 2>&1; then cat "$log" >&2; rm -f "$log"; die "failed: $*"; fi; rm -f "$log"; }

# ---------------------------------------------------------------- sources (pinned URL + SHA-256)
work="$repo/native/.generated/proot"
cache="${PROOT_SOURCE_CACHE:-$work/src-cache}"
mkdir -p "$cache"
declare -A src
while IFS='|' read -r name version urls sha top license; do
  [[ -z "$name" || "$name" == \#* ]] && continue
  read -r -a url_list <<< "$urls"
  case "${url_list[0]}" in *.zip) ext=.zip ;; *) ext=.tar.gz ;; esac
  archive="$cache/$name-$version$ext"
  if [[ ! -f "$archive" ]]; then
    [[ "${PROOT_OFFLINE:-}" == 1 ]] && die "$archive missing and PROOT_OFFLINE=1"
    # Upstream first, then the mirrors (sources.lock); the SHA-256 check below applies to whichever answered.
    got=0
    for url in "${url_list[@]}"; do
      echo "download $url"
      if curl -fsSL --retry 3 -o "$archive.part" "$url"; then got=1; break; fi
    done
    [[ $got == 1 ]] || die "$name $version: no source URL answered ($urls)"
    mv -f "$archive.part" "$archive"
  fi
  actual="$(sha256 "$archive")"
  [[ "$actual" == "$sha" ]] || die "$name $version: sha256 $actual != pinned $sha ($archive)"
  dest="$work/src/$name"; rm -rf "$dest"; mkdir -p "$dest"
  if [[ $ext == .zip ]]; then unzip -q "$archive" -d "$dest"; else tar -xzf "$archive" -C "$dest"; fi
  [[ -d "$dest/$top" ]] || die "$name: $top not found in $archive"
  src[$name]="$dest/$top"
  echo "$name $version  sha256 ok  ($license)"
done < "$here/sources.lock"
talloc_src="${src[talloc]}"; shmem_src="${src[libandroid-shmem]}"; proot_src="${src[proot]}/src"
[[ ! -e "$talloc_src/config.h" ]] || die "talloc tree unexpectedly has a config.h; compat/talloc/config.h would be shadowed"

ld_shared=(-s -Wl,--as-needed -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 -Wl,-z,relro -Wl,-z,now
           -Wl,-z,noexecstack -Wl,--build-id=sha1)
out_root="$repo/artifacts/native/proot"
sums=()
leaks=("$repo" "$work")
if command -v cygpath >/dev/null; then leaks+=("$(cygpath -m "$repo")" "$(cygpath -w "$repo")"); fi
shmem_want="libandroid_shmat,libandroid_shmat_fd,libandroid_shmctl,libandroid_shmdt,libandroid_shmdt_fd,libandroid_shmget,shmat,shmctl,shmdt,shmget"

for pair in arm64-v8a:aarch64-linux-android x86_64:x86_64-linux-android; do
  abi="${pair%%:*}"; target="--target=${pair#*:}$api"
  out="$out_root/$abi"; obj="$work/obj/$abi"
  rm -rf "$out" "$obj"; mkdir -p "$out" "$obj"

  # libtalloc.so (talloc.c only, like the waf build)
  (cd "$talloc_src" && quiet "$clang" "$target" -O2 -fPIC -shared -fvisibility=hidden -fstack-protector-strong \
      -D_GNU_SOURCE -D__STDC_WANT_LIB_EXT1__=1 -I"$here/compat/talloc" -I. -Ilib/replace \
      -Wl,-soname,libtalloc.so "${ld_shared[@]}" -o "$out/libtalloc.so" talloc.c)

  # libandroid-shmem.so (upstream Makefile flags; -include / _PATH_TMP stand in for the Termux NDK sysroot patches)
  (cd "$shmem_src" && quiet "$clang" "$target" -O2 -fpic -shared -std=c11 -Wall -Wextra -fstack-protector-strong \
      -include fcntl.h -include sys/ioctl.h '-D_PATH_TMP="/data/data/com.termux/files/usr/tmp/"' \
      -Wl,--version-script=exports.txt -Wl,-soname,libandroid-shmem.so "${ld_shared[@]}" \
      -o "$out/libandroid-shmem.so" shmem.c -llog -landroid)

  # PRoot (GNUmakefile logic, PROOT_UNBUNDLE_LOADER)
  cd "$proot_src"
  arch_defs="$("$clang" "$target" -E -dM -DNO_LIBC_HEADER arch.h)"
  arch_def() { printf '%s\n' "$arch_defs" | awk -v n="$1" '$1=="#define" && $2==n { $1=""; $2=""; sub(/^  /,""); print; exit }'; }
  loader_address="$(arch_def LOADER_ADDRESS)"; [[ -n "$loader_address" ]] || die "$abi: LOADER_ADDRESS not defined by arch.h"
  read -r -a loader_arch <<< "$(arch_def LOADER_ARCH_CFLAGS)"
  cpp=(-D_FILE_OFFSET_BITS=64 -D_GNU_SOURCE -I"$obj" -I. -DARG_MAX=131072 '-DVERSION="5.1.107.90"')
  cflags=(-Wall -Wextra -O2)

  # loader: static, freestanding, at LOADER_ADDRESS; no loader32 (DSHBox ships none)
  quiet "$clang" "$target" "${cpp[@]}" "${cflags[@]}" -fPIC -ffreestanding ${loader_arch[@]+"${loader_arch[@]}"} -c loader/loader.c -o "$obj/loader.o"
  quiet "$clang" "$target" "${cpp[@]}" "${cflags[@]}" -fPIC -ffreestanding ${loader_arch[@]+"${loader_arch[@]}"} -c loader/assembly.S -o "$obj/assembly.o"
  quiet "$clang" "$target" -o "$obj/loader" "$obj/loader.o" "$obj/assembly.o" -static -nostdlib \
    -Wl,--build-id=none,-Ttext="$loader_address",--rosegment,-z,noexecstack -Wl,-z,max-page-size=16384
  quiet "$strip" --strip-all -o "$out/libproot-loader.so" "$obj/loader"

  # build.h: the GNUmakefile's feature probes
  {
    echo "/* This file is auto-generated, edit at your own risk.  */"; echo "#ifndef BUILD_H"; echo "#define BUILD_H"
    for f in process_vm seccomp_filter; do
      if "$clang" "$target" "${cpp[@]}" "${cflags[@]}" -o "$obj/.check_$f" ".check_$f.c" >/dev/null 2>&1; then
        echo "#define HAVE_$(echo "$f" | tr a-z A-Z)"
      fi
    done
    echo "#endif /* BUILD_H */"
  } > "$obj/build.h"
  grep -qx '#define HAVE_PROCESS_VM' "$obj/build.h" && grep -qx '#define HAVE_SECCOMP_FILTER' "$obj/build.h" \
    || die "$abi: feature probes failed; the DSHBox binary has both"

  objects=()
  while read -r o; do objects+=("$o"); done < <(tr -d '\r' < GNUmakefile | sed -nE 's/^[[:space:]]*([A-Za-z0-9_/.-]+\.o)[[:space:]]*\\?[[:space:]]*$/\1/p' | grep -v -e '^loader/' -e '^\.check')
  (( ${#objects[@]} >= 60 )) || die "$abi: only ${#objects[@]} objects parsed from GNUmakefile"
  if printf '%s\n' "$arch_defs" | grep -qE '^#define HAS_POKEDATA_WORKAROUND( |$)'; then
    # = readelf -s loader | awk -f loader/loader-info.awk
    pw="$("$readelf" -sW "$obj/loader" | awk '$8=="pokedata_workaround"{print $2}')"
    st="$("$readelf" -sW "$obj/loader" | awk '$8=="_start"{print $2}')"
    [[ -n "$pw" && -n "$st" ]] || die "$abi: pokedata_workaround / _start not found in the loader"
    printf '#include <unistd.h>\nconst ssize_t offset_to_pokedata_workaround=%d;\n' "$(( 16#$pw - 16#$st ))" > "$obj/loader-info.c"
    objects+=(loader/loader-info.o)
  fi
  mkdir -p "$obj/shm-include/sys"; cp "$shmem_src/shm.h" "$obj/shm-include/sys/shm.h"
  objfiles=()
  for o in "${objects[@]}"; do
    file="${o%.o}.c"; [[ "$o" == loader/loader-info.o ]] && file="$obj/loader-info.c"
    extra=(); [[ "$o" == extension/ashmem_memfd/* ]] && extra=(-include string.h)
    dst="$obj/${o//\//_}"
    quiet "$clang" "$target" "${cpp[@]}" "${cflags[@]}" -fPIE -fstack-protector-strong -DWITH_LIBANDROID_SHMEM \
      '-DPROOT_UNBUNDLE_LOADER="/data/data/com.termux/files/usr/libexec/proot"' \
      -include "$here/compat/termux-parity.h" -I"$obj/shm-include" -I"$talloc_src" ${extra[@]+"${extra[@]}"} -c "$file" -o "$dst"
    objfiles+=("$dst")
  done
  quiet "$clang" "$target" -pie -o "$out/libproot.so" "${objfiles[@]}" -L"$out" -ltalloc -landroid-shmem \
    '-Wl,-rpath,$ORIGIN' "${ld_shared[@]}"
  cd "$repo"

  # checks (drop-in for the DSHBox jniLibs)
  needed() { "$readelf" -dW "$1" | sed -n 's/.*(NEEDED).*\[\(.*\)\]/\1/p' | paste -sd, -; }
  for f in libproot.so libproot-loader.so libtalloc.so libandroid-shmem.so; do
    p="$out/$f"
    "$readelf" -lW "$p" | awk -v f="$abi/$f" '$1=="LOAD"{ n++; a=$NF; sub(/^0x/,"",a); if (length(a) < 4 || (length(a) == 4 && a < "4000")) { print f ": LOAD aligned below 16 KB: " $0; bad=1 } } END { exit (bad || n==0) }' \
      || die "$abi/$f: alignment check failed"
    for leak in "${leaks[@]}"; do ! grep -qaF "$leak" "$p" || die "$abi/$f: contains the build path $leak"; done
    case $f in
      libproot.so) want=libtalloc.so,libandroid-shmem.so,libc.so ;;
      libtalloc.so) want=libc.so ;;
      libandroid-shmem.so) want=liblog.so,libc.so ;;
      *) want= ;;
    esac
    [[ "$(needed "$p")" == "$want" ]] || die "$abi/$f: NEEDED [$(needed "$p")] != [$want]"
    sums+=("$(sha256 "$p")  $abi/$f")
  done
  "$readelf" -dW "$out/libproot.so" | grep -q 'Library runpath: \[\$ORIGIN\]' || die "$abi/libproot.so: RUNPATH is not \$ORIGIN"
  "$readelf" -dW "$out/libproot.so" | grep -E 'FLAGS_1' | grep -q PIE || die "$abi/libproot.so: not a PIE executable"
  "$readelf" -hW "$out/libproot-loader.so" | grep -qE "Entry point address:[[:space:]]+$loader_address\$" || die "$abi: loader entry != $loader_address"
  ! "$readelf" -dW "$out/libproot-loader.so" | grep -q NEEDED || die "$abi: loader is not static"
  got="$("$nm" -D --defined-only "$out/libandroid-shmem.so" | awk '{print $NF}' | LC_ALL=C sort | paste -sd, -)"
  [[ "$got" == "$shmem_want" ]] || die "$abi/libandroid-shmem.so: exports [$got] != [$shmem_want]"
  talloc_defs="$("$nm" -D --defined-only "$out/libtalloc.so" | awk '{print $NF}')"
  ! grep -qx talloc_lib_init <<< "$talloc_defs" || die "$abi/libtalloc.so: exports talloc_lib_init (internal constructor)"
  missing="$("$nm" -D --undefined-only "$out/libproot.so" | awk '{print $NF}' | grep -v '@' | grep -v '^_binary_licenses_' \
    | grep -vxF -f <(printf '%s\n' "$talloc_defs"; tr ',' '\n' <<< "$shmem_want") || true)"
  [[ -z "$missing" ]] || die "$abi/libproot.so: unresolved non-libc imports: $missing"
  for want in 5.1.107.90 /data/data/com.termux/files/usr/libexec/proot/loader /data/data/com.termux/files/usr/tmp/; do
    grep -qaF "$want" "$out/libproot.so" || die "$abi/libproot.so: missing string '$want'"
  done
  echo "proot [$abi] loader @ $loader_address, NEEDED / exports / 16 KB alignment / RUNPATH ok"
done
printf '%s\n' "${sums[@]}" > "$out_root/SHA256SUMS"
cat "$out_root/SHA256SUMS"
