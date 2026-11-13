# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · 开放写操作与防自批（PR-07 第六段，ADR-0008）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，FastDev），
基于 `42cb9d1` + 未提交的 PR-07 第六段改动（ADR-0008）。包名 `org.example.dshmobile`，uid 10267。

前置条件由用户本人完成：在系统设置里开启本 App 的无障碍服务、在 Shizuku 里授权本 App。App 没有代为开启或授权。
驱动：在主机上用 `run-as` 往 `files/pilot/run/inbox` 写 v1 请求（tmp + mv），读 `outbox/<id>.json`；
Relay 回 `E_AWAITING_CONSENT` / `E_GATE_WAITING_TURN`（什么都没执行）时以新 id 重发同一调用，与 `ui-facade.mjs` 相同。

## 1. 状态

「手机操控（实验）」状态区（`uiautomator dump`）：

```text
允许的能力：app.install, app.launch, … ui.tap, ui.text, ui.waitFor   ← 39 项，全部来自上游 RelayCapabilities.BuiltIn
Shizuku：已授权（v13，以 shell 身份运行）
DSH 工具（mcp__ui__*）：已注册，下次启动 DSH 时加载
```

`files/pilot/capabilities.json`：`backend.shizuku` 为 running / authorized / bound；39 行都在。
`sys.shell` 的档位是 DENIED、`usable:false`，开放能力表并没有放行它。

## 2. 伪造的回答被丢弃（自批路径 1）

以本 App 的 UID（`run-as`，等价于 guest 进程）执行 `am broadcast --user 0 -n …`。
对旧（`dshmobile.nativebridge.RelayApprovalReceiver`）、新（C# `RelayApprovalReceiver`）两个 receiver 各发两条：
一条带 `choice=ALLOW_SESSION` 但不带令牌，一条带错误令牌。

- 广播送达（`result=0`），说明不导出挡不住同 UID 的发送方。
- 4 条全部被丢弃：logcat `answer without a valid token dropped` ×4。
- Relay 仍回 `E_AWAITING_CONSENT`，审批卡片仍在。
- 未带 `--user 0` 时 `am` 被 `INTERACT_ACROSS_USERS` 拒绝；带上即为 guest 能走的路径。

从 shell UID 执行 `am start -n …/dshmobile.app.RelayApprovalConfirmActivity` 被拒：`not exported`。
自动化页 `AutomationActivity` 同样不能从 shell 打开。

## 3. 卡片只能打开确认页（自批路径 2）

`dumpsys notification` 中本 App 的审批卡只有一张：

- 「允许一次」「本次会话允许」的 PendingIntent 是 `startActivity`，目标是确认页；
- 「拒绝」是 `broadcastIntent`。

卡片展开文本含能力、等待数、`目标：…`、`参数：…`。

**取消测试（18:04–18:05）。** 用户两次在卡片上点「允许一次」，确认页都打开了（`START` 18:04:13.129 / 18:05:02.101）。
两次都在确认页上点「取消」，页面关闭（18:04:14.989 / 18:05:03.910）。驱动从 18:04:09 到 18:05:08 只收到
`E_AWAITING_CONSENT`，`effectState` 一直是 `not_started`。**只点卡片批不了任何事。**

## 4. 本 App 窗口在前时写操作被暂停

DSH Mobile 在前台（自动化页或确认页）时，`ui.key {"key":"back"}` 每一次到达后端都得到：

```json
{"ok":false,"surface":"foreground","degradedFrom":"trusted-display","degradeReason":"E_SURFACE_UNAVAILABLE",
 "reason":"host window is in front: retry after the user leaves this screen",
 "error":{"code":"E_TASK_SUSPENDED_BY_HOST","retryable":true,"exitCode":5},"retryPolicy":"user_decide","effectState":"not_started"}
```

这正是上游 `A11yBackend` 的 `selfWindowVisible` 闸。确认页显示期间 Agent 按不到「允许」。

## 5. 影子（重装含 `UiToolsShadowCheck.AllowedWire` 修复的构建后，10:09–10:10 UTC）

同一构建、全表 39 项，五个切片全部 Agree：

```text
automation.ui-tools        Agree  after relay.status / after relay.start: registered=True conflict=False, layer byte-identical, static facts equal
automation.relay-envelope  Agree  16/16 answers byte-identical
automation.relay-admission Agree  12/12 answers byte-identical (E_TRANSPORT_MALFORMED ×10, E_GATE_HOST_DENIED ×2)
automation.relay-surface   Agree  39/39 rows agree (Shizuku running, display down; surface compared on 15/39 rows)
automation.relay-gate      Agree  39/39 rows agree (MANUAL_ONLY ×16, SESSION_CONSENT ×3, GRANTED ×11, FOREGROUND_FOCUS ×2,
                                  RUNTIME_ASKABLE ×6, UNAVAILABLE_ON_DEVICE ×1; tiers ASK ×38, DENIED ×1)
automation.relay-gate      Agree  live probe: 2/2 answers byte-identical (E_GATE_SYSTEM_MISSING/system_missing, before any prompt)
```

准入探针改用写能力的坏参数（`ui.tap` 多余字段、`sys.shell` 多余字段），C# 会放行的模板一律不发。

重装之前的构建上 `automation.ui-tools` 报过 Diverge：C# 的允许列表仍是两项。修复后即上面的 Agree。

## 6. 发现：MagicOS 上重装会关掉无障碍服务

`dotnet build -t:Install` 之后，`enabled_accessibility_services` 不再含本 App 的服务，状态区显示「无障碍服务：未开启」。
App 不代为重新开启，由用户在系统设置里打开。

## 7. 成功路径：卡片 → 确认页「允许」→ 执行（2026-11-13）

每次 `dotnet build -t:Install` 之后用户都重新开启了无障碍（见第 6 节）。中继在运行，驱动先按 HOME 回到桌面再发 `ui.key {"key":"back"}`。

**第一次（14:07，修复前）失败，发现确认页自挡。**

```text
14:07:02.014 START …/dshmobile.app.RelayApprovalConfirmActivity
14:07:03.743 DshRelayApproval: request 391103099 -> ALLOW_ONCE (delivered=true)   ← 确认页点「允许」时立即广播
14:07:04.471 removeSinkSurface …RelayApprovalConfirmActivity                       ← 退出动画结束、窗口才移除
→ {"ok":false,"error":{"code":"E_TASK_SUSPENDED_BY_HOST"},"effectState":"not_started"}
```

Relay 拿到允许就立即执行。上游 `selfWindowVisible` 读无障碍窗口列表，此时确认页还在列表里，写操作被暂停。
用户的那次允许因此白白耗掉，从确认页批准的写操作永远执行不了。

**修复**（`RelayApprovalConfirmActivity`）：点「允许」后去掉退出动画、立即关闭，在 `OnDestroy` 之后再过 500 ms 才发出回答。
「本 App 窗口可见时拒绝写」这条规则不变；取消或页面重建都不发回答。

**第二次（14:24，修复后）通过。**

```text
14:24:08.674 START …/dshmobile.app.RelayApprovalConfirmActivity
14:24:10.560 removeSinkSurface …RelayApprovalConfirmActivity                       ← 窗口先移除
14:24:11.079 DshRelayApproval: request 416580510 -> ALLOW_ONCE (delivered=true)   ← +519 ms 才回答
→ {"ok":true,"surface":"foreground","degradedFrom":"trusted-display","data":{"key":"back"},"effectState":"verified"}
```

允许之前的三次重发都是 `E_AWAITING_CONSENT`。

## 未覆盖

- `ui_*` 写工具经 DSH Agent 端到端调用（本轮只直接驱动信箱）。
- 节点写（`ui.click` / `ui.setValue` 等）与 Shizuku 可信虚拟屏上的坐标输入：本轮只执行了 `ui.key`。
