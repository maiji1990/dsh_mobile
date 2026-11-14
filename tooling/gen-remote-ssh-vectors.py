#!/usr/bin/env python3
"""Generates tests/TestVectors/remote-ssh.json (PR-09 first segment).

Expected values come from this independent Python model of the legacy Kotlin rules in
native/native-bridge/src/main/java/dshmobile/nativebridge/{RemoteHosts,SshKeys}.kt (JVM / Android libcore semantics),
not from either implementation under test. The legacy Kotlin LegacyRemoteSshParityTest and the C# RemoteSshParityTests
both run the file. Cases marked "csharpOnly" depend on Android libcore org.json (the device), which differs from the
real org.json the JVM unit tests use.

Run: python3 tooling/gen-remote-ssh-vectors.py
"""
import base64
import hashlib
import json
import os
import re
import struct
import unicodedata

ALG = "ecdsa-sha2-nistp256"

# ---------------------------------------------------------------- SSH wire (SshWire)


def ssh_string(b: bytes) -> bytes:
    return struct.pack(">I", len(b)) + b


def strip0(b: bytes) -> bytes:
    i = 0
    while i < len(b) and b[i] == 0:
        i += 1
    return b[i:]


def java_bigint_bytes(mag: bytes) -> bytes:
    """BigInteger(1, mag).toByteArray()."""
    m = strip0(mag)
    if not m:
        return b"\x00"
    return b"\x00" + m if m[0] & 0x80 else m


def fixed32(mag: bytes) -> bytes:
    raw = java_bigint_bytes(mag)
    if len(raw) > 32 and raw[0] == 0:
        raw = raw[1:]
    if len(raw) > 32:
        raise ValueError("coordinate too large")
    return b"\x00" * (32 - len(raw)) + raw


def public_blob(x: bytes, y: bytes) -> bytes:
    return ssh_string(ALG.encode()) + ssh_string(b"nistp256") + ssh_string(b"\x04" + fixed32(x) + fixed32(y))


def fingerprint(blob: bytes) -> str:
    return "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode().rstrip("=")


def der_to_ssh(der: bytes) -> bytes:
    i = 0

    def byte(at):
        if at < 0 or at >= len(der):
            raise IndexError("ArrayIndexOutOfBounds")
        return der[at]

    def length(at):
        first = byte(at)
        if first < 0x80:
            return first
        n = 0
        for k in range(1, (first & 0x7F) + 1):
            n = ((n << 8) | byte(at + k)) & 0xFFFFFFFF
        return n - (1 << 32) if n & 0x80000000 else n  # Kotlin Int

    def length_size(at):
        first = byte(at)
        return 1 if first < 0x80 else 1 + (first & 0x7F)

    if byte(i) != 0x30:
        raise ValueError("not a DER sequence")
    i += 1
    i += length_size(i)
    ints = []
    for _ in range(2):
        if byte(i) != 0x02:
            raise ValueError("not a DER integer")
        i += 1
        n = length(i)
        i += length_size(i)
        if n < 0 or i + n > len(der):
            raise ValueError("kotlin copyOfRange: bad range")  # Kotlin checks toIndex <= size (java.util.Arrays would pad)
        ints.append(der[i:i + n])
        i += n
    inner = ssh_string(java_bigint_bytes(ints[0])) + ssh_string(java_bigint_bytes(ints[1]))
    return ssh_string(ALG.encode()) + ssh_string(inner)


def key_type(k: bytes):
    if len(k) < 4:
        return None
    (n,) = struct.unpack(">i", k[:4])
    if n < 0 or 4 + n > len(k):
        return None
    return k[4:4 + n].decode("utf-8")


# ---------------------------------------------------------------- Kotlin text + libcore org.json


def kt_ws(c: str) -> bool:
    o = ord(c)
    return 0x09 <= o <= 0x0D or 0x1C <= o <= 0x1F or unicodedata.category(c) in ("Zs", "Zl", "Zp")


def kt_trim(s: str) -> str:
    a, b = 0, len(s)
    while a < b and kt_ws(s[a]):
        a += 1
    while b > a and kt_ws(s[b - 1]):
        b -= 1
    return s[a:b]


def iso_control(c: str) -> bool:
    o = ord(c)
    return o <= 0x1F or 0x7F <= o <= 0x9F


def utf16_len(s: str) -> int:
    return len(s.encode("utf-16-le")) // 2


def opt_string(o: dict, k: str) -> str:
    if k not in o:
        return ""
    v = o[k]
    if v is None:
        return "null"
    if v is True:
        return "true"
    if v is False:
        return "false"
    if isinstance(v, float):
        return repr(v)  # only simple values in the vectors (Java Double.toString agrees)
    return str(v)


def java_d2i(d: float) -> int:
    if d != d:
        return 0
    return max(-2**31, min(2**31 - 1, int(d)))


def opt_int(o: dict, k: str, fallback: int) -> int:
    if k not in o:
        return fallback
    v = o[k]
    if isinstance(v, bool) or v is None:
        return fallback
    if isinstance(v, int):
        if -2**63 <= v < 2**63:
            n = v & 0xFFFFFFFF
            return n - (1 << 32) if n & 0x80000000 else n
        return java_d2i(float(v))
    if isinstance(v, float):
        return java_d2i(v)
    if isinstance(v, str):
        try:
            return java_d2i(float(v.strip()))
        except ValueError:
            return fallback
    return fallback


HOST = re.compile(r"(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*|[0-9a-fA-F:.]{2,45})")
USER = re.compile(r"[A-Za-z0-9_][A-Za-z0-9._-]{0,63}")
FP = re.compile(r"SHA256:[A-Za-z0-9+/]{43}")


def is_fp(s) -> bool:
    return isinstance(s, str) and FP.fullmatch(s) is not None


def from_request(p: dict, existing):
    host = kt_trim(opt_string(p, "host"))
    user = kt_trim(opt_string(p, "user"))
    port = opt_int(p, "port", 22)
    dsh = opt_int(p, "dshPort", 3091)
    label = kt_trim(opt_string(p, "label")) or f"{user}@{host}"
    if not HOST.fullmatch(host):
        raise ValueError("host must be a host name or IP address")
    if not USER.fullmatch(user):
        raise ValueError("user may contain letters, digits, . _ - (max 64)")
    if not 1 <= port <= 65535:
        raise ValueError("port must be 1..65535")
    if not 1024 <= dsh <= 65535:
        raise ValueError("dshPort must be 1024..65535")
    if utf16_len(label) > 64 or any(iso_control(c) for c in label):
        raise ValueError("label must be at most 64 printable characters")
    same = existing is not None and existing["host"] == host and existing["port"] == port
    return {
        "id": existing["id"] if existing else "<new>",
        "label": label, "host": host, "port": port, "user": user, "dshPort": dsh,
        "pinnedFingerprint": existing["pinnedFingerprint"] if same else None,
    }


def requested_id(p: dict):
    if "id" not in p or p["id"] is None:
        return None
    s = opt_string(p, "id")
    return None if all(kt_ws(c) for c in s) else s


def libcore_get_string(o, k):
    # libcore get() only fails for a missing name; JSON null is JSONObject.NULL, and JSON.toString(NULL) is "null".
    if k not in o:
        raise KeyError(k)
    return opt_string(o, k)


def libcore_get_int(o, k):
    if k not in o:
        raise KeyError(k)
    v = o[k]
    if isinstance(v, bool) or v is None or isinstance(v, (dict, list)):
        raise ValueError(k)
    if isinstance(v, str):
        try:
            return java_d2i(float(v.strip()))
        except ValueError:
            raise ValueError(k)
    return opt_int(o, k, 0)


def parse_stored(stored):
    try:
        arr = json.loads(stored if stored is not None else "[]")
        if not isinstance(arr, list):
            return []
        out = []
        for o in arr:
            if not isinstance(o, dict):
                raise ValueError("entry")
            pin = opt_string(o, "pinnedFingerprint")
            out.append({
                "id": libcore_get_string(o, "id"), "label": libcore_get_string(o, "label"),
                "host": libcore_get_string(o, "host"), "port": libcore_get_int(o, "port"),
                "user": libcore_get_string(o, "user"), "dshPort": libcore_get_int(o, "dshPort"),
                "pinnedFingerprint": pin if is_fp(pin) else None,
            })
        return out
    except (ValueError, KeyError):
        return []


def token_from(line: str):
    at = line.find("dsh web:")
    if at < 0:
        return None
    t = line.find("token=", at)
    if t < 0:
        return None
    start = end = t + len("token=")
    while end < len(line) and not kt_ws(line[end]) and line[end] not in "&#":
        end += 1
    return line[start:end] or None


def redact(line: str) -> str:
    return re.sub(r"token=[^ \t\n\x0b\f\r&#]+", "token=***", line)


# ---------------------------------------------------------------- cases

OPENSSH_BLOB = base64.b64decode(
    "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBJvX3yMyoKRRfc5yC0oD+7pgEvqjrZVnE63xChjDsLHuR/bvlBGEFJBkj/QEX4dhMbBwZkomegmIGYl3quVl+1I=")
OPENSSH_FP = "SHA256:/O0DopfcupCOPu5e6/7zGNQITxFKE51poNy7jxKvT5Y"
assert fingerprint(OPENSSH_BLOB) == OPENSSH_FP
PT = OPENSSH_BLOB[-65:]
OX, OY = PT[1:33], PT[33:65]
assert public_blob(OX, OY) == OPENSSH_BLOB
OTHER_FP = "SHA256:" + "A" * 43


def b64(b):
    return base64.b64encode(b).decode()


def blob_case(name, x: bytes, y: bytes):
    c = {"name": name, "x": x.hex(), "y": y.hex()}
    try:
        blob = public_blob(x, y)
        c.update(blob=b64(blob), fingerprint=fingerprint(blob), line=f"{ALG} {b64(blob)} dsh-mobile")
    except ValueError:
        c["error"] = True
    return c


public_blobs = [
    blob_case("OpenSSH ssh-keygen -t ecdsa -b 256 key", OX, OY),
    blob_case("x with a leading 0x00 sign byte (BigInteger.toByteArray)", b"\x00" + bytes([0x80]) + OX[1:], OY),
    blob_case("short coordinates are left-padded to 32 bytes", OX[2:], OY[5:]),
    blob_case("zero coordinate", b"", b"\x00"),
    blob_case("33 significant bytes is too large", b"\x01" + OX, OY),
]

fingerprints = [
    {"blob": b64(OPENSSH_BLOB), "fingerprint": OPENSSH_FP},
    {"blob": "", "fingerprint": fingerprint(b"")},
    {"blob": b64(b"\x00\x00\x00\x0bssh-ed25519" + ssh_string(bytes(range(32)))),
     "fingerprint": fingerprint(b"\x00\x00\x00\x0bssh-ed25519" + ssh_string(bytes(range(32))))},
]

R = bytes([0x11] * 32)
S = bytes([0x22] * 32)
RH = bytes([0x91] + [0x33] * 31)  # high bit set


def der_int(v: bytes) -> bytes:
    return b"\x02" + bytes([len(v)]) + v


def der_seq(body: bytes) -> bytes:
    return b"\x30" + bytes([len(body)]) + body


der_cases_raw = [
    ("canonical 32-byte r and s", der_seq(der_int(R) + der_int(S))),
    ("r with the high bit set carries a 0x00 in DER and in the mpint", der_seq(der_int(b"\x00" + RH) + der_int(S))),
    ("short r (31 bytes)", der_seq(der_int(R[1:]) + der_int(S))),
    ("redundant leading zeros are dropped", der_seq(der_int(b"\x00\x00" + R[2:]) + der_int(S))),
    ("non-canonical negative-looking r is read unsigned", der_seq(der_int(RH) + der_int(S))),
    ("long-form sequence length", b"\x30\x81\x44" + der_int(R) + der_int(S)),
    ("long-form integer length", der_seq(b"\x02\x81\x20" + R + der_int(S))),
    ("indefinite (0x80) sequence length is skipped", b"\x30\x80" + der_int(R) + der_int(S)),
    ("bytes after s are ignored", der_seq(der_int(R) + der_int(S)) + b"\xde\xad"),
    ("sequence length is not checked", b"\x30\x05" + der_int(R) + der_int(S)),
    ("r = 0 becomes a one-byte 0x00 mpint", der_seq(der_int(b"\x00") + der_int(S))),
    ("truncated s is an error (Kotlin copyOfRange checks the range)", der_seq(der_int(R) + b"\x02\x20" + S[:10])),
    ("not a sequence", b"\x31\x04" + der_int(b"\x01") + der_int(b"\x01")),
    ("not an integer", der_seq(b"\x03\x01\x01" + der_int(b"\x01"))),
    ("missing s", der_seq(der_int(R))),
    ("empty input", b""),
    ("negative long-form length", b"\x30\x06\x02\x84\xff\xff\xff\xff"),
]
der_cases = []
for name, der in der_cases_raw:
    c = {"name": name, "der": der.hex()}
    try:
        c["ssh"] = der_to_ssh(der).hex()
    except (ValueError, IndexError):
        c["error"] = True
    der_cases.append(c)

EXIST = {"id": "id-1", "label": "pc", "host": "pc.lan", "port": 22, "user": "u", "dshPort": 3091, "pinnedFingerprint": OPENSSH_FP}


def prof(name, req, existing=None, csharp_only=False):
    c = {"name": name, "request": req, "existing": existing}
    try:
        c["profile"] = from_request(req, existing)
    except ValueError as e:
        c["error"] = str(e)
    if csharp_only:
        c["csharpOnly"] = True
    return c


base = {"host": "127.0.0.1", "port": 2222, "user": "wangbc"}
profile_cases = [
    prof("minimal new host: default label and DSH port", dict(base)),
    prof("what the C# page sends for a new host", {"id": None, "label": "", "host": "pc.lan", "port": 22, "user": "wangbc", "dshPort": 3091}),
    prof("host and user are trimmed", {"host": "  pc.lan\t", "user": " u \n"}),
    prof("IPv6 literal", {"host": "::1", "user": "u"}),
    prof("trailing hyphen in a label is allowed", {"host": "pc-.lan", "user": "u"}),
    prof("63-character DNS label", {"host": "a" * 63 + ".lan", "user": "u", "label": "x"}),
    prof("64-character DNS label", {"host": "a" * 64 + ".lan", "user": "u", "label": "x"}),
    prof("a long host makes the default label too long", {"host": "a" * 63 + ".lan", "user": "u"}),
    prof("lenient dotted digits", {"host": "999.999.999.999", "user": "u"}),
    prof("'a..b' passes through the hex / IP alternative", {"host": "a..b", "user": "u"}),
    prof("'g..h' is not hex: rejected", {"host": "g..h", "user": "u"}),
]
for h in ["-oProxyCommand=x", "a b", "host;id", "", "h$(x)", "fe80::1%wlan0", "pc.例", "pc\u0085"]:
    profile_cases.append(prof(f"bad host {h!r}", {"host": h, "user": "u"}))
profile_cases += [
    prof("U+001F is trimmed by Kotlin (not by .NET Trim)", {"host": "pc.lan\u001f", "user": "u"}),
    prof("host checked before user", {"host": "a b", "user": "c d"}),
]
for u in ["-l", "a b", "u;id", "", "x" * 65, ".x"]:
    profile_cases.append(prof(f"bad user {u!r}", {"host": "pc.lan", "user": u}))
profile_cases += [
    prof("64-character user", {"host": "pc.lan", "user": "x" * 64, "label": "x"}),
    prof("user with . _ -", {"host": "pc.lan", "user": "_Wang.Bao-1"}),
    prof("user given as a number", {"host": "pc.lan", "user": 42}),
    prof("port 0", {"host": "pc", "user": "u", "port": 0}),
    prof("port -1", {"host": "pc", "user": "u", "port": -1}),
    prof("port 65536", {"host": "pc", "user": "u", "port": 65536}),
    prof("port 65535", {"host": "pc", "user": "u", "port": 65535}),
    prof("port as a numeric string", {"host": "pc", "user": "u", "port": "2222"}),
    prof("port as a decimal string truncates", {"host": "pc", "user": "u", "port": "22.9"}),
    prof("port boolean falls back to 22", {"host": "pc", "user": "u", "port": True}),
    prof("port non-numeric string falls back to 22", {"host": "pc", "user": "u", "port": "ssh"}),
    prof("port long keeps the low 32 bits (4294967318 → 22)", {"host": "pc", "user": "u", "port": 4294967318}),
    prof("dshPort 80", {"host": "pc", "user": "u", "dshPort": 80}),
    prof("dshPort 1023", {"host": "pc", "user": "u", "dshPort": 1023}),
    prof("dshPort 1024", {"host": "pc", "user": "u", "dshPort": 1024}),
    prof("label of 64 characters", {"host": "pc", "user": "u", "label": "L" * 64}),
    prof("label of 65 characters", {"host": "pc", "user": "u", "label": "L" * 65}),
    prof("label length counts UTF-16 units (32 emoji)", {"host": "pc", "user": "u", "label": "\U0001F600" * 32}),
    prof("label length counts UTF-16 units (33 emoji)", {"host": "pc", "user": "u", "label": "\U0001F600" * 33}),
    prof("label with a tab", {"host": "pc", "user": "u", "label": "a\tb"}),
    prof("label with U+0085 (control, not trimmed)", {"host": "pc", "user": "u", "label": "\u0085x"}),
    prof("label ending in U+001F (trimmed)", {"host": "pc", "user": "u", "label": "x\u001f"}),
    prof("blank label gets the default", {"host": "pc", "user": "u", "label": "   "}),
    prof("label with a zero-width space", {"host": "pc", "user": "u", "label": "a​b"}),
    prof("update, same peer: pin kept", {"host": "pc.lan", "port": 22, "user": "other", "label": "new"}, EXIST),
    prof("update, other host: pin dropped", {"host": "pc2.lan", "port": 22, "user": "u"}, EXIST),
    prof("update, other port: pin dropped", {"host": "pc.lan", "port": 2222, "user": "u"}, EXIST),
    prof("update, host case differs: pin dropped", {"host": "PC.lan", "port": 22, "user": "u"}, EXIST),
    prof("update with a bad port is rejected", {"host": "pc.lan", "port": 0, "user": "u"}, EXIST),
    prof("libcore: host null reads as \"null\" (a valid name)", {"host": None, "user": "u"}, csharp_only=True),
    prof("libcore: label null reads as \"null\"", {"host": "pc", "user": "u", "label": None}, csharp_only=True),
    prof("libcore: port 22.9 number truncates", {"host": "pc", "user": "u", "port": 22.9}, csharp_only=True),
]

requested_ids = [
    {"request": {"id": None, "host": "127.0.0.1"}, "id": None},
    {"request": {"host": "127.0.0.1"}, "id": None},
    {"request": {"id": "  "}, "id": None},
    {"request": {"id": "\u001f"}, "id": None},
    {"request": {"id": "abc"}, "id": "abc"},
    {"request": {"id": " abc "}, "id": " abc "},
    {"request": {"id": "\u0085"}, "id": "\u0085"},
    {"request": {"id": 5}, "id": "5"},
]


def stored(entries):
    return json.dumps(entries, ensure_ascii=False, separators=(",", ":"))


GOOD1 = {"id": "a", "label": "pc", "host": "pc.lan", "port": 22, "user": "u", "dshPort": 3091, "pinnedFingerprint": OPENSSH_FP}
GOOD2 = {"id": "b", "label": "lab", "host": "10.0.0.2", "port": 2222, "user": "w", "dshPort": 3092, "pinnedFingerprint": None}
stored_raw = [
    ("absent", None, False),
    ("empty list", "[]", False),
    ("two profiles, in order", stored([GOOD1, GOOD2]), False),
    ("a malformed pin is dropped", stored([dict(GOOD1, pinnedFingerprint="SHA256:short")]), False),
    ("a missing pin is none", stored([{k: v for k, v in GOOD1.items() if k != "pinnedFingerprint"}]), False),
    ("one entry without dshPort empties the whole list", stored([GOOD1, {k: v for k, v in GOOD2.items() if k != "dshPort"}]), False),
    ("a null port empties the whole list", stored([dict(GOOD1, port=None)]), False),
    ("libcore: a null id reads as \"null\" (org.json on the JVM throws)", stored([dict(GOOD1, id=None)]), True),
    ("port stored as a numeric string", stored([dict(GOOD1, port="22")]), False),
    ("not JSON", "garbage", False),
    ("libcore: a numeric id reads as text", stored([dict(GOOD1, id=5)]), True),
]
stored_cases = []
for name, s, only in stored_raw:
    c = {"name": name, "stored": s, "profiles": parse_stored(s)}
    if only:
        c["csharpOnly"] = True
    stored_cases.append(c)


def hk(name, pinned, key):
    c = {"name": name, "pinned": pinned, "key": None if key is None else b64(key)}
    if key is None:
        c.update(decision="NotIncluded", seenFingerprint=None, seenType=None)
    else:
        fp = fingerprint(key)
        c.update(decision="NotIncluded" if pinned is None else "Ok" if pinned == fp else "Changed",
                 seenFingerprint=fp, seenType=key_type(key))
    return c


ED = b"\x00\x00\x00\x0bssh-ed25519" + ssh_string(bytes(range(32)))
host_keys = [
    hk("no pin yet: refused, fingerprint shown", None, OPENSSH_BLOB),
    hk("pinned key", OPENSSH_FP, OPENSSH_BLOB),
    hk("different key: changed", OTHER_FP, OPENSSH_BLOB),
    hk("ed25519 server key", None, ED),
    hk("no key presented", OPENSSH_FP, None),
    hk("empty key", None, b""),
    hk("type length runs past the blob", None, b"\x00\x00\x00\x40ssh"),
    hk("negative type length", None, b"\xff\xff\xff\xffssh"),
]

trust = [
    {"name": "the key just presented for this host", "hostId": "a", "fingerprint": OPENSSH_FP, "lastUnknown": ["a", OPENSSH_FP], "allowed": True},
    {"name": "nothing presented", "hostId": "a", "fingerprint": OPENSSH_FP, "lastUnknown": None, "allowed": False},
    {"name": "presented for another host", "hostId": "a", "fingerprint": OPENSSH_FP, "lastUnknown": ["b", OPENSSH_FP], "allowed": False},
    {"name": "another key than the one presented", "hostId": "a", "fingerprint": OTHER_FP, "lastUnknown": ["a", OPENSSH_FP], "allowed": False},
    {"name": "malformed fingerprint, even if it was 'presented'", "hostId": "a", "fingerprint": "SHA256:x", "lastUnknown": ["a", "SHA256:x"], "allowed": False},
]

token_lines = [
    "dsh web: http://127.0.0.1:3091/?token=abc123",
    "[boot] dsh web: http://127.0.0.1:3091/?token=abc123&x=1 ready",
    "listening on http://127.0.0.1:3091/?token=abc123",
    "dsh web: http://127.0.0.1:3091/",
    "token=early dsh web: http://h/",
    "dsh web: http://h/?token=",
    "dsh web: http://h/?token=a#frag",
    "dsh web: http://h/?token=a　b",
    "dsh web: http://h/?token=a\u0085b",
    "dsh web: http://h/?token=a\u001fb",
    "dsh web:token=t",
    "dsh web: http://h/?token= spaced",
]
tokens = [{"line": l, "token": token_from(l)} for l in token_lines]

redact_lines = [
    "url ?token=secret end",
    "token=a&token=b",
    "token=",
    "token=a　b",
    "TOKEN=abc",
    "xtoken=abc",
    "token=a\tb",
    "dsh web: http://127.0.0.1:3091/?token=abc123",
]
redactions = [{"in": l, "out": redact(l)} for l in redact_lines]

# ---------------------------------------------------------------- PR-09 second segment: connect rules (RemoteHosts.connect)

# JSch 2.28.8 JSch.config "server_host_key" default (javap of the jar the native-bridge build uses).
HOST_KEY_ALGORITHMS = (
    "ssh-ed25519-cert-v01@openssh.com,ecdsa-sha2-nistp256-cert-v01@openssh.com,ecdsa-sha2-nistp384-cert-v01@openssh.com,"
    "ecdsa-sha2-nistp521-cert-v01@openssh.com,rsa-sha2-512-cert-v01@openssh.com,rsa-sha2-256-cert-v01@openssh.com,"
    "ssh-ed25519,ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521,rsa-sha2-512,rsa-sha2-256").split(",")

UNKNOWN_MSG = "confirm the host key fingerprint before connecting"
CHANGED_MSG = "host key differs from the pinned one; connection refused"
AUTH_MSG = "public key not accepted; add this phone's key to authorized_keys"


def classify(pinned, seen, kind, lib_msg):
    """The when-block of the legacy connect's catch: host-key facts first, then the failure kind."""
    if pinned is None and seen is not None:
        return "HOST_KEY_UNKNOWN", UNKNOWN_MSG
    if pinned is not None and seen is not None and seen != pinned:
        return "HOST_KEY_CHANGED", CHANGED_MSG
    if kind == "AuthRejected":
        return "AUTH_FAILED", AUTH_MSG
    if kind == "Timeout":
        return "TIMEOUT", lib_msg
    return "UNREACHABLE", lib_msg


def jsch_kind(message):
    m = (message or "").lower()
    if "auth fail" in m:
        return "AuthRejected"
    return "Timeout" if "timeout" in m else "Other"


connect_cases = []
for pinned in (None, OPENSSH_FP, OTHER_FP):
    for seen in (None, OPENSSH_FP):
        for kind in ("AuthRejected", "Timeout", "Other"):
            code, msg = classify(pinned, seen, kind, "lib says no")
            connect_cases.append({
                "name": f"pin {'none' if pinned is None else 'match' if pinned == OPENSSH_FP else 'other'}, "
                        f"key {'seen' if seen else 'not seen'}, {kind}",
                "pinned": pinned, "seen": seen, "kind": kind, "libraryMessage": "lib says no", "code": code, "message": msg})

jsch_messages = [
    "Auth fail for methods 'publickey'", "USERAUTH fail", "auth FAIL", "timeout: socket is not established",
    "Session.connect: java.net.SocketTimeoutException: Read timed out", "java.net.ConnectException: Connection refused",
    "reject HostKey: [127.0.0.1]:2222", "Auth fail timeout", "", None,
]
jsch_kinds = [{"message": m, "kind": jsch_kind(m)} for m in jsch_messages]


def kt_lines(s: str):
    """Kotlin lineSequence(): splits on CRLF, LF, CR; keeps a trailing empty line."""
    return re.split(r"\r\n|\n|\r", s)


def take_utf16(s: str, n: int) -> str:
    u = s.encode("utf-16-le", "surrogatepass")[: 2 * n]
    return u.decode("utf-16-le", "surrogatepass")


def summarize(exit_status, stdout, stderr):
    out = ((stdout or "") + (stderr or ""))
    out = take_utf16(out, 8192)
    version = None
    for line in kt_lines(out):
        if not all(kt_ws(c) for c in line):
            version = take_utf16(kt_trim(line), 80)
            break
    return exit_status == 0, version


probe_raw = [
    ("node on Windows", 0, "v26.13.0\r\n", ""),
    ("dsh version", 0, "0.2.1-rc.5\n", ""),
    ("not found under cmd.exe: the error line is kept", 1, "", "'dsh' is not recognized as an internal or external command,\r\noperable program or batch file.\r\n"),
    ("not found under sh", 127, "", "sh: 1: dsh: not found\n"),
    ("blank lines first", 0, "\n  \r\n\tv1\n", ""),
    ("stderr after stdout", 0, "", "v2 on stderr\n"),
    ("stdout wins over stderr", 0, "out\n", "err\n"),
    ("CR only line ends", 0, "\rv3\rrest", ""),
    ("no exit status", None, "v4\n", ""),
    ("exit 0, no output", 0, "", ""),
    ("only whitespace", 0, " 　 \n ", ""),
    ("NEL is not whitespace in Kotlin", 0, "\u0085\n", ""),
    ("trimmed with unit separators", 0, "\u001f v5 \u001c\n", ""),
    ("first 80 UTF-16 units", 0, "x" * 100 + "\n", ""),
    ("80 units end on a whole surrogate pair", 0, "y" * 78 + "\U0001F600tail\n", ""),
    ("non-zero exit with a version-looking line", 2, "v6\n", ""),
    ("8192-char cap before the first line", 0, " " * 8192 + "late\n", ""),
]
probe_cases = []
for name, ex, so, se in probe_raw:
    present, version = summarize(ex, so, se)
    probe_cases.append({"name": name, "exit": ex, "stdout": so, "stderr": se, "present": present, "version": version})

doc = {
    "description": (
        "PR-09 first segment: remote DSH host over SSH (ADR-0006) — SSH wire encodings, host profile validation and "
        "storage, host-key pin, trust rule, remote dsh web launch-token parsing. Generated by "
        "tooling/gen-remote-ssh-vectors.py from an independent model of the legacy Kotlin rules (RemoteHosts.kt / "
        "SshKeys.kt). Run by the legacy Kotlin (native-bridge LegacyRemoteSshParityTest) and the C# port "
        "(RemoteSshParityTests). csharpOnly = Android libcore org.json semantics (the JVM tests use the real org.json). "
        "trust is C# only: the legacy rule is inline in RemoteHosts.trust (source contract). "
        "PR-09 second segment (ADR-0012): hostKeyAlgorithms (legacy: JSch.getConfig(\"server_host_key\"); C#: the "
        "restricted SSH.NET ConnectionInfo), connectOutcome / jschKind (the legacy connect catch block) and probe "
        "(legacy runProbe / exec): the legacy Kotlin test runs the same expressions as RemoteHosts.kt, which a C# "
        "source contract pins."),
    "publicBlob": public_blobs,
    "fingerprint": fingerprints,
    "derToSsh": der_cases,
    "profileRequest": profile_cases,
    "requestedId": requested_ids,
    "stored": stored_cases,
    "hostKey": host_keys,
    "trust": trust,
    "launchToken": tokens,
    "redact": redactions,
    "hostKeyAlgorithms": HOST_KEY_ALGORITHMS,
    "connectOutcome": connect_cases,
    "jschKind": jsch_kinds,
    "probe": probe_cases,
}

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "TestVectors", "remote-ssh.json")
with open(out, "w", encoding="utf-8", newline="\n") as f:
    json.dump(doc, f, ensure_ascii=True, indent=1)
    f.write("\n")
print(f"wrote {os.path.normpath(out)}: " + ", ".join(f"{k} {len(v)}" for k, v in doc.items() if isinstance(v, list)))
