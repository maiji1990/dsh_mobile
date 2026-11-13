# 2026-11-13 · Honor PTP-AN10 · Android 17 (API 37) · 无障碍读取路径影子比对（PR-07 第七段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，FastDev），
基于 `c5036e5` + 未提交的 PR-07 第七段改动。包名 `org.example.dshmobile`，uid 10267。

前置条件由用户本人完成：在系统设置里开启本 App 的无障碍服务（每次重装后 MagicOS 会把它关掉，用户重新开启）、
在「手机操控（实验）」页开启中继、点「无障碍读取比对（开发者）」、在两张审批卡上选「本次会话允许」并在确认页按「允许」。
App 和主机都没有代为开启服务或回答审批。

## 比对方式

`RelayA11yReadShadowCheck`：C# 读一次活体树 → 同一调用经真实信箱发给旧 Relay（走正常闸门与审批）→ C# 再读一次。
旧 Relay 回包的 `data` 原文与 C# `RelayEncoding.EncodeData` 必须逐字节相等（失败回包比 `code:reason`）。
旧回包等于前后任一次 C# 读取即一致；两次 C# 读取相同而旧回包不同才算差异；屏幕在调用期间变化或服务暂时不在时重试（最多 3 次），
仍不能比较则为 Inconclusive，不算差异。

调用（由当时的树挑选）：`ui.snapshot`；`ui.node {"nodeId":0}`；第一个可点节点 `{"nodeId":3}`；
其下方相对定位 `{"nodeId":3,"relative":{"side":"below"}}`；第一个唯一文本的选择器。
C# 侧经 JNI 读同一个服务实例（`RelayAccessibilityService.Companion.current()`，只读、过渡期），见 `RelayA11yTreeSource`。

## 1. 第一次点按：检查卡住，没有发出请求（已修）

点按后按钮全部变灰、提示「正在比对」，但信箱 `inbox/` 为空、没有审批卡、`shadow-reports.jsonl` 没有新行。
原因：探针按钮以 `_ = A11yReadProbeAsync()` 丢弃任务，读树中的异常无人接收，页面停在运行中。
修复：`RunAsync` 把读树故障变成 Inconclusive 报告（「the C# reader failed: 类型: 消息 位置」），按钮处理器 try/finally 恢复按钮并显示异常。
重装后同样的操作正常发出请求，异常未再出现（故障的具体类型没有被记录下来）。

## 2. 两次 Diverge：由主机端 `uiautomator dump` 造成（非读取差异）

15:21:06 与 15:21:28 各写入一条 `Diverge`，差异都在同一调用：

```text
ui.node {"nodeId":3}: legacy E_BACKEND_UNAVAILABLE:no backend reachable for the foreground surface; … | C# (no tree)
```

logcat 显示 15:20:52.161 `AccessibilityManagerService: unbindService … RelayAccessibilityService`、
15:20:54.343 `bindService …`，中间是 `UiAutomationManager`：这正是主机端为看屏幕而执行的 `uiautomator dump`——
UiAutomation 注册期间系统解绑所有无障碍服务。旧 Relay（`BackendDispatcher` 挑不到后端）与 C#（`current()` 为 null）
同时看不到服务，双方实际一致，没有可比较的树。同一两次运行中其余调用：`ui.snapshot` 2671 字节 / 14 节点两次相等，
相对定位 500 字节相等、选择器 658 字节相等（另一次两边都是 `E_NODE_NOT_FOUND`），`{"nodeId":0}` 因审批期间屏幕变化而 Inconclusive。

两次运行重叠：从确认页回到本页时 `OnResume` 刷新状态并重新启用了按钮，用户得以再点一次。

修复（均有单测）：
- 服务在调用前后不在 → 重试，不算差异；三次都不在 → Inconclusive（`ServiceGoneAroundOneCall_IsRetried_NotADifference`、`ServiceGoneForEveryAttempt_IsInconclusive`）。
- 屏幕在调用期间变化 → 重试（会话授权已给，重试不再弹卡）。
- 同一进程内拒绝并发运行（`OverlappingRun_IsRefused`）；页面在比对期间不再因刷新而启用按钮。
- 之后的真机运行期间主机端只用 `dumpsys` / `run-as` 观察，不再执行 `uiautomator dump`。

## 3. 结果：5/5 逐字节一致

重装、用户重新开启无障碍与中继后，15:28:42（`ui.snapshot`）与 15:29:54（`ui.node`）各一次 `ALLOW_SESSION`（`DshRelayApproval … delivered=true`），
15:29:55 写入：

```text
automation.relay-a11y-read  Agree  5/5 byte-identical:
  ui.snapshot: equal (screen changed during the call) (2671 bytes, 14 nodeId)
  ui.node {"nodeId":0}: equal (422 bytes, 1 nodeId)
  ui.node {"nodeId":3}: equal (491 bytes, 1 nodeId)
  ui.node {"nodeId":3,"relative":{"side":"below"}}: equal (500 bytes, 1 nodeId)
  ui.node {"selector":{"text":"让沙箱内的 DSH 通过 ui_* 工具读取…"}}: equal (658 bytes, 1 nodeId)
```

覆盖到的上游规则：前序 nodeId、类名 / 文本 / 描述 / 坐标或 offscreen、clickable / editable / checked 只在为真时出现、
`package` 与 `screen{width,height,densityDpi,display}`；`describe` 的整套布尔、`actions[]` 名称、`bounds`、`treeDisplay`；
相对几何 below；选择器文本精确匹配（含中文与 `ui_*` 原样输出、"/" 转义规则）。期间旧 Relay 照常执行审批，C# 没有发出任何写请求。

## 未覆盖

- `range`（`RangeInfo` 与 Float 打印）、`truncated`（600 节点 / 40 层上限）、歧义候选文案、`E_NODE_NOT_FOUND` 的过期 nodeId 提示：
  只有 C# 向量（relay-a11y-read.json 由独立 Python 实现按上游规则生成；上游直接用 `AccessibilityNodeInfo`，JVM 桩跑不了），这页上没有对应控件。
- 宿主窗口挂起规则（`HostWindowRule`）只有向量；第六段真机已验旧实现的 `E_TASK_SUSPENDED_BY_HOST`。
- 其他应用 / 多窗口 / 非默认显示的树；写路径（act / dispatch / gesture / setText / globalAction / setProgress / scroll / capture）属第八段。
