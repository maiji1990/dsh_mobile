#Requires -Version 7
<#
.SYNOPSIS
  Installs the Android SDK components needed to build DSHBox v1.5.1 from source (TASK-001 baseline):
  platforms;android-37.2 and build-tools;36.0.0.

.DESCRIPTION
  Run from an ELEVATED (administrator) PowerShell 7 when the SDK lives under "Program Files".
  License prompts are shown to you; nothing is auto-accepted.

.EXAMPLE
  pwsh -File tooling/install-android-sdk-components.ps1
.EXAMPLE
  # Through a local HTTP proxy (only if the direct connection fails):
  pwsh -File tooling/install-android-sdk-components.ps1 -ProxyHost 127.0.0.1 -ProxyPort 7897
#>
param(
    [string]$SdkRoot = $(if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'C:\Program Files (x86)\Android\android-sdk' }),
    [string[]]$Packages = @('platforms;android-37.2', 'build-tools;36.0.0'),
    [string]$ProxyHost,
    [int]$ProxyPort
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$underProgramFiles = ($SdkRoot -like "$env:ProgramFiles*") -or ($SdkRoot -like "${env:ProgramFiles(x86)}*")
if ($underProgramFiles -and -not $isAdmin) {
    throw "SDK is under Program Files ($SdkRoot). Re-run this script from an administrator PowerShell."
}

$sdkmanager = Join-Path $SdkRoot 'cmdline-tools\latest\bin\sdkmanager.bat'
if (-not (Test-Path $sdkmanager)) { throw "sdkmanager not found: $sdkmanager" }

$sdkArgs = @("--sdk_root=$SdkRoot")
if ($ProxyHost) {
    $sdkArgs += @('--proxy=http', "--proxy_host=$ProxyHost", "--proxy_port=$ProxyPort")
}

Write-Host "SDK root : $SdkRoot"
Write-Host "Packages : $($Packages -join ', ')"
& $sdkmanager @sdkArgs --install @Packages
if ($LASTEXITCODE -ne 0) { throw "sdkmanager failed with exit code $LASTEXITCODE" }

$missing = @()
foreach ($p in $Packages) {
    $dir = Join-Path $SdkRoot ($p -replace ';', '\')
    if (Test-Path $dir) { Write-Host "OK      $p -> $dir" } else { Write-Host "MISSING $p"; $missing += $p }
}
if ($missing) { throw "Not installed: $($missing -join ', ')" }
Write-Host 'Done. Tell Claude to continue TASK-001.'
