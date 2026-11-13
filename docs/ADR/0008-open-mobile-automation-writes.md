# ADR-0008：开放手机操控全部能力（写操作 + Shizuku），以一次性令牌 + 应用内确认页防自批

- 状态：已接受（2026-11-12，用户要求「打开白名单、连接 Shizuku，完成后续的迁移」）
- 取代：[ADR-0004](0004-mobile-automation-interlock-relay.md) 第 2 条（只读白名单）、第 3 条（清单裁剪）；
  满足并关闭第 8 条（开放写操作的前置条件）。ADR-0004 其余条款与 [ADR-0007](0007-source-ownership-csharp-first.md) 不变。
- 关联：PR-07（手机操控执行层），`docs/SECURITY.md`，`docs/PROVIDER_CONTRACTS.md` §2

## 背景

V1 只注册 `ui.snapshot` / `ui.node`。ADR-0004 第 8 条要求开放写操作之前证明「Agent 无法点中本 App 的审批 UI」。
复核旧实现时发现两条自批路径，都不需要真机就能成立：

1. **伪造广播**。guest 进程（PRoot）以本 App 的 UID 运行，且 `--bind=/system`、`/apex`，能执行 `/system/bin/am`。
   `RelayApprovalReceiver` 不导出只挡其它 UID，挡不住同 UID 的 `am broadcast -n …/RelayApprovalReceiver
   --ei requestId N --es choice ALLOW_SESSION`。这一条与是否开放写操作无关，是既有缺陷，按安全修复处理。
2. **点自己的审批按钮**。开放 `ui.click` / `ui.tap` 后，Agent 可以下拉通知栏、点「允许」。快照能读到通知栏里本 App 的卡片
   （ADR-0004 第 8 条的真机观察）。

上游 Relay 的 `A11yBackend.execute` 已有一道闸：只要本 App 任一窗口在宿主屏上可见（`selfWindowVisible`），
所有写类能力（`ui.tap/swipe/text/key` 与节点写的八条）一律回 `E_TASK_SUSPENDED_BY_HOST`。通知栏属于 SystemUI，
这道闸挡不住第 2 条，但挡得住「在本 App 窗口里点允许」。

## 决策

1. **一次性令牌（安全修复，新旧两侧同时做）**。每张审批卡发 128 位随机令牌，只放进该卡自己的不可变 PendingIntent。
   其它进程读不到这些 extras。receiver 只接受带对的令牌的回答，比较用常数时间，令牌用一次即作废；
   取消卡片时吊销令牌，整体撤销时清空。实现：Kotlin `RelayApproval.kt` 的 `ApprovalTokens`、
   C# `ApprovalTokens`（`DshMobile.Runtime`）+ `RelayApprovalReceiver.Tokens`。
2. **允许必须在本 App 窗口里按下**。卡片上「允许一次」「本次会话允许」不再直接广播，而是打开
   `dshmobile.app.RelayApprovalConfirmActivity`（Material 3，不导出、不进最近任务、`FLAG_SECURE`、
   `filterTouchesWhenObscured`）。确认页显示期间它就是本 App 的可见窗口，Relay 拒绝一切写操作，
   Agent 无法替用户按下确认页上的「允许」。按下「允许」后确认页先无动画关闭，窗口移除后再回答：否则 Relay 立即执行时
   确认页仍在窗口列表里，这一次允许会被自己挡掉。确认页只把卡片原有的 id / 选择 / 令牌转发给白名单内的两个 receiver
   （`legacy` / `csharp`），自己不做决定。「拒绝」仍直接广播：拒绝帮不了调用方。
3. **卡片说清在批什么**。卡片展开文本与确认页显示上游已截断的 `targetLabel` / `paramDetail`。
   以前只显示能力名，用户批 `sys.shell` 时看不到命令。
4. **能力表全开**。`MobileAutomation` 用上游 `RelayCapabilities.BuiltIn`（39 项）。闸门不变：
   档位上限、逐次审批、本会话授权、系统态（无障碍 / 通知监听 / 运行时权限由用户在系统里开）、执行配额全部照旧。
   开放能力不等于授权：`sys.shell` 上限 ASK_ONLY、默认 DENIED，其中会改系统的动词（含 `input tap`）还要逐条在
   「危险能力」开关里打开，本 App 没有提供这个开关界面。
5. **清单不再裁剪**。移除 `tools:node="remove"`，Relay 库清单里的权限、`<queries>`、通知监听服务、
   录屏前台服务、Shizuku provider 原样合并。每一项都是用户在系统界面里做的授权，App 不代为授予。
6. **连接 Shizuku**。「手机操控」页新增「连接 Shizuku」。它经标准 JNI 调用已随包的 `rikka.shizuku.Shizuku`
   客户端（MIT）。判定移植自上游 `ShizukuState`（`ShizukuConnection`，C#）：binder 不在（或服务端 v11 以前）就如实报告，
   不替用户启动 Shizuku；已授权不重复弹框；被永久拒绝时提示去 Shizuku 里手动允许；其余情况调用
   `Shizuku.requestPermission(41)`，弹出 Shizuku 自己的授权框，由用户作答。请求码 41 与上游相同，
   Relay 自己的结果监听据此刷新状态。
7. **`ui_*` 写工具**（`ui-facade.mjs` 0.2.0，契约 §2）：`ui_click`、`ui_set_text`、`ui_press_key`（back / home / recents）、
   `ui_scroll`、`ui_wait_for`、`ui_screenshot`。
   - 动作之前先用 `ui.node` 实时回读并比对身份（类名 / 标签 / 位置，可选 `expectedRole` / `expectedName`）。
   - 任何写操作之后（无论成败）当前快照的 ref 一律作废。
   - 写操作只在「用户尚未作答」（什么都没执行）时重发同一调用；超时报 `OPERATION_TIMEOUT` 并提示效果未知，
     不自动重发。
   - 不提供 `approvalTicket` / `ui_confirm_approval`：审批只能在手机上由人完成。

## 残余风险

- **用户批准了 shell 类能力**。shell 身份的权限并集覆盖其余全部能力，批准 `sys.shell` 的写动词，等于把同等能力交给 Agent。
  需要三道条件同时成立：用户把档位调离 DENIED、在危险能力开关里逐条打开该动词、逐次批准（卡片上显示命令）。
  Shizuku 的坐标输入只落在可信虚拟屏上（`BACKEND_TRUSTED`），本 App 在那块屏上没有窗口，碰不到确认页。
- **确认页的保护依赖无障碍后端的 `selfWindowVisible`**。将来 C# 接管执行层（PR-07 后续段）时必须原样保留这条规则，
  并把「确认页在前台时写操作被拒」列入真机验收。
- **本会话允许**按能力计，在会话内对同一能力不再逐次询问，这是上游语义，未改。
- **改投时令牌被清空（失败即关闭）**。上游在队列把待决项改投到 PAGE / OVERLAY / NONE 时整体 `cancel()` 通知；
  令牌随之清空。如果确认页已经打开、之后才发生改投，确认页上的「允许」会被丢弃（logcat `answer without a valid token dropped`），
  Relay 继续等待，下一次重发再挂一张新卡（新令牌）。这只会让用户再点一次，不会误批；旧桥不为此加需求，C# 接管队列时
  可改为「只在终局 `cancelFor` 与中继停止时吊销」。

## 验证

- C#：`RelayApprovalParityTests`（令牌只用一次、错令牌不消耗正确令牌、吊销 / 清空、receiver 白名单、只有允许需要确认页）、
  `ShizukuConnectionTests`（上游判定顺序）。
- Kotlin：`build-native` 全部 JVM 单测（含既有的上游对拍）。
- guest：`ui-facade.test.mjs` 44 例（写工具、ref 作废、身份不符不动作、超时不重发、确认页在前台时
  `E_TASK_SUSPENDED_BY_HOST` → `PROVIDER_UNAVAILABLE`）。
- 真机（必须，AGENTS.md 规则 9）：用户开启无障碍、授权 Shizuku 之后，逐项验证下面四条：
  - 通知卡 → 确认页 → 执行；
  - 确认页在前台时 Agent 写操作被拒；
  - 用 `am broadcast` 伪造的回答被丢弃（logcat `answer without a valid token dropped`）；
  - Shizuku 状态行与授权框。
  证据写入 `migration/parity/`。
  2026-11-12 结果见 [2026-11-12-PTP-AN10-A17-relay-writes.md](../../migration/parity/2026-11-12-PTP-AN10-A17-relay-writes.md)：
  四条全部通过，五个影子切片在全表上 Agree。「允许 → 执行」第一次失败：确认页还在关闭时 Relay 就执行了，被自己的窗口挡下。
  修复为确认页无动画关闭、窗口移除后 500 ms 再发回答（第 2 条的实现细节，规则不变），之后 `ui.key back` 执行并 `verified`。
