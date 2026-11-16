#!/usr/bin/env bash
# vNext PR-03: smoke test of freshly built layers in the guest layout, on an arm64 Linux host (CI: ubuntu-24.04-arm).
#
#   bash tooling/runtime-smoke.sh <runtime bundle .zip> <dsh layer .tar.zst> <work dir> [report.json]
#
# Extracts base / node / dsh exactly where the app puts them for PRoot (base = /, node bound at /usr/local, DSH layer
# at /opt/dshapp/runtime, user data at /root/projects), chroots into the Debian rootfs and runs the app's own guest probe
# (src/DshMobile.Runtime/Guest/Assets/dsh-probe.mjs, unchanged) for every DSH profile with a real protocol exchange:
#   web, sdk, sdk-minimal, acp  must be "pass";  headless  must be "pass" or "limited" (a real job needs a credential).
# `plugin` is not run: it needs the pnpm toolchain the APK ships (toolchain-cache), so it stays a device check.
#
# This is NOT the Android environment: no PRoot, no Android kernel restrictions, no link-shim. It proves the layers are
# complete and DSH starts on this Node / glibc; device acceptance is still required (AGENTS.md rule 9).
# The web profile is also started for real (as the app starts it) so the probe's GET / -> 401 check has a server.
# Needs: sudo, unzip, zstd, tar, curl. No network access is needed by the probe; no model API is called.
set -euo pipefail

zip="$(realpath "$1")"; dsh_tar="$(realpath "$2")"; work="$3"; report="${4:-}"
here="$(cd "$(dirname "$0")" && pwd)"; repo="$(cd "$here/.." && pwd)"
probe="$repo/src/DshMobile.Runtime/Guest/Assets/dsh-probe.mjs"
[ "$(uname -m)" = aarch64 ] || { echo "runtime-smoke: needs an arm64 host (got $(uname -m))" >&2; exit 2; }

sudo rm -rf "$work"; mkdir -p "$work/zip" "$work/node" "$work/dsh"
root="$work/root"; sudo mkdir -p "$root"
cleanup() {
  sudo pkill -f 'dsh/lib/bin.js --profile web' 2>/dev/null || true; sleep 1
  for m in "$root/dev" "$root/proc" "$root/opt/dshapp/runtime" "$root/usr/local"; do
    mountpoint -q "$m" 2>/dev/null && sudo umount -R "$m" || true
  done
}
trap cleanup EXIT

unzip -q "$zip" -d "$work/zip"
zstd -dc "$work/zip/base.tar.zst" | sudo tar -C "$root" -xpf -
zstd -dc "$work/zip/node.tar.zst" | tar -C "$work/node" -xf -
zstd -dc "$dsh_tar" | tar -C "$work/dsh" -xf -
[ -x "$work/node/bin/node" ] || { echo "runtime-smoke: node layer has no bin/node" >&2; exit 1; }
[ -f "$work/dsh/node_modules/@deepseek-ai/dsh/lib/bin.js" ] || { echo "runtime-smoke: DSH layer has no lib/bin.js" >&2; exit 1; }

sudo mkdir -p "$root/usr/local" "$root/opt/dshapp/runtime" "$root/root/projects" "$root/opt/dshmobile-cache/dshmobile"
sudo mount --bind "$work/node" "$root/usr/local"
sudo mount --bind "$work/dsh" "$root/opt/dshapp/runtime"
sudo mount -t proc proc "$root/proc"
sudo rm -rf "$root/dev"; sudo mkdir -p "$root/dev"; sudo mount --rbind /dev "$root/dev"
sudo install -m 644 "$probe" "$root/opt/dshmobile-cache/dshmobile/dsh-probe.mjs"

guest() {
  # The env the app gives the guest (ProotCommandBuilder placeholders: HOME=/root, TERM=xterm-256color, …).
  sudo chroot "$root" /usr/bin/env -i HOME=/root TERM=xterm-256color LANG=C.UTF-8 DSH_PERMISSION_MODE=danger-full-access \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    /bin/sh -c 'cd /root/projects && exec "$@"' guest "$@"
}

echo "guest: $(guest /bin/sh -c 'cat /etc/debian_version; uname -m') node $(guest /usr/local/bin/node --version)"
echo "dsh --version: $(guest /usr/local/bin/node /opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js --version 2>&1 | tail -n 1)"

# The web probe checks the running app DSH (GET / must be 401: launch-token auth enforced), so start it the way
# ProotCommandBuilder does (role "dsh": DSH_HOME, PORT=3080, TMPDIR, SHELL; node --expose-internals bin.js --profile web).
sudo setsid chroot "$root" /usr/bin/env -i HOME=/root TERM=xterm-256color LANG=C.UTF-8 DSH_PERMISSION_MODE=danger-full-access \
  PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin DSH_HOME=/root/projects/.dsh PORT=3080 \
  TMPDIR=/tmp TMP=/tmp TEMP=/tmp SHELL=/bin/bash \
  /bin/sh -c 'cd /root/projects && exec /usr/local/bin/node --expose-internals /opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js --profile web' \
  > "$work/web.log" 2>&1 &
code=000
for _ in $(seq 1 90); do
  code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:3080/ || true)"
  [ "$code" != 000 ] && break
  sleep 1
done
echo "dsh web: GET / -> $code"
[ "$code" != 000 ] || { echo "runtime-smoke: DSH web did not start; log tail:" >&2; tail -n 40 "$work/web.log" >&2; }

results=(); failed=0
for profile in web headless sdk sdk-minimal acp; do
  line="$(guest /usr/local/bin/node /opt/dshmobile-cache/dshmobile/dsh-probe.mjs "$profile" 2>&1 | grep '^DSHPROBE ' | tail -n 1 || true)"
  json="${line#DSHPROBE }"
  status="$(printf '%s' "$json" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("status","?"))' 2>/dev/null || echo none)"
  case "$profile:$status" in
    headless:pass|headless:limited|*:pass) mark=ok ;;
    *) mark=FAIL; failed=1 ;;
  esac
  echo "probe $profile: $status [$mark]"
  [ "$mark" = ok ] || printf '%s\n' "$json" | python3 -m json.tool 2>/dev/null || printf '%s\n' "${json:-<no DSHPROBE line>}"
  results+=("${json:-null}")
done

if [ -n "$report" ]; then
  printf '{"schema":1,"probes":[%s]}\n' "$(IFS=,; echo "${results[*]}")" > "$report"
fi
exit "$failed"
