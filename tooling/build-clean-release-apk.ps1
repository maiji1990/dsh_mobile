<#
.SYNOPSIS
    发布前净构建并执行 APK 产物零业务 WebView 门禁检查（PR-09 / F27 防护）。
.DESCRIPTION
    清理 src/DshMobile.Android 的 obj/<Configuration> 与 bin/<Configuration>，消除增量构建在
    classes*.dex 中残留已删除 Java 调用包装类型（F27）的风险；随后构建 APK 并通过
    NoBusinessWebViewGateTests（DSHM_VERIFY_APK_PATH）校验最终签名 APK 产物。
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [switch]$NoRestore,

    [switch]$NoBuildTests,

    [switch]$VerifyOnly,

    [string]$ApkPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$androidProject = Join-Path $repoRoot "src\DshMobile.Android"
$agentTestsProject = Join-Path $repoRoot "tests\DshMobile.Agent.Tests"

if (-not $VerifyOnly) {
    foreach ($sub in @("obj", "bin")) {
        $targetDir = Join-Path $androidProject (Join-Path $sub $Configuration)
        if (Test-Path -LiteralPath $targetDir) {
            Write-Host "[clean] Removing $targetDir"
            Remove-Item -LiteralPath $targetDir -Recurse -Force
        }
    }

    $buildArgs = @("build", $androidProject, "-c", $Configuration)
    if ($NoRestore) {
        $buildArgs += "--no-restore"
    }

    Write-Host "[build] dotnet $($buildArgs -join ' ')"
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

if ([string]::IsNullOrWhiteSpace($ApkPath)) {
    $apkOutputDir = Join-Path $androidProject "bin\$Configuration\net11.0-android"
    if (-not (Test-Path -LiteralPath $apkOutputDir)) {
        throw "APK output directory not found: $apkOutputDir"
    }

    $signedApks = @(Get-ChildItem -LiteralPath $apkOutputDir -Filter "*-Signed.apk" -File)
    if ($signedApks.Count -eq 0) {
        throw "No *-Signed.apk found under $apkOutputDir"
    }
    if ($signedApks.Count -gt 1) {
        throw "Expected 1 *-Signed.apk under $apkOutputDir, found $($signedApks.Count): $($signedApks.FullName -join ', ')"
    }
    $ApkPath = $signedApks[0].FullName
}
else {
    $ApkPath = (Resolve-Path -LiteralPath $ApkPath).Path
}

$apkItem = Get-Item -LiteralPath $ApkPath
$apkSha256 = (Get-FileHash -LiteralPath $ApkPath -Algorithm SHA256).Hash
Write-Host "[apk]   $ApkPath ($($apkItem.Length) B, SHA-256: $apkSha256)"

$previousVerifyPath = $env:DSHM_VERIFY_APK_PATH
try {
    $env:DSHM_VERIFY_APK_PATH = $ApkPath
    $testArgs = @(
        "test", $agentTestsProject,
        "-c", $Configuration,
        "--filter", "FullyQualifiedName~NoBusinessWebViewGateTests"
    )
    if ($NoBuildTests) {
        $testArgs += @("--no-build", "--no-restore")
    }
    elseif ($NoRestore) {
        $testArgs += "--no-restore"
    }

    Write-Host "[gate]  dotnet $($testArgs -join ' ')"
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "NoBusinessWebViewGateTests APK verification failed with exit code $LASTEXITCODE"
    }
}
finally {
    if ($null -eq $previousVerifyPath) {
        Remove-Item Env:DSHM_VERIFY_APK_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:DSHM_VERIFY_APK_PATH = $previousVerifyPath
    }
}

Write-Host "[ok]    Clean $Configuration APK verified (zero business WebView)."
