# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · C# Relay 审批界面 / 无障碍缺口提示 / 服务归属（ADR-0011）真机记录

设备：PTP-AN10，`AN6B024B01054420`，Shizuku 13。构建：Debug（`dotnet build -t:Install`），基于 `ec844db` + 未提交的 ADR-0011 改动；
设备上的 `DshMobile.Android.dll` 与本地构建 md5 一致（`af7d741a…`）。主 App `org.example.dshmobile`。

前置条件全部由用户本人完成：在系统设置里开启「DSH Mobile 手机操控」无障碍服务、允许「显示在其他应用上层」、
点「开启中继 → C# Relay（实验）」，以及每一张审批卡的回答。主机端只用 `run-as` 往 guest 信箱 `files/pilot/run/inbox`
放请求（暂存后改名，等同 guest CLI）并读 `relay.log` / 回包 / `dumpsys`；没有代为开启服务、代为授权或回答审批，
没有执行 `uiautomator dump`。测试脚本（`files/cardtest/`）测后已删除。

## 安装与清单

- `dumpsys package`：只声明 `dshmobile.relay.DshAccessibilityService`（`BIND_ACCESSIBILITY_SERVICE`），旧
  `interlock.relay.core.exec.a11y.RelayAccessibilityService` 已不在合并清单中；`android.permission.SYSTEM_ALERT_WINDOW` 已声明。
- 用户开启后 `dumpsys accessibility` 的绑定服务里有 `label=DSH Mobile 手机操控`；`appops SYSTEM_ALERT_WINDOW: allow`。
- 「手机操控」页状态行（Guest 模式，C# 提供）：`C# Relay: Running · 信箱运行中`、无障碍已开启、允许的能力、`guest CLI：/opt/pilot/bin/relay`、
  Shizuku 已授权（v13，shell）、`DSH 工具（mcp__ui__*）：已注册（…需重启 DSH 才会加载）`。

## 1. 本页审批卡（「手机操控」页在前台）

| 请求 | 日志 | 用户回答 | 回包 |
|---|---|---|---|
| `card1` `pkg.query` | `approval presented via=page` | 允许一次（超过 8 s 内联等待之后才按） | `E_AWAITING_CONSENT`（retryable）；此后的点击无人等待，按上游规则不缓存（防一次点击批两次） |
| `card2` `pkg.query` | `via=page` → `approval decided verdict=allow_once` → `backend=direct result=ok` | 允许一次 | `ok`，7.4 s |
| `pq1` / `pr1` / `dn1` `pkg.query` | 同上 | 允许一次 | `ok`（`pr1`：`count=109`），5.5 s / 7.8 s / 2.6 s |

- 卡片显示期间页面窗口 `fl=… SECURE …`，主机 `adb screencap` 返回 "Capturing failed"（FLAG_SECURE 生效）。
- App 日志：`c# relay page card: pkg.query -> ALLOW_ONCE (request 331389961…963)`。
- `card1` 的 `E_AWAITING_CONSENT` 不是缺陷：guest CLI 会在该码上重试，重试挂到同一张卡上；主机的单次请求不重试，所以那次点击没有在等的调用。
  之后改用设备端循环（每次拿到 `E_AWAITING_CONSENT` 就以新 id 重发同一问题）。

## 2. 悬浮审批卡（本页不在前台）

| 请求 | 前台 | 日志 | 用户回答 | 回包 |
|---|---|---|---|---|
| `ov1` `ui.key back` | DSH Mobile 主界面（`MainActivity`） | `approval presented via=overlay` → `call deferred cause=awaiting_approval` → `verdict=allow_session` | 本次会话允许 | `E_TASK_SUSPENDED_BY_HOST`：前台是本 App 主界面，防自批规则正确挡下 |
| `k1` `ui.key back`（会话授权，无卡） | 同上 | `dispatch armed` → `backend=a11y result=failed` | — | `E_TASK_SUSPENDED_BY_HOST`（同上，确认与悬浮卡无关） |
| `h1` `ui.key back`（会话授权，无卡） | 桌面（`UniHomeLauncher`） | `backend=a11y result=ok` | — | `ok`，`effectState: verified` |
| `q1` `pkg.query` | 桌面 | `via=overlay`，`mCurrentFocus` = 本 App 悬浮窗 | 未在 60 s 内回答 | `E_GATE_APPROVAL_TIMEOUT`（`verdict=timeout`），超时即未批准 |
| `t1` `ui.key back`（会话授权，`q1` 悬浮卡在屏上时） | 本 App 悬浮窗 | `backend=a11y result=failed`，8 ms | — | **`E_TASK_SUSPENDED_BY_HOST`**：悬浮卡显示期间 Agent 的写被挂起 |
| `d1` `pkg.query` | 桌面 | `via=overlay` → `verdict=deny` | 拒绝 | `E_GATE_HOST_DENIED`，`effectState: not_started`；卡片撤下后焦点回到桌面 |

- 悬浮路线超过内联等待后不回 `E_AWAITING_CONSENT`，而是挂起请求（`call deferred`），用户回答后才回包。

## 3. 无障碍缺口提示（用户在系统设置里关闭本 App 服务后）

- `enabled_accessibility_services` 不含本 App；页面状态行：`无障碍服务：未开启（读取界面需要先开启）`。
- `g1` `ui.snapshot` → `E_GATE_SYSTEM_MISSING`（5 ms）；页面弹出「需要无障碍服务」：
  「DSH 刚才请求了 ui.snapshot，但「DSH Mobile 手机操控」无障碍服务没有开启，这次请求已被拒绝……」，按钮 知道了 / 去开启（截图已查看）。
- 用户点「知道了」后 `s1` `ui.snapshot` 仍 `E_GATE_SYSTEM_MISSING`，对话框**不再**出现（`mCurrentFocus` 仍为页面窗口）：同一缺口已静默，直到绑定变化。

## 4. 偏好写入即时重铺

- 用户点「执行面偏好（开发者）」：`shared_prefs/relay.xml` 10:55:13.368 写入 `surface_preference=BACKGROUND_PREFERRED`，
  `files/pilot/capabilities.json` 与 `version.json` 10:55:13.376 重写（**8 ms 后**，不是 15 s 心跳）；新清单 21 行
  `degradeReason: E_SURFACE_UNAVAILABLE`（没有可信虚拟屏，如实退回）。

## 结论

ADR-0011 的验证清单全部在真机上通过：本页卡片、其他界面中的悬浮卡（允许 / 拒绝 / 超时）、悬浮卡显示期间写操作被挂起、
主界面在前时写操作被挂起、缺口对话框与静默、C# 状态行、偏好即时重铺、只剩一个无障碍服务。

未验：缺口 `NotBound`（设置里开启但未绑定）只有单测；「去开启」直达本服务详情页在本 ROM 上的落点未截图；悬浮卡被系统遮挡
（`hidden-by-platform` → 改投通知卡）未在真机触发；本页卡片批准宿主屏写操作（返回 `E_TASK_SUSPENDED_BY_HOST`）未单独演示。

测试结束时状态：本 App 无障碍服务为用户关闭状态，执行面偏好为「优先后台」，C# Relay 仍服务 guest 信箱。
