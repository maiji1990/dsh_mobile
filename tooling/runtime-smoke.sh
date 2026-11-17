#!/usr/bin/env bash
# vNext PR-03: smoke test of freshly built layers in the guest layout, on an arm64 Linux host (CI: ubuntu-24.04-arm).
#
#   bash tooling/runtime-smoke.sh <runtime bundle .zip> <dsh layer .tar.zst> <work dir> [report.json]
#
# Extracts base / node / dsh exactly where the app puts them for PRoot (base = /, node bound at /usr/local, DSH layer
# at /opt/dshapp/runtime, user data at /root/projects), chroots into the Debian rootfs and runs the app's own guest probe
# (src/DshMobile.Runtime/Guest/Assets/dsh-probe.mjs, unchanged) for every DSH profile with a real protocol exchange:
#   web, sdk, sdk-minimal, acp  must be "pass";  headless  must be "pass" or "limited" (a real job needs a credential).
# It also runs DSH's own first-use workspace resolver without / with the app's xdg-user-dir wrapper, and the Agent E2E
# driver dsh-e2e.mjs, which must report "needs-credential" here (no model credential in CI) (vNext PR-07).
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
xdg_shim="$repo/src/DshMobile.Runtime/Guest/Assets/xdg-user-dir"
e2e="$repo/src/DshMobile.Runtime/Guest/Assets/dsh-e2e.mjs"
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
sudo install -m 644 "$e2e" "$root/opt/dshmobile-cache/dshmobile/dsh-e2e.mjs"
# The app's guest wrappers dir (GuestToolchain.ShimBinDir; first on the dsh role's PATH). Only xdg-user-dir is staged:
# dsh / pnpm need the link-shim and the APK's corepack cache.
shim_bin=/root/projects/.dsh/dshmobile/bin

guest() {
  # The env the app gives the guest (ProotCommandBuilder placeholders: HOME=/root, TERM=xterm-256color, …).
  sudo chroot "$root" /usr/bin/env -i HOME=/root TERM=xterm-256color LANG=C.UTF-8 DSH_PERMISSION_MODE=danger-full-access \
    PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    /bin/sh -c 'cd /root/projects && exec "$@"' guest "$@"
}

# A successful archive/import is not proof that the official Node binary can load
# inside Debian minbase. Fail immediately on missing ELF dependencies (e.g.
# libatomic.so.1 from Debian's libatomic1 package in Node.js 26) instead of
# spending 90 seconds waiting for a DSH web server that cannot start.
if ! node_version="$(guest /usr/local/bin/node --version)"; then
  echo "runtime-smoke: Node could not start inside the guest rootfs; inspect missing ELF shared libraries and DEBIAN_PACKAGES" >&2
  exit 1
fi
echo "guest: $(guest /bin/sh -c 'cat /etc/debian_version; uname -m') node $node_version"
echo "dsh --version: $(guest /usr/local/bin/node /opt/dshapp/runtime/node_modules/@deepseek-ai/dsh/lib/bin.js --version 2>&1 | tail -n 1)"

# vNext PR-07: DSH's first-use workspace asks `xdg-user-dir DOCUMENTS` (Linux), which Debian minbase lacks. Run DSH's own
# resolver (unchanged) first without and then with the app's wrapper: without it must fail (the device symptom
# "无法创建默认工作区"), with it it must answer /root/projects/deepseek-harness/default-workspace.
resolver="$(cd "$work/dsh" && find node_modules -path '*/dsh-api-workspace-controller/lib/types/default-directory.js' -print -quit)"
[ -n "$resolver" ] || { echo "runtime-smoke: DSH layer has no dsh-api-workspace-controller default-directory.js" >&2; exit 1; }
resolve_js="import('file:///opt/dshapp/runtime/$resolver').then(m => m.defaultWorkspaceDirectory(undefined, AbortSignal.timeout(10000))).then(d => console.log('WORKSPACE ' + d), e => console.log('WORKSPACE-ERROR ' + e.message))"
without="$(guest /usr/local/bin/node -e "$resolve_js" 2>&1 | grep '^WORKSPACE' | tail -n 1 || true)"
sudo install -D -m 755 "$xdg_shim" "$root$shim_bin/xdg-user-dir"
with="$(sudo chroot "$root" /usr/bin/env -i HOME=/root LANG=C.UTF-8 PATH="$shim_bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" \
  /bin/sh -c 'cd /root/projects && exec /usr/local/bin/node -e "$1"' guest "$resolve_js" 2>&1 | grep '^WORKSPACE' | tail -n 1 || true)"
echo "default workspace without wrapper: ${without:-<none>}"
echo "default workspace with wrapper:    ${with:-<none>}"
workspace_ok=1
case "$without" in "WORKSPACE-ERROR "*) ;; *) echo "runtime-smoke: expected DSH's resolver to fail without xdg-user-dir" >&2; workspace_ok=0 ;; esac
[ "$with" = "WORKSPACE /root/projects/deepseek-harness/default-workspace" ] || { echo "runtime-smoke: default workspace wrong with the wrapper" >&2; workspace_ok=0; }

# The web probe checks the running app DSH (GET / must be 401: launch-token auth enforced), so start it the way
# ProotCommandBuilder does (role "dsh": DSH_HOME, PORT=3080, TMPDIR, SHELL; node --expose-internals bin.js --profile web).
sudo setsid chroot "$root" /usr/bin/env -i HOME=/root TERM=xterm-256color LANG=C.UTF-8 DSH_PERMISSION_MODE=danger-full-access \
  PATH="$shim_bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" DSH_HOME=/root/projects/.dsh PORT=3080 \
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
[ "$workspace_ok" = 1 ] || failed=1
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

# vNext PR-07: the Agent E2E driver against the real DSH. CI holds no model credential, so the only acceptable result
# here is "needs-credential" (exit 3) for every suite: it proves the driver reaches DSH's real model path through
# headless / sdk / acp and that a missing credential is never reported as a pass. The real pass is a device run.
e2e_line="$(guest /usr/bin/env PATH="$shim_bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin" \
  /usr/local/bin/node /opt/dshmobile-cache/dshmobile/dsh-e2e.mjs --turn-timeout-ms 180000 2>&1 | grep '^DSHE2E ' | tail -n 1 || true)"
e2e_json="${e2e_line#DSHE2E }"
e2e_summary="$(printf '%s' "$e2e_json" | python3 -c 'import json,sys; r=json.load(sys.stdin); print(r.get("status","?"), " ".join(s["suite"]+"="+s["status"] for s in r.get("suites",[])))' 2>/dev/null || echo none)"
echo "e2e (no credential): $e2e_summary"
case "$e2e_summary" in
  "needs-credential headless=needs-credential sdk=needs-credential acp=needs-credential") ;;
  *) failed=1; printf '%s\n' "$e2e_json" | python3 -m json.tool 2>/dev/null || printf '%s\n' "${e2e_json:-<no DSHE2E line>}" ;;
esac

if [ -n "$report" ]; then
  printf '{"schema":1,"defaultWorkspace":%s,"probes":[%s],"e2e":%s}\n' \
    "$(printf '%s' "${with:-}" | python3 -c 'import json,sys; print(json.dumps(sys.stdin.read()))')" "$(IFS=,; echo "${results[*]}")" "${e2e_json:-null}" > "$report"
fi
exit "$failed"
