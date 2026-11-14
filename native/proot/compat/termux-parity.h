/*
 * Forced include for the PRoot objects (ADR-0014). termux-packages builds PRoot against a patched NDK sysroot
 * whose <stdio.h> sets P_tmpdir to the Termux prefix; the DSHBox libproot.so carries that default. Keeping it makes
 * this build a drop-in for the DSHBox binary. It is only a fallback: every DSH Mobile launch path sets
 * PROOT_TMP_DIR (and PROOT_LOADER), see ProotCommandBuilder / TerminalCommandBuilder and tests/TestVectors.
 */
#include <stdio.h>
#undef P_tmpdir
#define P_tmpdir "/data/data/com.termux/files/usr/tmp/"
