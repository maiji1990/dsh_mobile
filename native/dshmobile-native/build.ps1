# Builds libdshmobile_native.so (ADR-0007 minimal native layer) from src/dshm_native.c with the pinned NDK, for the
# APK's ABIs, 16 KB page aligned. Output: artifacts/native/dshmobile-native/<abi>/libdshmobile_native.so.
# Called by native/build-native.ps1; can run alone. NDK: native/local.properties ndk.dir, else ANDROID_NDK_HOME, else
# <sdk.dir>/ndk/<pinned version>. Fails (never skips) when the NDK or a check is missing.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here '..\..')
$ndkVersion = '27.0.12077973'
$api = 29   # = SupportedOSPlatformVersion of src/DshMobile.Android

function Get-Prop([string]$file, [string]$key) {
    if (-not (Test-Path $file)) { return $null }
    $line = Get-Content $file | Where-Object { $_ -match "^$([regex]::Escape($key))=" } | Select-Object -First 1
    if ($line) { return ($line -replace "^$([regex]::Escape($key))=", '') -replace '\\:', ':' -replace '\\\\', '\' }
    return $null
}

$props = Join-Path $repo 'native\local.properties'
$ndk = Get-Prop $props 'ndk.dir'
if (-not $ndk) { $ndk = $env:ANDROID_NDK_HOME }
if (-not $ndk) {
    $sdk = Get-Prop $props 'sdk.dir'
    if (-not $sdk) { $sdk = $env:ANDROID_HOME }
    if ($sdk) { $ndk = Join-Path $sdk "ndk\$ndkVersion" }
}
if (-not $ndk -or -not (Test-Path $ndk)) { throw "NDK $ndkVersion not found (ndk.dir / ANDROID_NDK_HOME / sdk.dir)" }

$hostTag = if ($IsWindows -or $env:OS -eq 'Windows_NT') { 'windows-x86_64' } elseif ($IsMacOS) { 'darwin-x86_64' } else { 'linux-x86_64' }
$bin = Join-Path $ndk "toolchains\llvm\prebuilt\$hostTag\bin"
$exe = if ($hostTag -eq 'windows-x86_64') { '.exe' } else { '' }
$clang = Join-Path $bin "clang$exe"
$readelf = Join-Path $bin "llvm-readelf$exe"
foreach ($t in @($clang, $readelf)) { if (-not (Test-Path $t)) { throw "missing NDK tool: $t" } }

$src = Join-Path $here 'src\dshm_native.c'
$exports = 'dshm_abi_version', 'dshm_pty_spawn', 'dshm_pty_resize', 'dshm_read', 'dshm_write', 'dshm_close', 'dshm_wait', 'dshm_kill'
$abis = @{ 'arm64-v8a' = 'aarch64-linux-android'; 'x86_64' = 'x86_64-linux-android' }

foreach ($abi in $abis.Keys) {
    $outDir = Join-Path $repo "artifacts\native\dshmobile-native\$abi"
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $out = Join-Path $outDir 'libdshmobile_native.so'
    & $clang "--target=$($abis[$abi])$api" -std=c11 -O2 -fPIC -shared -fvisibility=hidden `
        -Wall -Wextra -Werror -fstack-protector-strong -D_FORTIFY_SOURCE=2 `
        '-Wl,-z,max-page-size=16384' '-Wl,-z,common-page-size=16384' '-Wl,-z,relro' '-Wl,-z,now' '-Wl,-z,noexecstack' `
        '-Wl,--build-id=sha1' '-Wl,-soname,libdshmobile_native.so' -o $out $src
    if ($LASTEXITCODE -ne 0) { throw "clang failed for $abi" }

    # 16 KB page devices (Android 15+) refuse libraries whose LOAD segments are aligned below 0x4000.
    $segments = & $readelf -lW $out | Where-Object { $_ -match '^\s+LOAD\s' }
    if (-not $segments) { throw "${abi}: no LOAD segments?" }
    foreach ($s in $segments) {
        $align = [Convert]::ToInt64(($s.Trim() -split '\s+')[-1], 16)
        if ($align -lt 0x4000) { throw "${abi}: LOAD segment aligned to 0x$([Convert]::ToString($align, 16)) (< 16 KB): $s" }
    }
    $dyn = (& $readelf --dyn-syms -W $out) -join "`n"
    foreach ($e in $exports) { if ($dyn -notmatch "\s$e(\s|$)") { throw "${abi}: export $e missing" } }
    $hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "libdshmobile_native.so [$abi] 16 KB aligned, $($exports.Count) exports, sha256 $hash"
}
