#!/usr/bin/env pwsh
# Downloads the runtime layers pinned in runtime-assets.lock, verifies SHA-256, and stages them as
#   artifacts/runtime/android-assets/runtime/android-side.tar.zst(+.sha256)   -> APK assets/runtime/
#   artifacts/runtime/android-assets/dsh/<version>.tar.zst(+.sha256)           -> APK assets/dsh/
#   artifacts/runtime/downloads/dshapp-runtime-*.zip                           -> imported on device (base/node)
# Nothing here is committed (artifacts/runtime/ is gitignored).
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'artifacts/runtime'
$downloads = Join-Path $out 'downloads'
$assets = Join-Path $out 'android-assets'
New-Item -ItemType Directory -Force $downloads, (Join-Path $assets 'runtime'), (Join-Path $assets 'dsh') | Out-Null

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

$lock = Get-Content (Join-Path $PSScriptRoot 'runtime-assets.lock') | Where-Object { $_ -and -not $_.StartsWith('#') }
foreach ($line in $lock) {
    $name, $sha, $url = $line -split '\s+'
    $file = Join-Path $downloads $name
    if ($Force -or -not (Test-Path $file) -or (Get-Sha256 $file) -ne $sha) {
        for ($i = 1; $i -le 6; $i++) {
            # curl handles resume and the local proxy more reliably than Invoke-WebRequest here.
            & curl.exe -sSfL --ssl-no-revoke --retry 3 -C - --max-time 1800 -o $file $url
            if ($LASTEXITCODE -eq 0) { break }
            Write-Warning "download attempt $i failed for $name"
            Start-Sleep -Seconds 5
        }
    }
    $got = Get-Sha256 $file
    if ($got -ne $sha) { throw "SHA-256 mismatch for ${name}: got $got, want $sha" }
    Write-Host "verified $name"

    if ($name -like '*.tar.zst') {
        # DSH layer: shipped in the APK under assets/dsh/ with a "<sha>  <file>" sidecar.
        Copy-Item $file (Join-Path $assets "dsh/$name") -Force
        "$sha  $name" | Set-Content -NoNewline -Encoding ascii (Join-Path $assets "dsh/$name.sha256")
    } elseif ($name -like 'dshapp-runtime-*.zip') {
        # Only android-side goes into the APK (DSHBox >= 1.4 layout); base/node are imported on device.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($file)
        try {
            foreach ($entry in 'android-side.tar.zst', 'android-side.tar.zst.sha256') {
                $e = $zip.GetEntry($entry) ?? (throw "missing $entry in $name")
                [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $assets "runtime/$entry"), $true)
            }
        } finally { $zip.Dispose() }
        $sidecar = ((Get-Content (Join-Path $assets 'runtime/android-side.tar.zst.sha256') -Raw).Trim() -split '\s+')[0]
        $layer = Get-Sha256 (Join-Path $assets 'runtime/android-side.tar.zst')
        if ($sidecar -ne $layer) { throw "android-side sidecar mismatch: $sidecar vs $layer" }
        Write-Host "staged android-side ($layer)"
    }
}
Write-Host "runtime assets ready in $assets"
