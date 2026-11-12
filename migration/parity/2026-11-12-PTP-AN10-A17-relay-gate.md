# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · Relay 审批闸门判定影子（PR-07 第五段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `f024599`（第四段）+ 未提交的 PR-07 第五段改动。
运行时：沙箱 / DSH 未启动。无障碍：本 App 服务**未**在系统设置中列出（`enabled_accessibility_services` 只有其他应用）；通知监听同样未列出。Shizuku：未安装。

操作：「手机操控（实验）」→「开启中继」→「协议信封比对（开发者）」→ 等 65 s（避开 Relay 每分钟入口计数）→「协议信封比对」→「关闭中继」。
证据为按钮输出区的文字（`uiautomator dump`）与 `adb logcat -s DshMobile:I`（这台 ROM 日志缓冲滚得快，只留住了第二轮）。

## 影子做什么

`automation.relay-gate` 有两半：

- **被动（只读）**：读 Relay 发布的 `files/pilot/capabilities.json`，每行的 `systemPermission`（Relay 在裁决路线上探测的系统态）、
  `assistantTier`（`relay` 偏好里的档位）、`ceiling` 与 C# `RelaySystemProbe` / `RelayTierStore` / `RelayGatePolicy` 从同一组事实算出的值必须相等；
  C# 闸门会拒的行（系统态阻断、档位 DENIED、需要指定模式）Relay 不得标 `usable`。
  C# 直接从 Android 读：SDK、Settings 里的无障碍 / 通知监听名单、能力表涉及的运行时权限、麦克风 app-op；
  Shell 与可信屏取自同一文件的 `backend.shizuku`。「服务此刻是否已绑定」只有旧进程知道：只有绑定才能解释的答案两种都接受并在详情里列出（本次没有这种行）。
- **活体**：仅当 ①本 App 无障碍未在设置中列出、②Relay 自己对 `ui.snapshot` / `ui.node` 发布 `MANUAL_ONLY`、③两者档位都不是 ALWAYS、
  ④C# 整链（信封 → 准入 → 执行面 → 系统态探测 → 闸门）预测 `E_GATE_SYSTEM_MISSING/system_missing` 时，才往信箱写这两条只读请求（`ttlMs 5000`），
  回包除 `elapsedMs` 外逐字节比对。拒绝发生在任何确认框与后端之前；就算误放行，闸门只剩约 2.25 s 预算，低于确认框所需的 5 s，也不会弹给用户。
  Relay 对每次拒绝写一条审计记录。

## 结果

第一轮（按钮输出区，15:05 左右）与第二轮（logcat）完全一致：

```text
15:07:18.835 shadow automation.relay-admission: Agree (12/12 answers byte-identical (E_NOT_IMPLEMENTED ×2, E_TRANSPORT_MALFORMED ×8, E_GATE_HOST_DENIED ×2))
15:07:18.836 shadow automation.relay-surface: Agree (2/2 rows agree (no Shizuku; surface compared on 0/2 rows (the others are not usable now, e.g. accessibility off)): ui.snapshot→foreground, ui.node→foreground [preference FOREGROUND, sdk 37])
15:07:18.836 shadow automation.relay-gate: Agree (2/2 rows agree on system state, tier and ceiling (states MANUAL_ONLY ×2; tiers ASK ×2) [sdk 37, accessibility not listed, listener not listed, 1 permissions granted, microphone blocked])
15:07:19.039 shadow automation.relay-gate: Agree (live probe: 2/2 answers byte-identical (E_GATE_SYSTEM_MISSING/system_missing, before any prompt))
15:07:37.532 relay.stop: phase=STOPPED channel=False a11y=False
15:07:37.532 shadow automation.ui-tools: Agree (after relay.stop: registered=False conflict=False a11y=False, static facts equal)
```

第二段信封比对两轮也都是 16/16 Agree。两轮之间与之后 `dumpsys notification` 中本 App 只有中继前台服务那条静默通知，**没有审批通知**。测试结束中继已关闭。

## 第二组：用户开启无障碍之后（15:12）

用户自己在系统设置里开启了本 App 的无障碍服务（`enabled_accessibility_services` 含 `org.example.dshmobile/interlock.relay.core.exec.a11y.RelayAccessibilityService`），
并在后台运行 Shizuku（`shizuku_server` 进程在）；执行面偏好此时为「优先后台」。操作：「开启中继」→「协议信封比对」→「关闭中继」。

```text
15:12:48.757 relay.start: phase=STARTING channel=False a11y=True
15:12:48.758 shadow automation.ui-tools: Agree (after relay.start: registered=True conflict=False a11y=True, layer byte-identical, static facts equal)
15:13:02.770 shadow automation.relay-envelope: Agree (16/16 answers byte-identical (…))
15:13:02.974 shadow automation.relay-admission: Agree (12/12 answers byte-identical (E_NOT_IMPLEMENTED ×2, E_TRANSPORT_MALFORMED ×8, E_GATE_HOST_DENIED ×2))
15:13:02.975 shadow automation.relay-surface: Agree (2/2 rows agree (no Shizuku; surface compared on every row): ui.snapshot→foreground (from trusted-display: E_SURFACE_NO_SHELL), ui.node→foreground (from trusted-display: E_SURFACE_NO_SHELL) [preference BACKGROUND_PREFERRED, sdk 37])
15:13:02.975 shadow automation.relay-gate: Agree (2/2 rows agree on system state, tier and ceiling (states GRANTED ×2; tiers ASK ×2) [sdk 37, accessibility listed, listener not listed, 1 permissions granted, microphone blocked])
15:13:02.976 shadow automation.relay-gate: Inconclusive (live probe: not run: the accessibility service is listed in Settings, so the gate would not refuse these calls)
```

- 被动比对在 `GRANTED` 状态下也 **Agree**；「仅凭绑定才说得通」的行为 0（名单与绑定一致）。
- 活体探针按设计**拒绝运行**：无障碍已列出时闸门不会以 system_missing 拒绝，这两条请求会走到审批，探针不写信箱。
- 执行面比对第一次在每一行都实比了落点（无障碍开启后 Relay 写出 `surface`）：优先后台无 Shell，两侧都退回前台并注明 `E_SURFACE_NO_SHELL`。
- **Shizuku 在跑，但 Relay 报 no Shizuku 是正确的**：本 App 清单按只读白名单移除了 `rikka.shizuku.ShizukuProvider` 与 `moe.shizuku.manager.permission.API_V23`
  （Shizuku 后端不在白名单），Relay 收不到 binder，`backend.shizuku.running=false`。接入 Shizuku 会扩大权限面，属于单独决策，本次不改。
- 结束后中继已关闭，本 App 通知数 0；无障碍保持用户开启的状态，执行面偏好保持「优先后台」（未改用户设置）。

## 没覆盖到的

- 只有两条注册能力（`ui.snapshot` / `ui.node`），都只依赖无障碍：运行时权限、通知监听、Shell、麦克风 app-op 几组事实在真机上没有被比对到
  （`microphone blocked` 只是 C# 读到的事实，未注册 `audio.capture`）；这些靠 `relay-gate.json` 向量。
- Shell / 可信屏事实（Shizuku 已接入时的 `GRANTED` 与 `trusted-display` 路线）真机测不到：本 App 不接入 Shizuku（见上），只有向量。
- 档位 DENIED / ALWAYS、会话授权、审批频次合并、确认窗口与超时两种结局只有向量（Release 包不可调试，测试不改 Relay 偏好）。
- 中继刚开启时状态为 STARTING、信箱尚未运行，状态刷新触发的被动比对没有跑；本记录的被动结果来自按钮。
