# ADR-0010：Relay 切换切片——C# 宿主可接管 guest 信箱、发布 guest 文件

- 状态：已接受（2026-11-14，PR-07 切换切片；用户要求「继续推进切换切片」）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first、规则 3 先影子后默认）、[ADR-0008](0008-open-mobile-automation-writes.md)（写操作开放与防自批）、
  [ADR-0009](0009-relay-host-csharp-optin.md)（C# 宿主可选、独立信箱）、`docs/MIGRATION.md` PR-07 切换切片

## 背景

ADR-0009 之后 C# 宿主能完整处理一次请求，但只服务自己的信箱 `files/pilot-cs`：guest 看到的 `/opt/pilot`
（入口命令 `bin/relay`、能力清单 `capabilities.json`、手册 `USAGE.md`、提交标记 `version.json`、信箱 `run/*`）
仍全部由旧 Relay 发布和服务，`ui_*` 工具层（DSH `--patch`）也由旧 `relay.start` 写入。要让 C# 成为默认，
先得让 C# 宿主能在 guest 面前**替换**旧 Relay，并在真机上用 guest 自己的 CLI 和 DSH 的 `ui_*` 走通。

## 决策

1. **移植 guest 文件发布**。`GuestAssets`（暂存目录写入 → 改名进绑定子树、逐级拒绝链接、提交标记 `version.json`
   记录三份文件的 SHA-256、`Intact` 复核）与 `capabilityRows` / `capabilitiesJson`（先裁决路线再按该路线探测、
   `usable` / `implemented` / `surface` / `degradeReason`、`backend.shizuku` 四段链路）移植到 `DshMobile.Runtime`，
   连同「变了才重铺」的单飞门与触发点（启动序列的 assets 步骤先于信箱、Shizuku 状态 / 用户服务绑定 / 无障碍绑定 /
   建屏收屏 / 每分钟巡检）。上游的通知监听绑定回调与档位 / 偏好 setter 在 C# 侧没有对应事件（偏好仍由旧页面写），
   以心跳每 15 s 一次的内容复核代替（内容不变不写盘）。
2. **guest 资产随程序集发布，不读 `third_party/`**。`relay-client.cjs` 与 `USAGE` 模板（上游 Kotlin 原始字符串经
   `trimIndent()`）逐字节复制到 `src/DshMobile.Runtime/Automation/Relay/Transport/Assets/`，作为嵌入资源；
   `.gitattributes` 设为 `-text` 防止换行转换。登记于 `docs/UPSTREAMS.md`。
3. **两种模式，同一时刻只有一个 C# 宿主**（`CsRelay`）：
   - *Shadow*（ADR-0009，不变）：独立信箱 `files/pilot-cs`，与旧 Relay 并行，供双中继比对；现在也在那里发布
     guest 文件，因此可以和旧 Relay 发布的文件逐字节比对。
   - *Guest*（新）：路径策略 `RelayPathPolicy.DshMobile`（`files/pilot` ↔ guest `/opt/pilot`），代替旧 Relay 服务
     guest 信箱并写入 `ui_*` 工具层。用户在「手机操控 → 开启中继」里选「C# Relay（实验）」。
4. **绝不两个 Relay 服务同一信箱**。进入 Guest 模式前先调旧 `relay.stop`；它失败就**不接管**（失败即关闭，界面写明原因）。
   选「旧 Relay」时先停 C# Guest 宿主再调 `relay.start`。Guest 模式运行时不能再起 Shadow 宿主。
5. **随沙箱停止**。`RuntimeCoordinator.SandboxStopping`（正常停止、C# 宿主停止、启动失败后的清理三处）触发时，
   Guest 宿主停止并删除我们写的 `ui_*` 层——与旧 `sandbox.stop` 停 Relay、删层的时机一致。
6. **审批与安全不变**。Guest 宿主每次需要审批的调用仍走 C# 通知卡 → 应用内确认页（ADR-0008 一次性令牌 + 本 App
   窗口内按下）；不自行开启任何系统权限。guest 文件只是告知（guest 与宿主同 uid，改它们改不了宿主判定），但写入
   形态是安全边界：不跟随 guest 放置的链接写宿主私有文件。
7. **默认仍是旧 Relay**（`MigrationOptions.RelayHost = LegacyWithShadow`）。Guest 模式是开发者 / 验收选项；
   通过真机验收（guest `relay doctor` + `ui_*` 自检 + DSH 里真实 `ui_*` 调用）并经用户确认后，再单独翻转默认，
   之后才删除该切片的 DSHBox 依赖（ADR-0007 规则 3）。
8. **暂不移植**：悬浮窗审批、无障碍缺口提示、应用内审批卡（C# 宿主 `UiInForeground` 仍为 false，只走通知卡）。
   （已由 [ADR-0011](0011-relay-approval-surfaces-and-a11y-ownership.md) 加入；同一 ADR 去掉了旧无障碍服务的声明。）
   `media.write` 的 RIFF 偏移缺陷继续保留以维持影子可比，翻转默认时单独修复。

## 后果

- 能力清单的文本形式用真机上旧 Relay 发布的 `capabilities.json` 锁定（`tests/TestVectors/relay-guest/`）：
  由同一组事实重建，C# 输出与之逐字节一致；`USAGE.md` 与 `bin/relay` 与设备上的提交标记哈希一致。
- 新影子 `automation.relay-guest-files`：「双中继比对」先只读比较两边发布的三份文件（路径改写后），再发 16 条调用。
- 旧 `relay.status` 仍报告旧 Relay 的阶段；Guest 模式下页面另起一行写明 C# Relay 正在服务 guest 信箱。
  `relay.selftest` 只是在 guest 里跑 `/opt/pilot/bin/relay doctor` 与 `ui-facade-check.mjs`，对哪一个 Relay 服务信箱是中立的，沿用。
- 每次重装 APK，部分 ROM 会关闭本 App 的无障碍服务，验收前需用户重新开启（C# 服务优先）。
