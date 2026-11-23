#requires -Version 7.0
<#
.SYNOPSIS
    Reusable adb / uiautomator helpers for the DSH Mobile on-device acceptance runs
    (docs/native/ACCEPTANCE_PR04.md, docs/plan/DSH_Mobile_原生Android重构任务清单.md).

.DESCRIPTION
    Pure helpers: no product code, no assumptions about which PR is being verified.
    Every function talks to the device through adb only, so the same file works for a
    physical phone or an emulator (pass -Serial to override the default device).
#>

$script:Adb = 'adb'
$script:Serial = $null
$script:UiDumpPath = '/data/local/tmp/dshmobile-ui.xml'

function Set-DshDevice {
    param([string]$Serial, [string]$AdbPath = 'adb')
    $script:Serial = $Serial
    $script:Adb = $AdbPath
}

function Invoke-DshAdb {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)
    $full = if ($script:Serial) { @('-s', $script:Serial) + $Arguments } else { $Arguments }
    $out = & $script:Adb @full 2>&1
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "adb $($Arguments -join ' ') failed ($LASTEXITCODE): $out"
    }
    return ($out | Out-String)
}

function Get-DshUiXml {
    # uiautomator occasionally answers 255 ("could not get idle state") while the UI animates;
    # retry a few times instead of failing an acceptance run on a transient.
    for ($i = 0; $i -lt 4; $i++) {
        $out = Invoke-DshAdb -Arguments @('shell', 'uiautomator', 'dump', '--compressed', $script:UiDumpPath) -AllowFailure
        if ($LASTEXITCODE -eq 0 -and $out -notmatch 'ERROR') {
            $raw = Invoke-DshAdb @('shell', 'cat', $script:UiDumpPath)
            if ($raw -match '<hierarchy') {
                try { return [xml]$raw } catch { }
            }
        }
        Start-Sleep -Milliseconds 900
    }
    throw 'uiautomator dump failed after retries'
}

function Get-DshUiNode {
    param([string]$Text, [string]$Id, [int]$Index = 0)
    $nodes = @((Get-DshUiXml).SelectNodes('//node') | Where-Object {
        $node = $_
        ($Text -eq $null -or $node.text -eq $Text -or $node.'content-desc' -eq $Text) -and
        ($Id -eq $null -or $node.'resource-id' -like "*$Id")
    })
    if ($nodes.Count -le $Index) { return $null }
    return $nodes[$Index]
}

function Get-DshEditText {
    $nodes = @((Get-DshUiXml).SelectNodes('//node') | Where-Object { $_.class -eq 'android.widget.EditText' })
    if ($nodes.Count -eq 0) { return $null }
    return $nodes[0]
}

function Get-DshNodePoint {
    param([Parameter(Mandatory)]$Node)
    $m = [regex]::Match($Node.bounds, '\[(\d+),(\d+)\]\[(\d+),(\d+)\]')
    if (-not $m.Success) { throw "node has no bounds: $($Node.bounds)" }
    return @{
        X = [int](([int]$m.Groups[1].Value + [int]$m.Groups[3].Value) / 2)
        Y = [int](([int]$m.Groups[2].Value + [int]$m.Groups[4].Value) / 2)
    }
}

function Invoke-DshTap {
    param([int]$X, [int]$Y)
    Invoke-DshAdb @('shell', 'input', 'tap', "$X", "$Y") | Out-Null
}

function Invoke-DshTapText {
    param([Parameter(Mandatory)][string]$Text, [string]$Id, [int]$Index = 0)
    $node = Get-DshUiNode -Text $Text -Id $Id -Index $Index
    if ($null -eq $node) { throw "on-screen text not found: $Text" }
    $p = Get-DshNodePoint $node
    Invoke-DshTap -X $p.X -Y $p.Y
    return $node
}

function Wait-DshUiText {
    param([Parameter(Mandatory)][string]$Text, [int]$TimeoutSeconds = 30, [string]$Id)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Get-DshUiNode -Text $Text -Id $Id) { return $true }
        Start-Sleep -Milliseconds 800
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Save-DshScreenshot {
    param([Parameter(Mandatory)][string]$Path)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Invoke-DshAdb @('shell', 'screencap', '-p', '/sdcard/dshmobile-shot.png') | Out-Null
    $null = Invoke-DshAdb @('pull', '/sdcard/dshmobile-shot.png', $Path)
    return $Path
}

function Clear-DshLogcat {
    Invoke-DshAdb @('logcat', '-c') | Out-Null
}

function Get-DshLogcat {
    param([string]$Filter = '')
    $lines = Invoke-DshAdb @('logcat', '-d')
    if ($Filter) { return ($lines -split "\r?\n" | Where-Object { $_ -match $Filter }) -join "`n" }
    return $lines
}

function Wait-DshLog {
    param([Parameter(Mandatory)][string]$Pattern, [int]$TimeoutSeconds = 60)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ((Get-DshLogcat -Filter $Pattern).Trim()) { return $true }
        Start-Sleep -Milliseconds 1000
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Get-DshAppProcess {
    Invoke-DshAdb @('shell', 'ps', '-A', '-o', 'PID,PPID,NAME') |
        Select-String -Pattern 'io\.github\.maiji1990\.dshmobile'
}

function Get-DshGuestProcesses {
    Invoke-DshAdb @('shell', 'ps', '-A', '-o', 'PID,PPID,NAME') |
        Select-String -Pattern 'libproot|proot-loader|node'
}
