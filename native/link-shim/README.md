# link-shim (`libdshmobile-link.so`)

An `LD_PRELOAD` shim that turns `link(2)` / `linkat(2)` into a copy when the real call fails. Android's app data
partition refuses hard links, and dpkg / pnpm use them. The target is the guest's Debian (aarch64, **glibc**), so it
cannot be built with the Android NDK.

`libdshmobile-link.c` is DSHBox `v1.5.1` (`7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4`,
`plugin-manager/dshbox-plugins/tools/link-shim/libdshbox-link.c`, GPL-3.0-or-later) with the file name and the disable
switch renamed (`DSHMOBILE_LINK_SHIM_DISABLE=1`, ADR-0019); the interception logic is unchanged. DSHBox's chroot build
scripts were replaced by `build.sh` (cross-compiles on x86_64 Linux / WSL, native gcc on aarch64).

```bash
bash native/link-shim/build.sh   # -> artifacts/native/link-shim/libdshmobile-link.so + SHA-256
```

The shipped asset `src/DshMobile.Android/Assets/dshmobile/libdshmobile-link.so` is pinned by SHA-256 (the bytes depend on
the compiler version), so `native/build-native.*` does not rebuild it. Replace it only together with a guest check and an
update of `src/DshMobile.Android/Assets/README.md` and `UpstreamPinnedValuesTests`.
