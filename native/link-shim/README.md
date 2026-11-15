# link-shim (`libdshbox-link.so`)

An `LD_PRELOAD` shim that turns `link(2)` / `linkat(2)` into a copy when the real call fails. Android's app data
partition refuses hard links, and dpkg / pnpm use them. The target is the guest's Debian (aarch64, **glibc**), so it
cannot be built with the Android NDK.

The source and build scripts are copied unmodified from DSHBox `v1.5.1` (`7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4`,
`plugin-manager/dshbox-plugins/tools/link-shim/`, GPL-3.0-or-later) and kept here for traceability (AGENTS.md rule 2,
ADR-0018). The shipped binary, `src/DshMobile.Android/Assets/dshbox/libdshbox-link.so`, is DSHBox's prebuilt one
(SHA-256 `7d880b83…1973a`). It is **not** rebuilt by `native/build-native.*`.

To rebuild, follow the header of `build_link_shim.sh`: WSL2 with an arm64 Debian rootfs and `qemu-aarch64-static`, then
`gcc -shared -fPIC -O2 -s … -ldl`. The output is not bit-for-bit reproducible across compilers. Replace the asset only
together with a guest check and an update of `src/DshMobile.Android/Assets/README.md`.
