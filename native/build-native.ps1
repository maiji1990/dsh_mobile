#!/usr/bin/env pwsh
# Builds the native layer (Java facade + reused DSHBox modules), runs JVM unit tests,
# and exports release AARs with SHA-256 sidecars to artifacts/native/.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path (Join-Path $here '..\third_party\dshbox\settings.gradle.kts'))) {
    throw 'DSHBox submodule missing. Run: git submodule update --init --depth 1'
}
Push-Location $here
try {
    & .\gradlew.bat --no-daemon ':native-bridge:testDebugUnitTest' 'exportAars' @args
    if ($LASTEXITCODE -ne 0) { throw "gradle failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}
