<#
.SYNOPSIS
    Copies the operator's DSH credential store onto a connected device (test setup only).

.DESCRIPTION
    The native client has no credential UI yet (PR-05); until then a device can only authenticate with
    a DSH credential store that already contains a usable ref. This script copies the local
    `%USERPROFILE%\.dsh\.credentials.yaml` into the app's DSH_HOME (files/user-data/.dsh) through
    `adb shell run-as` (the installed APK must be debuggable), backing up the previous file first.

    The secret never leaves the two files: it is passed to the device as base64 and is never printed.
#>
[CmdletBinding()]
param(
    [string]$Serial,
    [string]$Source = (Join-Path $env:USERPROFILE '.dsh\.credentials.yaml'),
    [string]$Package = 'io.github.maiji1990.dshmobile'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Source)) { throw "credential source not found: $Source" }

$adb = if ($Serial) { @('-s', $Serial, 'shell') } else { @('shell') }
$target = "/data/data/$Package/files/user-data/.dsh/.credentials.yaml"
$backup = "$target.bak"

# The APK must be debuggable for run-as to work at all.
& adb @adb run-as $Package id | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'run-as failed; the installed APK is not debuggable' }

& adb @adb run-as $Package cp -f $target $backup | Out-Null

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Source))
# One quoted -c script: the base64 payload has no spaces, so the device shell sees a single argument.
& adb @adb run-as $Package sh -c "'printf %s $b64 | base64 -d > $target'" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "credential upload failed ($LASTEXITCODE)" }
& adb @adb run-as $Package chmod 600 $target | Out-Null

$size = (& adb @adb run-as $Package stat -c %s $target | Out-String).Trim()
$uploaded = & adb @adb run-as $Package cat $target | Out-String
$refs = ([regex]::Matches($uploaded, '(?m)^  ([A-Za-z0-9_]+):')) | ForEach-Object { $_.Groups[1].Value }
"uploaded $size bytes; refs: $($refs -join ', '); backup: $backup"
