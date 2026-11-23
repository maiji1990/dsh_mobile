# PR-04 原生聊天垂直切片验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（N-04-01 ～ N-04-05）
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 5、9，无真机日志或截图前一律标为「待真机验证」。
> 真机完整报告：[ACCEPTANCE_PR00_PR04_DEVICE.md](ACCEPTANCE_PR00_PR04_DEVICE.md)（2026-11-23，M2007J1SC / Android 17 / DSH 0.2.2-rc.5；第 8 节为修复后复测）

## 1. 桌面自动化与构建验证

| 检查项 | 范围 | 结果 |
|---|---|---|
| 纯 net11.0 呈现层、协议、连接与 Markdown IR 单测 | DshMobile.Agent.Tests（ChatViewModelAndMarkdownTests、RemoteWireCodecTests、RequestsEngineTests、TranscriptFaultAndReconcileTests、DshRemoteConnectionTests 等） | 通过（61/61：瀑布帧 `request`/`cancel`、晚到 `tool/call` 回填、内部事件过滤、`source.kind` 过滤、`session/title`、`FromSessionError`、斜杠 `/` 前缀、宿主停机排空重连恢复） |
| 架构与安全门禁 | ArchitectureBoundaryTests、TrimAndSerializationGateTests、CancellationAndFailureGateTests、SavedStateSecurityTests、LoggingPrivacyTests | 全绿 |
| DshMobile.Android Release 全量裁剪构建 | dotnet build src/DshMobile.Android -c Release（TrimMode=full） | 通过（0 错误；0 新增 Trim 警告；原 3 条 `CS8604` 已清零） |
| N-08-01 并行提前项 | ProcessTable + NativeOrphanReaper + SupervisionTests | 全绿（Runtime 832 通过 / 2 跳过） |

## 2. Android 17 arm64 真机验收矩阵（2026-11-23 修复后复测）

| 验收项 | 期望行为 | 真机状态 |
|---|---|---|
| 并行入口 | 首页可打开「打开原生会话（实验）」，既有「打开 DSH（Web）」正常 | **通过**：两个入口均可用，WebView 可加载 DSH Web UI |
| 流式输出与 Markdown | 段落、标题、列表、表格、Diff 与一键复制代码块流式无卡顿 | **通过**：表格、代码块（含复制）、思考过程卡正常；流式 917 帧 / 2 janky（0.22%）/ p90 11 ms |
| 滚动锚点 | 上滑锁止自动滚动，浮钮出现；点击回到底部并恢复跟随 | **通过** |
| 审批与问答 | 正确展示工具名与参数摘要，Hold-to-undo 可撤销，拒绝零执行 | **通过**：卡片显示「执行工具 `bash`」与参数 JSON（F9 已修复）；允许/撤销/拒绝均可用；「拒绝后零执行」仍缺设备侧独立证据 |
| 图片与斜杠命令 | SAF 图片校验发送；斜杠命令执行正常 | **通过**：图片视觉问答正常；芯片显示 `/compact` 并返回「Compacted 12 history items (~1643 tokens).」（F10 已修复） |
| 配置变更与 IME | 深色切换、横竖屏旋转、中文输入法组合上屏、返回键正常 | **部分通过**：深色原地切换已生效（F2 修复，截图哈希不同）、旋转保留草稿与会话、IME `adjustResize`（竖屏）、返回键正常；**横屏 + IME 仍不可用（F6）**；设备无中文 IME，组合上屏未验证 |
| 对话可读性 | 时间线以用户/助手/工具/审批为主体 | **通过**：一轮对话仅含用户消息、助手文本与工具卡，无 `event.unknown`/`agent/inbox/spliced`/`job/*` 卡片，系统提示不再渲染（F5/F11/F12 已修复） |

## 3. 真机缺陷与修复状态（详见 [真机报告](ACCEPTANCE_PR00_PR04_DEVICE.md) 第 4、7、8 节）

> **复测状态（2026-11-23 12:05–12:12，APK `92E6E007…`）**：`run-pr00-04.ps1` 矩阵 **PASS 18 / FAIL 0 / INFO 2**。

- **F1 已修复**：断连横幅显示「DSH 未运行或连接已断开（STOPPED）」。
- **F2 已修复**：移除 `ConfigChanges.UiMode`，深色/浅色原地切换生效且草稿/会话保留。
- **F3 单测覆盖**：`DshFailureMapper.FromSessionError` 分类透传；真机未复现会话级错误，未取得端到端样本。
- **F4 已修复**：真机 `startup-perf` 现已记录 `protocol.connected` / `protocol.initialized`。
- **F5/F11/F12/F13 已修复**：事件词表与静默忽略、`source.kind` 与 `<system-reminder>` 过滤、`session/title` 同步顶栏标题。
- **F6 未修复**：横屏 + 软键盘时 `RecyclerView` 为 `[90,442][2340,906]`，完全被 IME 覆盖不可见；IME 关闭时仅约 209 px 高。
- **F9 已修复**：审批卡显示真实工具名 `bash` 与参数 JSON；允许后工具卡显示「已完成」。
- **F10 已修复**：芯片命令自带 `/`，`/compact` 正常执行。
- **F14 已修复**：DSH 停止再启动后可正常重连。

## 4. 结论

11 项缺陷中 **F1/F2/F4/F5/F9/F10/F11/F12/F13/F14 已真机复测通过**，F3 由单测覆盖；**F6（横屏 + 输入法）仍未修复**，且设备缺少中文输入法无法验证组合上屏。因此在 F6 处理与中文 IME 验证前，`N-04-05` 保持 `[ ]`。
