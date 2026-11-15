# APK assets

`terminal/`: the C# terminal page (own files) and vendored xterm.js (`tooling/terminal-web.lock.json`).

The files below were copied **unmodified** from DSHBox `v1.5.1` (`7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4`, GPL-3.0-or-later)
when PR-10 removed the submodule (ADR-0018). Before PR-10 the legacy Gradle build staged the same bytes into the Bridge AAR.
`GuestAssetProvisioner` / `GuestToolchain` copy them to the guest; `GuestAssetProvisionerTests` pins the hashes.

| File | DSHBox source path | SHA-256 |
|---|---|---|
| `dshbox/link-shim.mjs` | `plugin-manager/dshbox-plugins/assets/dshbox/link-shim.mjs` | `c34505677976141016d7ceffb7fc255f8e4de1ac6c232eb6cd7b4d6d0db9b497` |
| `dshbox/libdshbox-link.so` (aarch64 glibc; source + build script in `native/link-shim/`) | `plugin-manager/dshbox-plugins/assets/dshbox/libdshbox-link.so` | `7d880b83ee456e93c91b52aa5df1be0dc35e4ded4ed9718bac942c3d61e1973a` |
| `toolchain-cache/corepack-pnpm-11.1.2.tar.zst` (offline corepack cache of pnpm 11.1.2, MIT) | `app/src/main/assets/toolchain-cache/corepack-pnpm-11.1.2.tar.zst` | `d9f9677d89a9027df3f5c8d70922789a0c00cdd15b4dece8b36f0d07b0f02498` |
| `toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256` | `app/src/main/assets/toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256` | `c1dcec1a5cf41a8bebdf87cab32ca127c9588772632b7b7f60d5202f07d57dba` |

Not taken: `xdg-open` and DSHBox's plugins (`browser-use`, mobile-pilot, mobile-adapt). The browser feature was dropped in PR-10.
