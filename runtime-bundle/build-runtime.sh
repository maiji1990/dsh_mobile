#!/usr/bin/env bash
# Builds the runtime layers from pinned inputs (ADR-0020). Linux only; needs sudo (mmdebstrap --mode=root, root-owned
# rootfs), mmdebstrap, debian-archive-keyring, curl, zstd, zip, python3. On a non-arm64 host the arm64 steps run through
# qemu-user binfmt (check: `arch-test arm64`).
#
#   bash runtime-bundle/build-runtime.sh [--layers base,node,android-side,dsh] [--proot DIR] [--out DIR] [--work DIR]
#
# --work must be on a Linux filesystem (the rootfs keeps owners, device nodes and symlinks); under WSL use e.g. ~/dshm-rt.
#
# Inputs:  runtime-bundle/runtime-build.lock, runtime-bundle/dsh/{package.json,package-lock.json},
#          the NDK PRoot build (default artifacts/native/proot/arm64-v8a, from native/build-native.*).
# Outputs (default artifacts/runtime-build/dist):
#   <BUNDLE_NAME>-<BUNDLE_VERSION>.zip   base / node / android-side .tar.zst (+ .sha256) and runtime-profile.json
#   <DSH_VERSION>.tar.zst (+ .sha256)    the DSH layer
#   SHA256SUMS
# Archives are reproducible for the same inputs: sorted entries, mtimes = SOURCE_DATE_EPOCH (the Debian snapshot time),
# zstd single-threaded, zip with fixed timestamps.
#
# Ports DSHBox v1.5.1 (7c9d33c, GPL-3.0-or-later) tools/build_arm64_runtime_bundle.sh, runtime-bundle/build_{base,node,
# android_side}.sh, tools/pack_runtime.sh and runtime-bundle/scripts/gen_profile.sh (registered in docs/UPSTREAMS.md).
# Changes: mmdebstrap from a snapshot instead of debootstrap from a live mirror, reproducible packing, our own PRoot,
# the DSH layer built here, `.dshmobile/env.d` and the `dshmobile-runtime` bundle name (ADR-0019 / ADR-0020).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
layers="base,node,android-side,dsh"
proot_dir="$root/artifacts/native/proot/arm64-v8a"
out="$root/artifacts/runtime-build/dist"
work="$root/artifacts/runtime-build/work"
while [ $# -gt 0 ]; do
  case "$1" in
    --layers) layers="$2"; shift 2 ;;
    --proot) proot_dir="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --work) work="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
cache="$work/../cache"

die() { echo "build-runtime: $*" >&2; exit 1; }
want() { case ",$layers," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }

# ---------------------------------------------------------------- pinned inputs (KEY=VALUE, never eval'd)
declare -A L
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%$'\r'}"
  case "$line" in ''|'#'*) continue ;; esac
  L["${line%%=*}"]="${line#*=}"
done < "$here/runtime-build.lock"
for k in BUNDLE_NAME BUNDLE_VERSION ARCH DEBIAN_SUITE DEBIAN_SNAPSHOT DEBIAN_PACKAGES GUEST_APT_SOURCE NODE_VERSION \
         NODE_SHA256 DSH_VERSION DSH_PACKAGE_JSON_SHA256 DSH_PACKAGE_LOCK_SHA256 ZSTD_LEVEL; do
  [ -n "${L[$k]:-}" ] || die "runtime-build.lock: missing $k"
done
[ "${L[ARCH]}" = arm64 ] || die "only arm64 is supported"
snap="${L[DEBIAN_SNAPSHOT]}"
[[ "$snap" =~ ^[0-9]{8}T[0-9]{6}Z$ ]] || die "bad DEBIAN_SNAPSHOT: $snap"
export SOURCE_DATE_EPOCH
SOURCE_DATE_EPOCH="$(date -u -d "${snap:0:4}-${snap:4:2}-${snap:6:2} ${snap:9:2}:${snap:11:2}:${snap:13:2}" +%s)"
export TZ=UTC LC_ALL=C.UTF-8

for tool in curl zstd zip python3 sha256sum tar; do command -v "$tool" >/dev/null || die "missing $tool"; done
mkdir -p "$out" "$cache"
sudo rm -rf "$work"
mkdir -p "$work"

# ---------------------------------------------------------------- helpers
# pack <layer-dir> <out-file> [flatten-owner]: reproducible tar.zst of the directory's contents ("./…" entries).
pack() {
  local dir="$1" file="$2" owner=()
  [ "${3:-}" = flatten ] && owner=(--owner=0 --group=0)
  sudo tar -C "$dir" --format=gnu --sort=name --mtime="@$SOURCE_DATE_EPOCH" --numeric-owner "${owner[@]}" \
      -cf - . \
    | zstd -q -T1 "-${L[ZSTD_LEVEL]}" -o "$file" -f
  local sha
  sha="$(sha256sum "$file" | cut -d' ' -f1)"
  printf '%s  %s\n' "$sha" "$(basename "$file")" > "$file.sha256"
  echo "packed $(basename "$file"): $(stat -c%s "$file") bytes, sha256 $sha"
}

# env.d fragment of a layer (same placeholders as DSHBox's; ProotCommandBuilder fills them in).
env_fragment() {
  local dir="$1" name="$2" body="$3"
  sudo mkdir -p "$dir/.dshmobile/env.d"
  printf '%s' "$body" | sudo tee "$dir/.dshmobile/env.d/$name.sh" >/dev/null
}

bundle_dir="$work/bundle"
mkdir -p "$bundle_dir"

# ---------------------------------------------------------------- L0 base
if want base; then
  command -v mmdebstrap >/dev/null || die "missing mmdebstrap"
  base="$work/base"
  mirror="https://snapshot.debian.org/archive/debian/$snap"
  # Explicit: on a non-Debian host (Ubuntu runners, WSL) apt would not trust the Debian archive keys otherwise.
  keyring=/usr/share/keyrings/debian-archive-keyring.gpg
  [ -f "$keyring" ] || die "missing $keyring (install debian-archive-keyring)"
  echo "==> base: mmdebstrap ${L[DEBIAN_SUITE]} arm64 @ $snap"
  sudo env SOURCE_DATE_EPOCH="$SOURCE_DATE_EPOCH" mmdebstrap --mode=root --variant=minbase --architectures=arm64 \
    --include="${L[DEBIAN_PACKAGES]}" \
    --aptopt='Acquire::Check-Valid-Until "false"' --aptopt='Acquire::Retries "5"' \
    "${L[DEBIAN_SUITE]}" "$base" "deb [check-valid-until=no signed-by=$keyring] $mirror ${L[DEBIAN_SUITE]} main"     || die "base: mmdebstrap failed"
  # Build-host leftovers: no host name (privacy, reproducibility), no device nodes (Android cannot extract them; PRoot
  # binds /dev), no mmdebstrap apt config.
  echo localhost | sudo tee "$base/etc/hostname" >/dev/null
  sudo find "$base/dev" -mindepth 1 -delete
  # …but the same /dev skeleton DSHBox's base had (dirs and /proc symlinks, no device nodes).
  sudo mkdir -p "$base/dev/pts" "$base/dev/shm"
  sudo ln -s pts/ptmx "$base/dev/ptmx"
  sudo ln -s /proc/self/fd "$base/dev/fd"
  sudo ln -s /proc/self/fd/0 "$base/dev/stdin"
  sudo ln -s /proc/self/fd/1 "$base/dev/stdout"
  sudo ln -s /proc/self/fd/2 "$base/dev/stderr"
  sudo rm -f "$base/etc/apt/apt.conf.d/99mmdebstrap"
  # DSHBox build_base.sh customizations.
  printf '%s\n' "${L[GUEST_APT_SOURCE]}" | sudo tee "$base/etc/apt/sources.list" >/dev/null
  sudo rm -f "$base"/etc/apt/sources.list.d/*
  printf 'nameserver 114.114.114.114\nnameserver 8.8.8.8\nnameserver 223.5.5.5\n' | sudo tee "$base/etc/resolv.conf" >/dev/null
  sudo find "$base/usr/share/locale" -mindepth 1 -maxdepth 1 -type d \
    ! -name C ! -name C.UTF-8 ! -name zh_CN ! -name zh_CN.utf8 -exec rm -rf {} +
  if [ -d "$base/usr/share/doc" ]; then
    sudo find "$base/usr/share/doc" -type f ! -name copyright -delete
    sudo find "$base/usr/share/doc" -mindepth 1 -type d -empty -delete
  fi
  sudo rm -rf "$base/usr/share/man"
  sudo rm -rf "$base"/var/lib/apt/lists/* "$base"/var/cache/apt/*.bin "$base"/var/cache/apt/archives/*.deb
  env_fragment "$base" base '# L0 base (Debian guest env)
export HOME="@HOME@"
export TERM="@TERM@"
export LANG="C.UTF-8"
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
export DSH_PERMISSION_MODE="@DSH_PERMISSION_MODE@"
'
  [ -f "$base/etc/debian_version" ] || die "base: no /etc/debian_version"
  pack "$base" "$bundle_dir/base.tar.zst"
fi

# ---------------------------------------------------------------- L1 node
node="$work/node"
if want node || want dsh; then
  tarball="$cache/node-v${L[NODE_VERSION]}-linux-arm64.tar.xz"
  if [ ! -f "$tarball" ] || [ "$(sha256sum "$tarball" | cut -d' ' -f1)" != "${L[NODE_SHA256]}" ]; then
    echo "==> node: downloading ${L[NODE_VERSION]}"
    curl -fsSL --retry 3 -o "$tarball" "https://nodejs.org/dist/v${L[NODE_VERSION]}/node-v${L[NODE_VERSION]}-linux-arm64.tar.xz"
  fi
  [ "$(sha256sum "$tarball" | cut -d' ' -f1)" = "${L[NODE_SHA256]}" ] || die "node tarball SHA-256 mismatch"
  mkdir -p "$node"
  tar -xJf "$tarball" -C "$node" --strip-components=1
  env_fragment "$node" node '# L1 node (Node runtime is bound at /usr/local in the guest)
export NODE_BIN="@NODE_BIN@"
'
fi
if want node; then
  pack "$node" "$bundle_dir/node.tar.zst" flatten
fi

# ---------------------------------------------------------------- L3 android-side (this repo's NDK PRoot, ADR-0014)
if want android-side; then
  for f in libproot.so libproot-loader.so libtalloc.so libandroid-shmem.so; do
    [ -f "$proot_dir/$f" ] || die "android-side: missing $proot_dir/$f (run native/build-native.*)"
  done
  aside="$work/android-side"
  mkdir -p "$aside/bin" "$aside/lib" "$aside/libexec/proot"
  install -m 755 "$proot_dir/libproot.so" "$aside/bin/proot"
  install -m 755 "$proot_dir/libproot-loader.so" "$aside/libexec/proot/loader"
  install -m 644 "$proot_dir/libtalloc.so" "$proot_dir/libandroid-shmem.so" "$aside/lib/"
  env_fragment "$aside" android-side '# L3 android-side (host-side proot env)
export LD_LIBRARY_PATH="@PROOT_LIB@"
export PROOT_LOADER="@PROOT_LOADER@"
export PROOT_TMP_DIR="@PROOT_TMP_DIR@"
'
  pack "$aside" "$bundle_dir/android-side.tar.zst" flatten
fi

# ---------------------------------------------------------------- runtime-profile.json + bundle zip
if want base && want node && want android-side; then
  python3 - "$bundle_dir" "${L[BUNDLE_NAME]}" "${L[BUNDLE_VERSION]}" "${L[ZSTD_LEVEL]}" "${L[NODE_VERSION]}" <<'PY'
import datetime, json, os, sys
d, name, version, level, node = sys.argv[1:6]
epoch = int(os.environ["SOURCE_DATE_EPOCH"])
def layer(n, v, deps):
    f = f"{n}.tar.zst"
    sha = open(os.path.join(d, f + ".sha256")).read().split()[0]
    return {"name": n, "version": v, "compression": "zstd", "sha256": sha,
            "size_bytes": os.path.getsize(os.path.join(d, f)), "env_file": f".dshmobile/env.d/{n}.sh",
            "file": f, "deps": deps}
profile = {
    "bundle": {"kind": "runtime", "name": name, "version": version, "arch": "arm64", "compression": "zstd",
               "zstd_level": int(level),
               "built_at": datetime.datetime.fromtimestamp(epoch, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")},
    "layers": [layer("base", version, []), layer("node", node, ["base"]), layer("android-side", version, ["base"])],
    "assembly": ["base", "node", "android-side"],
}
with open(os.path.join(d, "runtime-profile.json"), "w", newline="\n") as fh:
    json.dump(profile, fh, indent=2)
    fh.write("\n")
PY
  zipfile="$out/${L[BUNDLE_NAME]}-${L[BUNDLE_VERSION]}.zip"
  rm -f "$zipfile"
  (cd "$bundle_dir"
   touch -d "@$SOURCE_DATE_EPOCH" ./*
   # Layers are already zstd: store them (-0). -X: no extra attributes, -D: no directory entries.
   zip -q -X -D -0 "$zipfile" base.tar.zst base.tar.zst.sha256 node.tar.zst node.tar.zst.sha256 \
     android-side.tar.zst android-side.tar.zst.sha256 runtime-profile.json)
  echo "bundle: $zipfile"
fi

# ---------------------------------------------------------------- DSH layer (npm ci under arm64 Node)
if want dsh; then
  dsh="$work/dsh"
  mkdir -p "$dsh"
  cp "$here/dsh/package.json" "$here/dsh/package-lock.json" "$dsh/"
  [ "$(sha256sum "$dsh/package.json" | cut -d' ' -f1)" = "${L[DSH_PACKAGE_JSON_SHA256]}" ] || die "dsh/package.json SHA-256 mismatch"
  [ "$(sha256sum "$dsh/package-lock.json" | cut -d' ' -f1)" = "${L[DSH_PACKAGE_LOCK_SHA256]}" ] || die "dsh/package-lock.json SHA-256 mismatch"
  echo "==> dsh: npm ci (node ${L[NODE_VERSION]} arm64)"
  if [ "$(uname -m)" != aarch64 ]; then
    # qemu-user needs an arm64 glibc for the dynamically linked node: the base rootfs just built, else the cross libc.
    if [ -e "$work/base/lib/ld-linux-aarch64.so.1" ]; then export QEMU_LD_PREFIX="$work/base"
    elif [ -e /usr/aarch64-linux-gnu/lib/ld-linux-aarch64.so.1 ]; then export QEMU_LD_PREFIX=/usr/aarch64-linux-gnu
    else die "dsh: no arm64 glibc for qemu (build base first, or install libc6-arm64-cross)"; fi
  fi
  (cd "$dsh"
   PATH="$node/bin:$PATH" npm_config_cache="$cache/npm" "$node/bin/node" "$node/bin/npm" ci \
     --no-audit --no-fund --no-update-notifier --os=linux --cpu=arm64 --libc=glibc)
  [ -f "$dsh/node_modules/@deepseek-ai/dsh/package.json" ] || die "dsh: @deepseek-ai/dsh not installed"
  got="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$dsh/node_modules/@deepseek-ai/dsh/package.json")"
  [ "$got" = "${L[DSH_VERSION]}" ] || die "dsh: installed $got, want ${L[DSH_VERSION]}"
  pack "$dsh" "$out/${L[DSH_VERSION]}.tar.zst" flatten
fi

(cd "$out" && sha256sum -- *.zip *.tar.zst 2>/dev/null > SHA256SUMS || true)
echo "==> done: $out"
cat "$out/SHA256SUMS"
