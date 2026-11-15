#!/usr/bin/env bash
# Builds this repo's native layer with the pinned NDK (no Gradle, no DSHBox since PR-10 / ADR-0018):
#   artifacts/native/dshmobile-native/<abi>/libdshmobile_native.so   (ADR-0007, own C source, 16 KB aligned)
#   artifacts/native/proot/<abi>/*.so + SHA256SUMS                   (ADR-0014, pinned upstream PRoot sources)
# Extra arguments go to native/proot/build.sh.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bash "$here/dshmobile-native/build.sh"
bash "$here/proot/build.sh" "$@"
