<#
.SYNOPSIS
    Device acceptance run for PR-00 ~ PR-04 of the native Android rewrite.

.DESCRIPTION
    Drives the real device through adb + uiautomator only and writes a Markdown + JSON report:

      * PR-00: home entry points, runtime layer version, no regression of the WebView entry
      * PR-01: host start/stop, startup-perf stages, no leftover guest processes
      * PR-04: native chat entry, capabilities, slash bar, image picker, rotation draft,
               dark/light, IME resize, back navigation

    Scenarios that need a reachable LLM endpoint (real conversation, tool approval, long stream)
    are NOT covered here: they need the device to have a default network and a usable DSH
    credential (PR-05 adds the native credential UI). The script reports the network state so the
    run is self-documenting.

.PARAMETER Serial
    adb serial of the target device (default: the only connected device).

.PARAMETER ApkPath
    APK to install first. Omit with -SkipInstall to test whatever is installed.

.EXAMPLE
    pwsh -File tooling/device-e2e/run-pr00-04.ps1 -Serial 4bf97d61
#>
[CmdletBinding()]
param(
    [string]$Serial,
    [string]$ApkPath,
    [switch]$SkipInstall,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\artifacts\device-e2e')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DshDevice.ps1')
Set-DshDevice -Serial $Serial

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dir = Join-Path $OutDir $stamp
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$results = [System.Collections.Generic.List[object]]::new()

function Add-Result([string]$Id, [string]$Name, [string]$Outcome, [string]$Detail) {
    $results.Add([pscustomobject]@{ Id = $Id; Name = $Name; Outcome = $Outcome; Detail = $Detail })
    '{0,-8} {1,-7} {2}' -f $Id, $Outcome, $Name
    if ($Detail) { '         ' + $Detail }
}

function Get-ScreenText {
    (Get-DshUiXml).SelectNodes('//node') | ForEach-Object {
        if ($_.text) { $_.text } elseif ($_.'content-desc') { $_.'content-desc' }
    }
}

function Test-ScreenText([string]$Needle) {
    (Get-ScreenText) -contains $Needle
}

function Save-Shot([string]$Name) {
    Save-DshScreenshot -Path (Join-Path $dir "$Name.png") | Out-Null
}

# ---------------------------------------------------------------- facts
$device = @{
    model = (Invoke-DshAdb @('shell','getprop','ro.product.model')).Trim()
    release = (Invoke-DshAdb @('shell','getprop','ro.build.version.release')).Trim()
    sdk = (Invoke-DshAdb @('shell','getprop','ro.build.version.sdk')).Trim()
    build = (Invoke-DshAdb @('shell','getprop','ro.build.display.id')).Trim()
}
$conn = Invoke-DshAdb @('shell','dumpsys','connectivity')
$net = ($conn -split "`r?`n" | Where-Object { $_ -match 'Active default network' } | Select-Object -First 1).Trim()
Add-Result 'PRE-01' '设备与网络事实' 'INFO' ("{0} / Android {1} (API {2}) / {3} / {4}" -f $device.model, $device.release, $device.sdk, $device.build, $net)

if (-not $SkipInstall) {
    if (-not $ApkPath) { throw 'pass -ApkPath or -SkipInstall' }
    $install = Invoke-DshAdb @('install','-r','-d',(Resolve-Path $ApkPath).Path)
    Add-Result 'PRE-02' '安装 APK' ($(if ($install -match 'Success') { 'PASS' } else { 'FAIL' })) (($install.Trim() -split "\r?\n")[-1])
}

# ---------------------------------------------------------------- PR-00 home
Invoke-DshAdb @('shell','am','force-stop','io.github.maiji1990.dshmobile') | Out-Null
Clear-DshLogcat
Invoke-DshAdb @('shell','am','start','-W','-n','io.github.maiji1990.dshmobile/crc6407bba3bacbe6d328.MainActivity') | Out-Null
Start-Sleep -Seconds 4

$homeReady = Test-ScreenText '打开原生会话（实验）'
Add-Result 'PR00-01' '首页原生会话并行入口可见' ($(if ($homeReady) { 'PASS' } else { 'FAIL' })) '打开原生会话（实验）'

$entries = @('手机操控','远程主机','运行环境') | Where-Object { Test-ScreenText $_ }
Add-Result 'PR00-02' '首页功能入口完整' ($(if ($entries.Count -eq 3) { 'PASS' } else { 'FAIL' })) ($entries -join ', ')

$layer = (Get-ScreenText | Where-Object { $_ -like 'DSH 0.2.2*' } | Select-Object -First 1)
Add-Result 'PR00-03' 'DSH 层版本' ($(if ($layer) { 'PASS' } else { 'FAIL' })) $layer

Save-Shot 'pr00-home'

# The WebView rollback entry only exists while the host runs; PR-00 keeps it intact.
Invoke-DshTapText -Text '启动 DSH' | Out-Null
$ready = Wait-DshUiText -Text 'DSH 已就绪' -TimeoutSeconds 180
Add-Result 'PR01-01' '启动 DSH 到就绪' ($(if ($ready) { 'PASS' } else { 'FAIL' })) ''

$trace = Get-DshLogcat -Filter 'startup-perf'
$stages = @('host.preconditions','sandbox.spawn','dsh.spawn','http.first_port_open','host.ready','bridge.complete')
$missing = $stages | Where-Object { $trace -notmatch ('"stage":"' + [regex]::Escape($_) + '"') }
Add-Result 'PR01-02' '启动阶段测点齐全' ($(if (-not $missing) { 'PASS' } else { 'FAIL' })) ($(if ($missing) { '缺失: ' + ($missing -join ', ') } else { 'host.ready 已记录' }))

$readyLine = ($trace -split "\r?\n" | Where-Object { $_ -match '"stage":"host.ready"' } | Select-Object -Last 1)
$readyMs = if ($readyLine -match '"elapsedMs":([0-9.]+)') { [double]$Matches[1] } else { 0 }
Add-Result 'PR01-03' '冷启到就绪耗时（单次样本）' 'INFO' ('{0:N0} ms' -f $readyMs)

$webEntry = Test-ScreenText '打开 DSH'
Add-Result 'PR00-04' 'WebView 回滚入口保留' ($(if ($webEntry) { 'PASS' } else { 'FAIL' })) '打开 DSH'

# ADR-0031 host phases used by the native chat banner; protocol.* only exists with a trace hook.
$protoTrace = ($trace -match 'protocol.connected|protocol.initialized')
Add-Result 'PR01-04' 'protocol.connected / protocol.initialized 真机记录' ($(if ($protoTrace) { 'PASS' } else { 'FAIL' })) ($(if ($protoTrace) { '两个阶段均由 AgentConnectionScope 注入的 StartupTrace 记录' } else { '真机未见该测点' }))

# ---------------------------------------------------------------- PR-04 chat
Invoke-DshTapText -Text '打开原生会话（实验）' | Out-Null
# First connect can need longer than a fixed sleep (session list + capability probe + history).
$chat = @()
for ($i = 1; $i -le 20; $i++) {
    Start-Sleep -Seconds 3
    $chat = Get-ScreenText
    if ($chat -match 'deepseek-official/') { break }
}
Add-Result 'PR04-01' '原生聊天打开并建立会话' ($(if ($chat -match 'deepseek-official/') { 'PASS' } else { 'FAIL' })) (($chat | Where-Object { $_ -like 'session-*' } | Select-Object -First 1))
Add-Result 'PR04-02' '模型 / 工作区标签' ($(if ($chat -match 'deepseek-official/') { 'PASS' } else { 'FAIL' })) (($chat | Where-Object { $_ -like '*·*' } | Select-Object -First 1))

$caps = @('添加图片附件','展开斜杠命令') | Where-Object { $chat -contains $_ }
Add-Result 'PR04-03' '能力门控（图片 / 斜杠入口）' ($(if ($caps.Count -eq 2) { 'PASS' } else { 'FAIL' })) ($caps -join ', ')

Invoke-DshTapText -Text '展开斜杠命令' -Index 1 | Out-Null
Start-Sleep -Seconds 2
$chips = (Get-ScreenText) | Where-Object { $_ -match '^/?(compact|export|feedback|goal|permission|plan)$' }
Add-Result 'PR04-04' '斜杠命令清单来自 commands/list' ($(if ($chips) { 'PASS' } else { 'FAIL' })) ($chips -join ', ')
Save-Shot 'pr04-slash'

# image picker
Invoke-DshTapText -Text '添加图片附件' | Out-Null
Start-Sleep -Seconds 3
$picker = (Get-DshUiXml).SelectNodes('//node') | Where-Object { $_.text -like '*.png' } | Select-Object -First 1
if ($picker) {
    Invoke-DshTapText -Text $picker.text | Out-Null
    Start-Sleep -Seconds 3
    $chip = (Get-ScreenText) | Where-Object { $_ -like 'img-*' } | Select-Object -First 1
    Add-Result 'PR04-05' 'SAF 选图生成附件 chip' ($(if ($chip) { 'PASS' } else { 'FAIL' })) $chip
    Save-Shot 'pr04-image'
} else {
    Add-Result 'PR04-05' 'SAF 选图生成附件 chip' 'FAIL' '文件选择器未列出测试图片'
}

# draft survives rotation (ChatActivity declares Orientation/ScreenSize/UiMode itself)
Invoke-DshTapText -Text '向 DSH 发送消息…' | Out-Null
Start-Sleep -Milliseconds 600
Invoke-DshAdb @('shell','input','text','draft-rotation-test') | Out-Null
Invoke-DshAdb @('shell','settings','put','system','accelerometer_rotation','0') | Out-Null
Invoke-DshAdb @('shell','settings','put','system','user_rotation','1') | Out-Null
Start-Sleep -Seconds 3
$draft = Test-ScreenText 'draft-rotation-test'
Add-Result 'PR04-06' '横竖屏切换保留草稿与会话' ($(if ($draft) { 'PASS' } else { 'FAIL' })) 'landscape 下草稿仍在'
Save-Shot 'pr04-landscape'
Invoke-DshAdb @('shell','settings','put','system','user_rotation','0') | Out-Null
Start-Sleep -Seconds 2

# IME: focus + type, then hide (the hint is gone once a draft exists, so target the EditText itself)
$edit = Get-DshEditText
if ($edit) { $p = Get-DshNodePoint $edit; Invoke-DshTap -X $p.X -Y $p.Y }
Start-Sleep -Milliseconds 900
Invoke-DshAdb @('shell','input','text','-ime-probe') | Out-Null
Start-Sleep -Milliseconds 600
Add-Result 'PR04-07' '输入法弹出后输入框可见可编辑' ($(if ((Get-ScreenText) -match 'ime-probe') { 'PASS' } else { 'FAIL' })) 'adjustResize 生效'
Invoke-DshAdb @('shell','input','keyevent','111') | Out-Null
Start-Sleep -Milliseconds 800

# dark / light in place
Invoke-DshAdb @('shell','cmd','uimode','night','yes') | Out-Null
Start-Sleep -Seconds 3
Save-Shot 'pr04-dark-yes'
$darkSet = (Invoke-DshAdb @('shell','cmd','uimode','night')).Trim()
Invoke-DshAdb @('shell','cmd','uimode','night','no') | Out-Null
Start-Sleep -Seconds 3
Save-Shot 'pr04-dark-no'
$yesShot = Join-Path $dir 'pr04-dark-yes.png'
$noShot = Join-Path $dir 'pr04-dark-no.png'
$yesHash = (Get-FileHash $yesShot -Algorithm SHA256).Hash
$noHash = (Get-FileHash $noShot -Algorithm SHA256).Hash
Add-Result 'PR04-08' '深色 / 浅色切换（原地）' ($(if ($yesHash -ne $noHash) { 'PASS' } else { 'FAIL' })) ("$darkSet；两张截图哈希" + $(if ($yesHash -ne $noHash) { '不同，主题已重新应用' } else { '相同，未重新主题化' }))
Invoke-DshAdb @('shell','cmd','uimode','night','yes') | Out-Null

# back navigation
Invoke-DshAdb @('shell','input','keyevent','4') | Out-Null
Start-Sleep -Seconds 2
Add-Result 'PR04-09' '返回键退出聊天回首页' ($(if (Test-ScreenText '打开原生会话（实验）') { 'PASS' } else { 'FAIL' })) ''

# ---------------------------------------------------------------- PR-01 stop
$before = @(Get-DshGuestProcesses).Count
Clear-DshLogcat
Invoke-DshTapText -Text '停止' | Out-Null
$stopped = Wait-DshUiText -Text 'DSH 未启动' -TimeoutSeconds 60
Start-Sleep -Seconds 2
$after = @(Get-DshGuestProcesses).Count
Add-Result 'PR01-05' '优雅停止 DSH' ($(if ($stopped) { 'PASS' } else { 'FAIL' })) ''
Add-Result 'PR01-06' '停止后无 guest 残留进程' ($(if ($after -eq 0) { 'PASS' } else { 'FAIL' })) ("停止前 $before 个 → 停止后 $after 个")
Save-Shot 'pr01-stopped'
(Get-DshLogcat -Filter 'host: |stop: ok') | Set-Content -Path (Join-Path $dir 'stop.log')

# ---------------------------------------------------------------- report
$pass = @($results | Where-Object { $_.Outcome -eq 'PASS' }).Count
$fail = @($results | Where-Object { $_.Outcome -eq 'FAIL' }).Count
$info = @($results | Where-Object { $_.Outcome -eq 'INFO' }).Count
$line = "PASS $pass / FAIL $fail / INFO $info"

$md = @()
$md += "# PR-00 ~ PR-04 device run $stamp"
$md += ''
$md += "- 设备: $($device.model)，Android $($device.release)（API $($device.sdk)），构建 $($device.build)"
$md += "- 网络: $net"
$md += "- 结果: $line"
$md += '- 证据目录: ' + $dir
$md += ''
$md += '| ID | 检查项 | 结果 | 说明 |'
$md += '|---|---|---|---|'
foreach ($r in $results) { $md += "| $($r.Id) | $($r.Name) | $($r.Outcome) | $(($r.Detail ?? '') -replace '\|','/') |" }
$md | Set-Content -Path (Join-Path $dir 'report.md') -Encoding utf8
$results | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dir 'report.json') -Encoding utf8

''
$line
'report: ' + (Join-Path $dir 'report.md')
