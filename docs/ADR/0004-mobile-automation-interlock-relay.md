# ADR-0004：手机自动化复用 Interlock Relay core，经 `ui_*` 门面暴露

- 状态：已接受（2026-11-10，用户选择「复用 Interlock Relay」）
- 修订（2026-11-10）：其中“复用 DSHBox / Kotlin 实现”的部分按 [ADR-0007](0007-source-ownership-csharp-first.md) 降级为过渡实现，将逐切片替换为 C#；本 ADR 的安全与契约约束（审批、单一 `ui_*` / `browser_*` 契约、主机指纹固定、不可导出私钥）继续有效，替换实现必须同样满足。
- 修订（2026-11-12）：第 2 条（只读白名单）、第 3 条（清单裁剪）由 [ADR-0008](0008-open-mobile-automation-writes.md) 取代；第 8 条的前置条件由 ADR-0008 的一次性令牌 + 应用内确认页满足，写操作已开放。
- 来源：方案第 9 节；`maiji1990/ui_mcp` 的 `docs/p2-phone-facade-design.md`
- 关联任务：TASK-010

## 背景

方案要求手机自动化只走一套 `ui_*` 契约（AGENTS.md 规则 6），执行层可复用 DSHBox 的 Interlock Relay core
（Apache-2.0，独立仓库 `WSK-build/interlock-relay`，DSHBox 内 `pilot/interlock-relay-core`）。Relay core 自带：
文件信箱传输（`run/inbox` / `run/outbox`）、审批互锁与分级、无障碍 / 平台直连 / Shizuku 三个后端、39 项能力、
审计日志、沙盒侧 `relay-client.cjs`，宿主经 7 个 SPI 注入 UI 与策略。

其库清单声明了大量权限（日历、联系人、定位、媒体、录音、闹钟、电池优化豁免）及通知监听、屏幕录制前台服务，
会自动合并进 App。这与本项目「可选模块、最小权限」相冲突。

## 决策

1. **执行层**：以源码引用 DSHBox 子模块中的 `pilot/interlock-relay-core`（不修改），`RelayRuntime.start/stop`
   随沙箱启停。不引入 `pilot/dshbox-adapter`（品牌与 Compose UI），宿主 SPI 由 `DshMobileRelay` 自行实现。
2. **能力白名单**（`RelayCapabilities` SPI）：V1 实际只注册只读的 `ui.snapshot`、`ui.node`
   （`MobileAutomation.ALLOWED_WIRE`）；写操作 `ui.click`、`ui.text`、`ui.scroll`、`ui.key` 留待第 8 条前置条件满足后再开。
   其余能力（日历、联系人、定位、媒体、录音、剪贴板、通知、安装、shell、Shizuku、虚拟屏、录屏等）不注册即不可调用。
3. **权限与组件裁剪**：在 App 清单中用 `tools:node="remove"` 移除白名单用不到的权限与组件
   （通知监听服务、媒体投影服务、相应权限）。最终 APK 清单以 `aapt2 dump` 核对，写入兼容性记录。
4. **路径策略**：与 DSHBox 部署一致——guest 挂载点 `/opt/pilot`、宿主目录 `files/pilot`
   （DSHBox sandbox-manager 已为 DSH 与 guest 命令绑定该目录）；CLI 名 `relay`。
5. **审批**：`RelaySurfaces` 只提供**通知栏**一种呈现面（`RelayApproval.kt`：允许一次 / 本次会话允许 / 拒绝，
   不可导出的 receiver + 不可变 PendingIntent）；无悬浮窗权限、无页内卡。`uiInForeground()` 恒为 false——
   SPI 的含义是「宿主页内卡可用」而非「App 在前台」，按进程前台判定会让队列把问题路由到不存在的页内卡（真机实测）。
   通知被关闭时 `canShow()` 为 false，Relay 以 `E_GATE_NO_FOREGROUND` 失败关闭，绝不自动放行。
6. **契约层**：guest 内新增 Node MCP stdio 服务 `ui-facade.mjs`，按 ui_mcp 契约暴露
   `ui_list_devices` / `ui_snapshot` / `ui_find` / `ui_get_element`（V1 只读），`deviceId` 固定
   `android-local`；`snapshotId + refId` 只在当次快照有效，旧引用返回 `STALE_ELEMENT` 且不发底层请求；
   Relay 错误码按 ui_mcp「保守映射」原则归一，无法判定时返回 `OBSERVATION_INCOMPLETE` 并附原文。
   门面无依赖（手写 JSON-RPC），对 `server/discover` 回 `-32601`，DSH 的 MCP 客户端（`@modelcontextprotocol/client`
   2.2.0，`versionNegotiation: auto`）据此回落到 `initialize`。门面在 Relay 只报「用户尚未作答」
   （`E_AWAITING_CONSENT` / `E_GATE_WAITING_TURN`）时重试同一只读调用，最长 90 s；其余结果立即返回。
7. **注册到 DSH（不改 DSH、不碰用户 profile）**：开启中继时写入一条 `- insert:` 的
   `@deepseek-ai/dsh-mcp-client` 条目（`serverName: ui`，stdio，`toolCallTimeoutMs: 120000`），关闭中继 / 停止沙箱时删除。
   落点是未修改的 DSHBox sandbox-manager 固定传给 DSH 的宿主专属 `--patch` 层之一
   （`Constants.DSH_OFFICIAL_PLUGIN_OVERLAY_RELATIVE_PATH`）；DSH Mobile 不写其它宿主层，所以这一层只放这一条。
   文件首行带标记，只删自己写的；内容不是我们的则不覆盖不删除并在状态中报告冲突。patch 层在 DSH 启动时读取，
   界面如实提示「下次启动 DSH 时加载 / 需重启 DSH」。
8. **开放写操作的前置条件**：真机快照可读到通知栏里本 App 自己的审批卡（「允许一次」「本次会话允许」按钮）。
   开放 click 之前必须证明 Agent 无法点中本 App 的审批 UI（Relay 自带的「助手驾驶自身窗口」拒绝需在通知栏场景实测，
   不足则在门面 / 宿主侧按包名与节点拒绝），并补齐 ui_mcp R0–R4 分级。
9. **不并行造第二套工具**：不打包 DSHBox 的 `mobile-pilot` 插件；Agent 只看到 `ui_*`。

## 后果

- 无障碍服务必须由用户在系统设置中手动开启（部分 ROM 还需 App 内二次确认），App 不得代为开启。
- Relay core 的 Shizuku 依赖（`dev.rikka.shizuku:api/provider`，MIT）仍随包，但白名单不含 Shizuku 后端能力。
- Relay 升级跟随 DSHBox 子模块，单独 PR；能力白名单与清单裁剪需随之复核。
- Relay 单次调用只内联等待审批约 8 s；恰在第 8 s 附近点下的「允许」会被交给已返回的那一趟，重试会重新提问
  （真机复现一次）。失败方向是「再问一次」，不会替用户放行；属上游行为，未修改。
- 会话授权按能力计：`ui.snapshot` 与 `ui.node` 各需一次「本次会话允许」。
