#!/usr/bin/env pwsh
# Builds the native layer (Java facade + reused DSHBox modules), runs JVM unit tests,
# and exports release AARs with SHA-256 sidecars to artifacts/native/.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path (Join-Path $here '..\third_party\dshbox\settings.gradle.kts'))) {
    throw 'DSHBox submodule missing. Run: git submodule update --init --depth 1'
}

# ndk-build (DSHBox terminal-emulator) rejects NDK paths containing spaces, e.g. the default
# 'C:\Program Files (x86)\Android\android-sdk'. If local.properties has no ndk.dir and the pinned NDK lives
# under such a path, point ndk.dir at a space-free directory junction (no admin rights needed).
$ndkVersion = '27.0.12077973'
$localProps = Join-Path $here 'local.properties'
if ((Test-Path $localProps) -and -not (Select-String -Path $localProps -Pattern '^ndk\.dir=' -Quiet)) {
    $sdkLine = Select-String -Path $localProps -Pattern '^sdk\.dir=(.+)$' | Select-Object -First 1
    $sdkDir = if ($sdkLine) { $sdkLine.Matches[0].Groups[1].Value.Replace('\:', ':').Replace('\\', '\') } else { $env:ANDROID_HOME }
    $ndkDir = Join-Path $sdkDir (Join-Path 'ndk' $ndkVersion)
    if ((Test-Path $ndkDir) -and $ndkDir.Contains(' ')) {
        $link = Join-Path $here (Join-Path '.generated' "ndk-$ndkVersion")
        if (-not (Test-Path $link)) {
            New-Item -ItemType Directory -Force (Split-Path $link) | Out-Null
            New-Item -ItemType Junction -Path $link -Target $ndkDir | Out-Null
        }
        Add-Content $localProps ('ndk.dir=' + $link.Replace('\', '/'))
        Write-Host "ndk.dir -> $link (space-free junction to $ndkDir)"
    }
}

Push-Location $here
try {
    & .\gradlew.bat --no-daemon ':native-bridge:testDebugUnitTest' 'exportAars' @args
    if ($LASTEXITCODE -ne 0) { throw "gradle failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

# ADR-0007 minimal native layer (own C source, NDK, 16 KB aligned): artifacts/native/dshmobile-native/<abi>/
& (Join-Path $here 'dshmobile-native/build.ps1')
# ADR-0014 PRoot / loader / talloc / shmem from pinned sources: artifacts/native/proot/<abi>/ (used with DshProotSource=self)
& (Join-Path $here 'proot/build.ps1')
