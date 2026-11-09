#!/usr/bin/env bash
# Builds the native layer (Java facade + reused DSHBox modules), runs JVM unit tests,
# and exports release AARs with SHA-256 sidecars to artifacts/native/.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ ! -f "$here/../third_party/dshbox/settings.gradle.kts" ]]; then
  echo "DSHBox submodule missing. Run: git submodule update --init --depth 1" >&2
  exit 1
fi
cd "$here"
./gradlew --no-daemon :native-bridge:testDebugUnitTest exportAars "$@"
