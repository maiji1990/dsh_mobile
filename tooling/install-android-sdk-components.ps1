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

# sdkmanager refuses to start when JAVA_HOME points at a removed JDK (e.g. after a JDK update). Pick an installed
# JDK for THIS process only; the user/system JAVA_HOME setting is left untouched.
if (-not $env:JAVA_HOME -or -not (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
    $jdk = Get-ChildItem 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Java', 'C:\Program Files\Microsoft' -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^jdk-?(1[7-9]|2\d)' -and (Test-Path (Join-Path $_.FullName 'bin\java.exe')) } |
        Sort-Object Name -Descending | Select-Object -First 1
    if (-not $jdk) { throw "JAVA_HOME ($env:JAVA_HOME) is invalid and no JDK 17+ was found; install one or fix JAVA_HOME." }
    Write-Host "JAVA_HOME ($env:JAVA_HOME) is invalid; using $($jdk.FullName) for this run only."
    $env:JAVA_HOME = $jdk.FullName
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
