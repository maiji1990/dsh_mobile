# Relay guest assets (copied from DSHBox, GPL-3.0)

Byte-exact copies, embedded into `DshMobile.Runtime` and published into the guest-visible tree by `GuestAssets`
(`<entry>/bin/relay`, `<entry>/USAGE.md`). Do not edit or re-encode them: the published `version.json` marker carries
their SHA-256, and the dual-relay shadow compares them with what the legacy Relay publishes.

| File | Upstream (DSHBox v1.5.1 @ 7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4) | SHA-256 |
| --- | --- | --- |
| `relay-client.cjs` | `pilot/interlock-relay-core/src/main/assets/relay/relay-client.cjs` (unchanged) | `9cb271f6a1adb50509ebc0293c9f9947fb2c09b71b5c0968942dfbdba0d8ff9b` |
| `relay-usage.template.md` | `pilot/interlock-relay-core/src/main/java/interlock/relay/core/transport/GuestAssets.kt` `USAGE_TEMPLATE` (the Kotlin raw string after `trimIndent()`, no trailing newline) | `e655efd9db93a8b81ab305087db1779ef0a6fe86a204bc07b25aa3e533b2e26f` |

Registered in `docs/UPSTREAMS.md`. License: GPL-3.0 (see `third_party/dshbox/LICENSE`).
