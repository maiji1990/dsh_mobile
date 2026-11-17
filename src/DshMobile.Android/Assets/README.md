# APK assets

The files below come from DSHBox `v1.5.1` (`7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4`, GPL-3.0-or-later). PR-10 copied them
**unmodified** when it removed the submodule (ADR-0018); ADR-0019 moved them from `dshbox/` to `dshmobile/` and replaced the
prebuilt link shim with one built here. `GuestAssetProvisioner` / `GuestToolchain` copy them to the guest;
`UpstreamPinnedValuesTests` pins the hashes.

| File | Origin | SHA-256 |
|---|---|---|
| `dshmobile/link-shim.mjs` | unmodified copy of DSHBox `plugin-manager/dshbox-plugins/assets/dshbox/link-shim.mjs` | `c34505677976141016d7ceffb7fc255f8e4de1ac6c232eb6cd7b4d6d0db9b497` |
| `dshmobile/libdshmobile-link.so` (aarch64 glibc) | **built in this repo** by `native/link-shim/build.sh` from `libdshmobile-link.c` (DSHBox `libdshbox-link.c` with the file name and the `DSHMOBILE_LINK_SHIM_DISABLE` switch renamed, ADR-0019); `aarch64-linux-gnu-gcc` 15.3.0 (Ubuntu 26.10), needs GLIBC ≤ 2.34. Replaces DSHBox's prebuilt `libdshbox-link.so` (`7d880b83…1973a`) | `400b9ba76c9f60b502a8add61e9c6e1bf3e5e3f3bebe7bf56c78012e482a1730` |
| `toolchain-cache/corepack-pnpm-11.1.2.tar.zst` (offline corepack cache of pnpm 11.1.2, MIT) | `app/src/main/assets/toolchain-cache/corepack-pnpm-11.1.2.tar.zst` | `d9f9677d89a9027df3f5c8d70922789a0c00cdd15b4dece8b36f0d07b0f02498` |
| `toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256` | `app/src/main/assets/toolchain-cache/corepack-pnpm-11.1.2.tar.zst.sha256` | `c1dcec1a5cf41a8bebdf87cab32ca127c9588772632b7b7f60d5202f07d57dba` |

Not taken: `xdg-open` and DSHBox's plugins (`browser-use`, mobile-pilot, mobile-adapt). The browser feature was dropped in PR-10.
