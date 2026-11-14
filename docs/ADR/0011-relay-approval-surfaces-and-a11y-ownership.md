# ADR-0011：C# Relay 的审批界面（本页卡片 / 悬浮卡）、无障碍缺口提示与无障碍服务归属

- 状态：已接受（2026-11-14，PR-07 切换切片的界面补齐；用户要求「这些一次性做完，然后真机测试」，并选定
  「卡片与悬浮卡都做、作为新功能单独立 ADR」「加缺口提示」「移除旧服务」）
- 关联：[ADR-0004](0004-mobile-automation-interlock-relay.md)（只走通知卡）、[ADR-0008](0008-open-mobile-automation-writes.md)
  （一次性令牌、防自批）、[ADR-0009](0009-relay-host-csharp-optin.md) 第 6 条、[ADR-0010](0010-relay-guest-switch.md) 第 8 条、
  `docs/MIGRATION.md` PR-07

## 背景

上游 Interlock Relay 的确认框（`InterlockQueue`）按顺序选呈现位置：助手界面在前台时在**页面内**问，否则在用户允许了
「显示在其他应用上层」时用**悬浮卡**问，再否则发**通知卡**。本仓库的旧 Relay 接入（`RelayApproval.kt`）把前两者置空
（`uiInForeground() = false`、`approvalOverlay() = null`），ADR-0004 只用通知卡，ADR-0008 又把通知卡的「允许」改为经
应用内确认页再按一次。C# 宿主（ADR-0009 / ADR-0010）照此只走通知卡，`UiInForeground` 为 false，`Overlay` 为空。

因此用户每批准一次写操作都要：下拉通知 → 点允许 → 在确认页再点允许。另外三件事仍缺：
上游在「无障碍未开启 / 已开启但未绑定」时会给用户一个提示（`publishAccessibilityGap`），C# 宿主没有；
「手机操控」页的状态行在 Guest 模式下仍读已停止的旧 Relay；页面写的偏好要等 15 s 心跳才生效。
本仓库还声明着两个无障碍服务（旧 Relay 的 `RelayAccessibilityService` 与 C# 的 `DshAccessibilityService`），
用户在系统设置里会看到两个同名相近的条目。

**旧 Relay 从未有过页面卡片和悬浮卡**，所以这两项不是「把旧实现移植过来」，而是 C# 宿主的新功能，按 AGENTS.md 先写本 ADR。

## 决策

1. **按上游顺序启用三个呈现位置，只用于 C# 宿主**（旧 Relay 不加需求）。
   - *本页卡片*：「手机操控」页处于 resumed 时 `CsRelay.PageCardShown = true`（即上游 `uiInForeground()`），
     队列把正在问的那一题路由到 PAGE，页面顶部显示 `ApprovalCardView`。onPause 立即置 false 并重新路由。
   - *悬浮卡*：`AndroidOverlayApproval`（翻译自上游 `OverlayApproval.kt`）实现 `IOverlayApprovalChannel`。
     仅当用户在系统设置里授予「显示在其他应用上层」（`Settings.canDrawOverlays`）时可用；清单声明
     `SYSTEM_ALERT_WINDOW`，App 只提供跳转到系统授权页的按钮，从不替用户授权。
   - *通知卡*：不变（允许仍经应用内确认页，ADR-0008）。
   两张卡是同一个类（`ApprovalCardView`，翻译自上游 `ApprovalCardView.kt` 的内容与按钮，外观为本 App 的 Material 3 卡片，
   无 DSHBox 品牌资产）：能力标题、对象、参数、风险、排队数、倒计时，按钮为 拒绝 / 本次会话允许（仅题目允许时）/ 允许一次，
   三个按钮外观相同，不暗示该选哪个。

2. **防自批不弱化**（本页卡片与悬浮卡都不需要确认页的第二次按下，因为它们本身就在本 App 的窗口里、由用户按下）：
   - 写能力在宿主显示屏上执行前，只要窗口列表里有本 App 的窗口就返回 `E_TASK_SUSPENDED_BY_HOST`（`HostWindowRule`，
     ADR-0008）。本页卡片在本 App 的 Activity 里；悬浮卡是本 App 的 `TYPE_APPLICATION_OVERLAY` 窗口，并且在系统窗口列表
     漏报或滞后时也由 `AndroidOverlayApproval.OnScreen` 强制计入本 App 窗口——Agent 在卡片显示期间按不到任何东西。
   - 悬浮卡「允许」的答案在窗口移除 500 ms 后才交回队列（上游 `ANSWER_AFTER_CLOSE_MS`），被批准的调用不会因卡片尚在而自挡，
     也不会在卡片仍在时执行。
   - 所有按钮与卡片 `filterTouchesWhenObscured`：被其他应用的窗口遮挡时触摸被丢弃，防点击劫持。
   - 悬浮窗口与显示本页卡片期间的页面窗口设置 `FLAG_SECURE`：截图 / 录屏拿不到卡片内容。
   - 悬浮卡在 4 s 内拿不到窗口焦点即视为被系统挡住（`hidden-by-platform`），撤下并让队列改投通知卡，不在看不见时等待。
   - 答案按请求 id 交给队列（`ResolveApproval(choice, id)`），旧卡片的答案不会落到新题目上；超时仍按上游视为未获批准。

3. **本页卡片批准的宿主屏写操作会返回 `E_TASK_SUSPENDED_BY_HOST`**（可重试）：用户按下时页面还在前台。这是上游语义，
   不为「看似成功」去绕过；读操作与非宿主屏的操作不受影响。悬浮卡没有这个问题（答案在窗口移除后才交回）。

4. **无障碍缺口提示**（移植上游 `publishAccessibilityGap` / `dismissAccessibilityNotice` / `SystemStateProbe` 的 `onAccessibilityGap`）：
   一次真实调用的探测发现缺口（`Revoked`：设置里没开；`NotBound`：设置里开了但系统没绑定）时，`RelayHost.AccessibilityNotice`
   发布一次；能力清单的探测不发布；有审批在问时不发布，审批出现时撤下（不算已读）；用户点「知道了 / 去开启」后同一缺口不再提示，
   直到无障碍绑定状态变化。「手机操控」页弹对话框，「去开启」只打开系统无障碍设置（优先直达本 App 服务的详情页），App 从不替用户开启。

5. **Guest 模式的状态行由 C# 宿主提供**（`RelayHost.Status()`：阶段、信箱、降级原因、无障碍缺口、允许的能力、guest 入口），
   `ui_*` 工具行如实写「已注册，DSH 运行中需重启才加载」，不声称已被当前 DSH 加载。

6. **页面写偏好后立即通知 C# 宿主**（`OnPreferencesChanged`），能力清单与手册随即重铺；心跳复核保留作兜底。

7. **只保留本 App 自己的无障碍服务**。清单以 `tools:node="remove"` 去掉 core AAR 合并进来的
   `interlock.relay.core.exec.a11y.RelayAccessibilityService`；`DshAccessibilityService` 的标签改为「DSH Mobile 手机操控」。
   后果：旧 Relay 的 `ui_*` 读取与操作返回无障碍未开启（稳定错误码，不伪造成功），其他能力不变；
   `RelayA11yTreeSource` 的旧实例兜底永远为空。DSHBox AAR 本身不动（所有 PR 完成后才移除依赖）。

## 后果

- 正面：在「手机操控」页或其他应用里，一次批准只需按一下；用户只看到一个无障碍服务；缺口有明确提示；状态行反映真正在服务的宿主。
- 负面 / 残余风险：
  - 悬浮卡依赖用户授予的特殊权限；ROM 若在悬浮窗上层再盖窗口，焦点检查会让它退回通知卡（多一步，但不会误批）。
  - 本页卡片批准的宿主屏写需要 Agent 重试（第 3 条）。
  - 旧 Relay 的 `ui_*` 不再可用：旧 Relay 只剩作为非 `ui_*` 能力与影子比对的基线；需要 `ui_*` 时用 C# Relay。
  - 无障碍读取比对（第七、八段影子）与双中继比对里旧侧的 `ui.*` 不再能取得活体树，这些影子此后只能得到 Inconclusive / 旧侧失败。
- 验证：Runtime 单测（缺口提示的发布 / 跳过 / 静默 / 重置、审批撤下提示、悬浮卡路由与答案、`Status()`、偏好即时重铺）；
  真机：本页卡片、其他应用中的悬浮卡、悬浮卡显示期间 `ui.tap` 被挂起、无障碍关闭时的缺口对话框、状态行、偏好重铺。
