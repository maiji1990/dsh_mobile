#!/usr/bin/env pwsh
# Builds this repo's native layer with the pinned NDK (no Gradle, no DSHBox since PR-10 / ADR-0018):
#   artifacts/native/dshmobile-native/<abi>/libdshmobile_native.so   (ADR-0007, own C source, 16 KB aligned)
#   artifacts/native/proot/<abi>/*.so + SHA256SUMS                   (ADR-0014, pinned upstream PRoot sources)
# NDK lookup: native/local.properties ndk.dir / sdk.dir, then ANDROID_NDK_HOME / ANDROID_HOME. Extra arguments go to
# native/proot/build.ps1 (-SourceCache, -Offline).
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $here 'dshmobile-native/build.ps1')
& (Join-Path $here 'proot/build.ps1') @args
