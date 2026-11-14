# 2026-11-13/14 · Honor PTP-AN10 · Android 17 (API 37) · 双中继比对（PR-07 写路径 / Direct / Shizuku / 宿主）真机记录

设备：PTP-AN10，`AN6B024B01054420`，Shizuku `13.6.0.r1086`。构建：Debug（`dotnet build -t:Install`，FastDev），
基于 `a8bed9b`（第八段）+ 未提交的写路径 / Direct / Shizuku / 宿主改动。主 App `org.example.dshmobile`（uid 10267），
测试目标 `org.example.dshmobile.testtarget`（`tests/DshMobile.TestTarget`，Material 3 控件，不发布）。

前置条件由用户本人完成：在系统设置里开启本 App 的（旧）无障碍服务、在 Shizuku 里授权、点「开启中继」「启动 C# 中继」
「双中继比对（开发者，需测试目标应用）」，并逐张回答两边的审批卡（旧 Relay 的卡；C# 宿主的通知卡 → 应用内确认页）。
App 与主机都没有代为开启服务、代为授权或回答审批；主机端只用 `run-as` / `dumpsys` / `logcat` / `screencap` 观察，
没有执行 `uiautomator dump`。

## 比对方式

`RelayHostShadowCheck`（`MigrationOptions.RelayHost`，默认 LegacyWithShadow）：16 条调用逐条先发旧信箱
（`files/pilot/run`），再发 C# 宿主自己的信箱（`files/pilot-cs/run`，不绑进 guest），各自走自己的闸门与审批；
`E_AWAITING_CONSENT` / `E_GATE_WAITING_TURN` 时换 id 重发。回包去掉 `id` / `elapsedMs` / `waitedMs` / `durationMs` /
`readBackRounds`（计时与轮询次数），`/opt/pilot-cs` 写成 `/opt/pilot` 后逐字节比；`screen.capture` / `notify.post`
只比 ok / 错误码与 data 键集合；联系人 / 日历的不一致只报哈希。依赖屏幕状态的三条（`ui.setValue` / `ui.setProgress` /
`ui.scroll`）每边执行前先由该边点测试目标的 `dshm-reset`。`ui.tap` / `ui.swipe` 的坐标取自 `ui.node` 回包的节点中心。

## 结果（四次运行）

```text
11-13 17:52  Inconclusive  14/16 相等；重装后无障碍服务被系统关闭 → 所有 ui.* 两边同为 E_GATE_SYSTEM_MISSING，tap/swipe 跳过
11-13 17:58  Diverge       12 相等，4 不同：
             screen.capture  旧 ok | C# E_STORAGE_FULL（"quota exhausted want=14336000"）  ← 真实缺陷，已修
             ui.setValue     C# 看到旧 Relay 刚输入的文字（顺序伪差）                       ← 比对改为每边先复位
             ui.setProgress  旧已置 50%，C# 再置同值被 SeekBar 拒绝（顺序伪差）            ← 同上
             ui.tap          只有 durationMs 65 vs 64                                     ← 计时字段不比
             （ui.scroll 两边同为 E_TRANSPORT_MALFORMED：比对参数写成 down，应为 forward，已改）
11-13 18:08  Diverge       15 相等，1 不同：ui.setProgress 差异落在 300 字摘录之外        ← 报告改为从首个差异处摘录
11-14 08:03  Diverge       15 相等，1 不同：app.launch
             旧 "the launch was accepted but another app is confirmed to hold the front: ... foreground is com.android.systemui"
             C# {"package":"org.example.dshmobile.testtarget","verified":true}
```

最后一次的 `app.launch` 差异是环境所致：旧 Relay 校验前台时用户正下拉通知栏回答审批卡（前台为 `com.android.systemui`），
C# 稍后执行时通知栏已收起；两边是同一套前台确认逻辑，前两次运行（17:58、18:08）`app.launch` 均相等。
`ui.setProgress` 在最后一次相等，确认 18:08 的差异是 `readBackRounds`（读回轮数，取决于时刻）。

逐项覆盖（最后两次合计，均相等）：`app.launch`、`ui.snapshot`、`ui.node`、`ui.waitFor`、`ui.click`、`ui.setValue`、
`ui.setProgress`、`ui.scroll`（节点动作）；`ui.tap`、`ui.swipe`（坐标动作）；`screen.capture`（无障碍截图 + 存储配额 / 发布）；
`pkg.query`、`notify.post`（Direct）；`app.stop`（Shizuku：两边日志均为 `backend=shizuku result=ok`，C# 经仓库内
`dshmobile.relay.ShellService` 用户服务）；`contact.read` / `cal.read`（两边同为 `E_GATE_SYSTEM_MISSING`，本 App 未获权限）。

## 真机发现并修复的缺陷

`StorageReaper` 默认的可用空间探测用 `DriveInfo(Path.GetPathRoot(dir))`：在 Android 上根是 `/`（只读系统分区，几乎无剩余），
导致 C# 宿主所有产物预留都报 `quota exhausted`。上游是 `StatFs(dir)`。修复为 Unix 上直接 statvfs 目录本身
（`StorageReaper.VolumeAvailableBytes`），并加回归测试 `Default_free_space_probe_measures_the_directorys_volume`。

## 未覆盖

- 联系人 / 日历 / 位置 / 媒体 / 录音 / 录屏 / 剪贴板写入 / `sys.shell` / `app.install` / `appops.set` / 虚拟屏：只有单元测试
  （RelayDirectTests、RelayShizukuTests），真机两边都未获对应权限或未纳入比对（避免改动用户数据 / 系统设置）。
- 写操作仍由旧 Relay 对 guest 服务；C# 宿主只服务自己的信箱。C# 自己的无障碍服务（`DshAccessibilityService`）未在本次开启，
  C# 宿主经 `RelayA11yTreeSource.Current()` 使用同进程内旧服务实例。
- 审批的通知卡 → 确认页路径在 C# 宿主上走通（每项能力两边各一张卡），应用内卡片（`UiInForeground`）未接入 C# 宿主。
