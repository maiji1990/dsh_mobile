# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · Relay 执行面裁决与执行配额影子（PR-07 第四段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `7e83811`（#18）+ 未提交的 PR-07 第四段改动。
运行时：沙箱 / DSH 未启动。无障碍：本 App 服务未开启（`a11y=False`）。Shizuku：未安装。

操作：「手机操控（实验）」→「开启中继」→「协议信封比对（开发者）」→「执行面偏好（开发者）」切到「优先后台」→「关闭中继」→「开启中继」→
「协议信封比对」→「执行面偏好」切回「前台」→「关闭中继」→「开启中继」→「协议信封比对」→「关闭中继」。证据为 `adb logcat -s DshMobile:I`。

## 影子做什么

`automation.relay-surface` 是**只读**比对：不写信箱、不发请求、不弹审批。

- 旧侧：Relay 启动时把每条已注册能力的执行面裁决发布到 `files/pilot/capabilities.json`（上游 `RelayContainer.capabilitiesJson`，
  每行来自 `capabilityRows()` 里的 `policy.decide`）。比对用到的字段：`surfaces`、`implemented`、`usable`、`surface`、`degradeReason`，
  以及 `backend.shizuku`（`running` / `bound` / `trustedDisplay.alive` / `displayId`）。
- C# 侧：用同一组环境事实（Relay 自己存的 `relay` 偏好里的 `surface_preference`、`Build.VERSION.SdkInt`、上面这份文件里的 Shizuku 与可信屏状态）
  跑 `SurfacePolicy.For(preference, facts).Decide(id)` 与 `RelayBackendSupport.IsImplemented`。
- 判定：声明的执行面、`implemented`、`degradeReason`（需要指定模式时为 `E_SURFACE_MODE_REQUIRED`，否则是裁决的 reason）必须相等；
  Relay 标 `usable` 的行再比 `surface`；Relay 不可用的行不得写 `surface`。Shizuku 在跑但未绑定、文件里又记着屏号时，可信屏是否存活无从判断，
  报 Inconclusive。

「执行面偏好（开发者）」只写 Relay 自己的偏好（上游界面写的同一个键），不授予任何东西：没有 Shizuku 与可信虚拟屏时，「优先后台」会退回前台并注明原因。
测试结束后已切回「前台」，中继已关闭。

## 结果

```text
14:08:06.735 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (…))
14:08:06.942 shadow automation.relay-admission: Agree (12/12 answers byte-identical (E_NOT_IMPLEMENTED ×2, E_TRANSPORT_MALFORMED ×8, E_GATE_HOST_DENIED ×2))
14:08:06.944 shadow automation.relay-surface: Agree (2/2 rows agree (no Shizuku; surface compared on 0/2 rows (the others are not usable now, e.g. accessibility off)): ui.snapshot→foreground, ui.node→foreground [preference FOREGROUND, sdk 37])
14:08:20.507 shadow automation.ui-tools: Agree (after relay.stop: …)
14:08:23.221 relay.start: phase=STARTING channel=False a11y=False
14:08:29.122 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (…))
14:08:29.327 shadow automation.relay-admission: Agree (12/12 answers byte-identical (…))
14:08:29.327 shadow automation.relay-surface: Agree (2/2 rows agree (no Shizuku; surface compared on 0/2 rows (…)): ui.snapshot→foreground (from trusted-display: E_SURFACE_NO_SHELL), ui.node→foreground (from trusted-display: E_SURFACE_NO_SHELL) [preference BACKGROUND_PREFERRED, sdk 37])
14:08:42.932 relay surface preference -> FOREGROUND (developer)
14:08:52.220 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (…))
14:08:52.424 shadow automation.relay-admission: Agree (12/12 answers byte-identical (…))
14:08:52.425 shadow automation.relay-surface: Agree (2/2 rows agree (…): ui.snapshot→foreground, ui.node→foreground [preference FOREGROUND, sdk 37])
```

- 前台偏好（默认）：两次 **Agree**，两条能力都落前台、无降级原因。
- 优先后台：Relay 发布的 `degradeReason` 为 `E_SURFACE_NO_SHELL`（没有 Shizuku 就没有可信屏），C# 裁决为「从 trusted-display 退回 foreground，原因 E_SURFACE_NO_SHELL」，**Agree**。
  这说明旧 Relay 确实按存储的偏好裁决，且 C# 的降级链、拒绝原因的先后（API 级别先于 shell）与之一致。
- 三轮里第二、三段探针继续 **16/16 + 12/12 逐字节一致**；`dumpsys notification` 无本 App 通知；日志里没有审批 / 确认记录。

## 未覆盖

- `surface` 字段：无障碍未开启时两行都不可用，Relay 不写 `surface`（0/2 行比对了落点）。开启无障碍须由用户本人在系统设置里完成，本次未做。
- 可信屏路径（`trusted-display` 落点、`E_SURFACE_MODE_REQUIRED`）、旧 Android（API 级别拒绝）：只有向量（新旧双跑）。这台手机没有 Shizuku。
- 后端支持矩阵（三个后端的 `supports()`）：上游后端构造要 Android `Context`，JVM 单测造不出来，矩阵只在 C# 向量里锁定；
  真机只验证到已注册的 `ui.snapshot` / `ui.node` 两行（`implemented` 与降级原因）。
- 执行配额与两条审批前拒绝（需要指定模式、未实现）：真机上要发能通过准入的请求才能触发，会进入闸门 / 审批，探针刻意不发；
  只有向量（新旧双跑）+ 上游源码契约扫描。
