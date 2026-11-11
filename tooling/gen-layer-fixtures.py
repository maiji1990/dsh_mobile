#!/usr/bin/env python3
"""Generates the deterministic layer-archive fixtures in tests/TestVectors/layers/ (PR-02 LayerInstaller parity).

The archives are extracted by BOTH the legacy DSHBox BundleManager.extractTarGz (native-bridge
LegacyLayerExtractParityTest) and the C# DshMobile.Runtime.Layers.LayerExtractor (LayerExtractParityTests);
expected outcomes live in tests/TestVectors/layer-extract.json. Re-run only when adding cases:

    python -I tooling/gen-layer-fixtures.py

Output is byte-stable (fixed mtime/uid/gid/uname, gzip mtime 0) so regenerating does not churn git.
"""
import bz2
import gzip
import io
import lzma
import os
import sys
import tarfile

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "TestVectors", "layers")


def info(name, type_=tarfile.REGTYPE, mode=0o644, data=b"", link=""):
    ti = tarfile.TarInfo(name)
    ti.type = type_
    ti.mode = mode
    ti.mtime = 0
    ti.uid = ti.gid = 0
    ti.uname = ti.gname = "root"
    ti.linkname = link
    ti.size = len(data) if type_ in (tarfile.REGTYPE, tarfile.AREGTYPE) else 0
    return ti, (io.BytesIO(data) if ti.size else None)


def tar_bytes(entries, fmt=tarfile.PAX_FORMAT):
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=fmt) as tar:
        for ti, data in entries:
            tar.addfile(ti, data)
    return buf.getvalue()


def write(name, data):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)


def gz(data):
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0, filename="") as g:
        g.write(data)
    return buf.getvalue()


def main():
    os.makedirs(OUT, exist_ok=True)
    D, F, S, H = tarfile.DIRTYPE, tarfile.REGTYPE, tarfile.SYMTYPE, tarfile.LNKTYPE

    basic = tar_bytes([
        info("./", D, 0o755),
        info("./etc/", D, 0o755),
        info("./etc/debian_version", F, 0o644, b"13.1\n"),
        info("./root/", D, 0o700),
        info("./root/secret.yaml", F, 0o600, b"token: x\n"),
        info("usr/bin/tool", F, 0o755, b"#!/bin/sh\necho tool\n"),        # implicit parents usr/, usr/bin/
        info("/opt/abs-name.txt", F, 0o444, b"leading slash stripped\n"),
        info("var/empty/", F, 0o755),                                       # type '0' but trailing '/' -> directory
        info("x/" + "long-component-" * 8 + "end.txt", F, 0o640, b"long name via pax\n"),
    ])
    write("basic.tar", basic)
    write("basic.tar.gz", gz(basic))

    write("hardlinks.tar", tar_bytes([
        info("usr/bin/perl5.40.1", F, 0o755, b"perl-binary\n"),
        info("usr/bin/perl", H, 0o755, link="/usr/bin/perl5.40.1"),          # absolute form
        info("usr/bin/perl-dot", H, 0o755, link="./usr/bin/perl5.40.1"),     # ./ form
        info("usr/bin/early", H, 0o700, link="usr/bin/late"),                # target appears later -> deferred
        info("usr/bin/late", F, 0o644, b"late-target\n"),
        info("usr/bin/dangling", H, 0o644, link="usr/bin/missing"),          # never resolved -> warning, skipped
    ]))

    write("devices.tar", tar_bytes([
        info("dev/", D, 0o755),
        info("dev/null", tarfile.CHRTYPE, 0o666),                            # legacy: becomes an empty regular file
        info("dev/loop0", tarfile.BLKTYPE, 0o660),
        info("run/initctl", tarfile.FIFOTYPE, 0o600),
    ]))

    write("symlinks.tar", tar_bytes([
        info("usr/lib/libfoo.so.1", F, 0o644, b"elf\n"),
        info("usr/lib/libfoo.so", S, 0o777, link="libfoo.so.1"),
        info("bin", S, 0o777, link="usr/bin"),                                # relative, target dir appears later
        info("usr/bin/", D, 0o755),
        info("etc/alternatives/editor", S, 0o777, link="/usr/bin/vim"),     # absolute (root-relative), dangling
        info("var/run", S, 0o777, link="../run"),                            # relative with ..
    ]))

    # Writing through an extracted symlink: legacy canonicalises the path through the link. With an absolute link the
    # canonical path leaves the root (the link is created literally, i.e. pointing at the HOST /etc) -> unsafe.
    write("through-abs-symlink.tar", tar_bytes([
        info("hostetc", S, 0o777, link="/etc"),
        info("hostetc/passwd", F, 0o644, b"pwned\n"),
    ]))
    # Debian /etc/ssl/certs: hash link -> name.pem (relative) -> /usr/share/ca-certificates/... (absolute). On the
    # Android host /usr does not exist, so the absolute link dangles and the canonical check stays inside the root.
    write("symlink-chain-abs-dangling.tar", tar_bytes([
        info("usr/share/ca/X.crt", F, 0o644, b"cert\n"),
        info("etc/ssl/certs/X.pem", S, 0o777, link="/usr/share/ca/X.crt"),
        info("etc/ssl/certs/abcd1234.0", S, 0o777, link="X.pem"),
    ]))
    write("through-rel-symlink.tar", tar_bytes([
        info("usr/bin/", D, 0o755),
        info("bin", S, 0o777, link="usr/bin"),
        info("bin/sh", F, 0o755, b"shell\n"),                                # lands in usr/bin/sh
    ]))

    write("err-protected-user-data.tar", tar_bytes([info("ok.txt", F, 0o644, b"ok\n"), info("user-data/x", F, 0o644, b"x\n")]))
    write("err-protected-dsh.tar", tar_bytes([info("root/.dsh/credentials.yaml", F, 0o600, b"k\n")]))
    write("err-traversal.tar", tar_bytes([info("ok.txt", F, 0o644, b"ok\n"), info("a/../../evil", F, 0o644, b"x\n")]))
    write("err-symlink-escape.tar", tar_bytes([info("escape", S, 0o777, link="../../outside")]))
    write("err-hardlink-escape.tar", tar_bytes([info("hl", H, 0o644, link="../../etc/passwd")]))
    write("err-file-over-dir.tar", tar_bytes([info("a/", D, 0o755), info("a", F, 0o644, b"x\n")]))
    write("err-symlink-exists.tar", tar_bytes([info("a", F, 0o644, b"x\n"), info("a", S, 0o777, link="b")]))
    write("err-readonly-dir.tar", tar_bytes([info("ro/", D, 0o555), info("ro/f", F, 0o644, b"x\n")]))
    write("err-not-an-archive.bin", b"this is neither tar nor any supported compression\n" * 8)

    small = tar_bytes([info("only.txt", F, 0o644, b"compressed\n")])
    write("small.tar.bz2", bz2.compress(small, 9))
    write("small.tar.xz", lzma.compress(small, format=lzma.FORMAT_XZ))
    write_expectations()
    print(f"fixtures written to {os.path.normpath(OUT)}")


# ---------------------------------------------------------------------------------------------------------------
# Expected outcomes, derived from DSHBox v1.5.1 BundleManager.extractTarGz:
#  - mode = owner bits only: r = 0400, w = 0200, x = ANY execute bit (mode & 0111); parents created implicitly have
#    no recorded mode (null); symlinks get no mode;
#  - hard links become independent copies; a target that is never extracted is skipped with a warning;
#  - commons-compress isFile() is true for every type except directories, so device nodes / FIFOs become EMPTY
#    regular files (the "skip device nodes" branch in the legacy code is unreachable);
#  - kotlinSkip: legacy runs on the desktop JVM where android.system.Os.symlink is a no-op stub, so cases whose
#    outcome depends on a symlink really existing are only checked by C# (and on a device via the shadow check).
# ---------------------------------------------------------------------------------------------------------------
import hashlib
import json


def f(path, mode, data):
    return {"path": path, "kind": "file", "mode": mode, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}


def d(path, mode=None):
    return {"path": path, "kind": "dir", "mode": mode}


def s(path, link):
    return {"path": path, "kind": "symlink", "link": link}


def ok(*entries, warnings=0):
    return {"result": "OK", "entries": sorted(entries, key=lambda e: e["path"]), "warnings": warnings}


def err(code):
    return {"result": code}


LONG = "x/" + "long-component-" * 8 + "end.txt"
BASIC = ok(
    d("etc", "rwx"), f("etc/debian_version", "rw-", b"13.1\n"),
    d("root", "rwx"), f("root/secret.yaml", "rw-", b"token: x\n"),
    d("usr"), d("usr/bin"), f("usr/bin/tool", "rwx", b"#!/bin/sh\necho tool\n"),
    d("opt"), f("opt/abs-name.txt", "r--", b"leading slash stripped\n"),
    d("var"), d("var/empty", "rwx"),
    d("x"), f(LONG, "rw-", b"long name via pax\n"),
)

CASES = [
    {"name": "basic: modes, implicit parents, ./ and / prefixes, trailing-slash dir, pax long name", "archive": "basic.tar", "expect": BASIC},
    {"name": "basic, gzip container", "archive": "basic.tar.gz", "expect": BASIC},
    {"name": "hard links: absolute and ./ forms, deferred target, unresolved target", "archive": "hardlinks.tar", "expect": ok(
        d("usr"), d("usr/bin"),
        f("usr/bin/perl5.40.1", "rwx", b"perl-binary\n"), f("usr/bin/perl", "rwx", b"perl-binary\n"),
        f("usr/bin/perl-dot", "rwx", b"perl-binary\n"),
        f("usr/bin/late", "rw-", b"late-target\n"), f("usr/bin/early", "rwx", b"late-target\n"),
        warnings=1)},
    {"name": "device nodes and fifos become empty regular files (legacy quirk)", "archive": "devices.tar", "expect": ok(
        d("dev", "rwx"), f("dev/null", "rw-", b""), f("dev/loop0", "rw-", b""), d("run"), f("run/initctl", "rw-", b""))},
    {"name": "symlinks: relative, forward, absolute (root-relative) dangling, relative with ..", "archive": "symlinks.tar", "expect": ok(
        d("usr"), d("usr/lib"), f("usr/lib/libfoo.so.1", "rw-", b"elf\n"), s("usr/lib/libfoo.so", "libfoo.so.1"),
        s("bin", "usr/bin"), d("usr/bin", "rwx"),
        d("etc"), d("etc/alternatives"), s("etc/alternatives/editor", "/usr/bin/vim"),
        d("var"), s("var/run", "../run"))},
    {"name": "writing through an absolute symlink leaves the root", "archive": "through-abs-symlink.tar",
     "expect": err("BUNDLE_UNSAFE_PATH"), "kotlinSkip": "needs real symlinks"},
    {"name": "relative link to an absolute link that dangles on the Android host (Debian ca-certificates)",
     "archive": "symlink-chain-abs-dangling.tar", "expect": ok(
        d("usr"), d("usr/share"), d("usr/share/ca"), f("usr/share/ca/X.crt", "rw-", b"cert\n"),
        d("etc"), d("etc/ssl"), d("etc/ssl/certs"), s("etc/ssl/certs/X.pem", "/usr/share/ca/X.crt"), s("etc/ssl/certs/abcd1234.0", "X.pem"))},
    {"name": "writing through a relative symlink lands at its target", "archive": "through-rel-symlink.tar", "expect": ok(
        d("usr"), d("usr/bin", "rwx"), s("bin", "usr/bin"), f("usr/bin/sh", "rwx", b"shell\n")),
     "kotlinSkip": "needs real symlinks"},
    {"name": "protected user-data path", "archive": "err-protected-user-data.tar", "expect": err("BUNDLE_PROTECTED_PATH")},
    {"name": "protected .dsh path", "archive": "err-protected-dsh.tar", "expect": err("BUNDLE_PROTECTED_PATH")},
    {"name": "path traversal", "archive": "err-traversal.tar", "expect": err("BUNDLE_UNSAFE_PATH")},
    {"name": "symlink pointing outside the root", "archive": "err-symlink-escape.tar", "expect": err("BUNDLE_UNSAFE_LINK")},
    {"name": "hard link pointing outside the root", "archive": "err-hardlink-escape.tar", "expect": err("BUNDLE_UNSAFE_LINK")},
    {"name": "regular file over an existing directory", "archive": "err-file-over-dir.tar", "expect": err("BUNDLE_EXTRACT_FAILED")},
    {"name": "symlink over an existing file", "archive": "err-symlink-exists.tar", "expect": err("BUNDLE_SYMLINK_FAILED"),
     "kotlinSkip": "needs real symlinks"},
    {"name": "child of a directory whose mode drops owner write (Linux EACCES)", "archive": "err-readonly-dir.tar",
     "expect": err("BUNDLE_EXTRACT_FAILED"), "kotlinSkip": "Windows JVM ignores directory permissions"},
    {"name": "not an archive (falls through to gzip)", "archive": "err-not-an-archive.bin", "expect": err("BUNDLE_EXTRACT_FAILED")},
    {"name": "bzip2 container", "archive": "small.tar.bz2", "expect": ok(f("only.txt", "rw-", b"compressed\n")),
     "csharp": err("BUNDLE_FORMAT_UNSUPPORTED")},
    {"name": "xz container", "archive": "small.tar.xz", "expect": ok(f("only.txt", "rw-", b"compressed\n")),
     "csharp": err("BUNDLE_FORMAT_UNSUPPORTED")},
]


def write_expectations():
    doc = {
        "description": "Expected outcomes of extracting tests/TestVectors/layers/* (generated by tooling/gen-layer-fixtures.py). "
                       "`expect` is the legacy DSHBox BundleManager.extractTarGz behavior, checked by native-bridge "
                       "LegacyLayerExtractParityTest (except kotlinSkip cases) and by C# LayerExtractParityTests. `csharp` "
                       "overrides the C# expectation where a known, documented gap exists (docs/MIGRATION.md). "
                       "The root directory itself is never listed.",
        "cases": CASES,
    }
    path = os.path.join(OUT, "..", "layer-extract.json")
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    write_install_scenarios()


# ---------------------------------------------------------------------------------------------------------------
# LayerInstaller (import + swap) scenarios, run by the legacy DefaultSandboxManager.importRuntimeBundle
# (native-bridge LegacyBundleImportParityTest) and the C# LayerInstaller (LayerInstallerParityTests).
# Each scenario: an initial files/ tree, a bundle zip, the expected result code and the ENTIRE files/ tree afterwards
# (path -> "dir" | file content). Staging directories must be gone afterwards on both sides.
# ---------------------------------------------------------------------------------------------------------------
import zipfile

BUNDLES = os.path.join(OUT, "..", "bundles")


def stable_zip(path, members):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_STORED) as z:
        for name, data in members:
            zi = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            zi.external_attr = 0o644 << 16
            z.writestr(zi, data)


def bundle(name, layers, corrupt=None):
    """layers: {layer: tar bytes}. corrupt: layer whose archive is altered AFTER its digest was declared."""
    sha = {k: hashlib.sha256(v).hexdigest() for k, v in layers.items()}
    profile = json.dumps({
        "bundle": {"kind": "runtime", "name": "fixture", "version": "9.9.9", "arch": "arm64", "compression": "none"},
        "layers": [{"name": k, "version": "9.9.9", "compression": "none", "sha256": sha[k], "size_bytes": len(v),
                    "env_file": "", "file": f"{k}.tar", "deps": [] if k == "base" else ["base"]} for k, v in layers.items()],
        "assembly": list(layers),
    }, indent=2).encode()
    members = []
    for k, v in layers.items():
        members.append((f"{k}.tar", v + b"\0" * 512 if k == corrupt else v))
        members.append((f"{k}.tar.sha256", f"{sha[k]}  build/layers/{k}.tar\n".encode()))
    members.append(("runtime-profile.json", profile))
    stable_zip(os.path.join(BUNDLES, name), members)
    return sha, profile.decode()


def write_install_scenarios():
    os.makedirs(BUNDLES, exist_ok=True)
    F = tarfile.REGTYPE
    layers = {
        "base": tar_bytes([info("etc/os-release", F, 0o644, b"new-base\n")]),
        "node": tar_bytes([info("bin/node", F, 0o755, b"new-node\n")]),
        "android-side": tar_bytes([info("proot/README", F, 0o644, b"new-side\n")]),
    }
    sha, profile = bundle("good.zip", layers)
    bundle("corrupt-node.zip", layers, corrupt="node")

    rc = "runtime/runtime-current"
    new_body = {
        f"{rc}/base": "dir", f"{rc}/base/etc": "dir", f"{rc}/base/etc/os-release": "new-base\n",
        f"{rc}/base/.dshbox": "dir", f"{rc}/base/.dshbox/layer-base.sha256": sha["base"],
        f"{rc}/node": "dir", f"{rc}/node/bin": "dir", f"{rc}/node/bin/node": "new-node\n",
        f"{rc}/node/.dshbox": "dir", f"{rc}/node/.dshbox/layer-node.sha256": sha["node"],
        f"{rc}/android-side": "dir", f"{rc}/android-side/proot": "dir", f"{rc}/android-side/proot/README": "new-side\n",
        f"{rc}/android-side/.dshbox": "dir", f"{rc}/android-side/.dshbox/layer-android-side.sha256": sha["android-side"],
        f"{rc}/runtime-profile.json": profile,
    }
    common = {"runtime": "dir", rc: "dir", "user-data": "dir", "user-data/keep.txt": "user\n"}
    old_body = {
        f"{rc}/base": "dir", f"{rc}/base/old.txt": "old-base\n",
        f"{rc}/node": "dir", f"{rc}/node/old.txt": "old-node\n",
        f"{rc}/android-side": "dir", f"{rc}/android-side/old.txt": "old-side\n",
        f"{rc}/runtime-profile.json": "old-profile\n",
        f"{rc}/dsh": "dir", f"{rc}/dsh/dsh.txt": "dsh\n",
    }
    older_previous = {
        f"{rc}/previous": "dir", f"{rc}/previous/base": "dir", f"{rc}/previous/base/older.txt": "older-base\n",
        f"{rc}/previous/runtime-profile.json": "older-profile\n",
        f"{rc}/previous/dsh": "dir", f"{rc}/previous/dsh/prev-dsh.txt": "prev-dsh\n",
    }
    moved_previous = {
        f"{rc}/previous": "dir",
        f"{rc}/previous/base": "dir", f"{rc}/previous/base/old.txt": "old-base\n",
        f"{rc}/previous/node": "dir", f"{rc}/previous/node/old.txt": "old-node\n",
        f"{rc}/previous/android-side": "dir", f"{rc}/previous/android-side/old.txt": "old-side\n",
        f"{rc}/previous/runtime-profile.json": "old-profile\n",
        f"{rc}/previous/dsh": "dir", f"{rc}/previous/dsh/prev-dsh.txt": "prev-dsh\n",
    }
    dsh_only = {f"{rc}/dsh": "dir", f"{rc}/dsh/dsh.txt": "dsh\n"}

    def sc(name, zip_name, before, code, after):
        return {"name": name, "bundle": zip_name, "before": dict(sorted(before.items())), "result": code,
                "after": dict(sorted(after.items()))}

    scenarios = [
        sc("fresh install into an empty runtime-current", "good.zip", common, "OK", {**common, **new_body}),
        # Legacy quirk: previous/ does not exist, so renaming the old layers into it fails and they are DELETED;
        # the old profile cannot be moved either and is overwritten. No rollback copy remains.
        sc("replace without previous/: old body discarded", "good.zip", {**common, **old_body}, "OK",
           {**common, **new_body, **dsh_only}),
        sc("replace with previous/: old body becomes previous, older previous dropped, dsh untouched", "good.zip",
           {**common, **old_body, **older_previous}, "OK", {**common, **new_body, **dsh_only, **moved_previous}),
        sc("corrupt layer: nothing changes", "corrupt-node.zip", {**common, **old_body, **older_previous},
           "BUNDLE_SHA256_MISMATCH", {**common, **old_body, **older_previous}),
    ]
    doc = {
        "description": "LayerInstaller scenarios (generated by tooling/gen-layer-fixtures.py). `before`/`after` list the whole "
                       "files/ tree: path -> \"dir\" or the file's UTF-8 content. Run by the legacy "
                       "DefaultSandboxManager.importRuntimeBundle (native-bridge LegacyBundleImportParityTest) and the C# "
                       "LayerInstaller (LayerInstallerParityTests); both must leave no staging directory behind.",
        "scenarios": scenarios,
    }
    with open(os.path.join(OUT, "..", "install-scenarios.json"), "w", encoding="utf-8", newline="\n") as fh:
        json.dump(doc, fh, indent=2, ensure_ascii=False)
        fh.write("\n")


if __name__ == "__main__":
    sys.exit(main())
