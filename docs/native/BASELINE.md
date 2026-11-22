# DSH Mobile 原生重构基线盘点（N-00-01 / N-00-05）

- **基线记录日期**：2026-11-22
- **仓库基线**：`main` @ `cd0fbd3`（Merge PR #71：`feat/android17-local-network-permission` / ADR-0032）
- **决策依据**：[ADR-0033](../ADR/0033-native-agent-client.md)、[DSH_Mobile_原生Android重构实施方案](../plan/DSH_Mobile_原生Android重构实施方案.md)

---

## 1. 工具链与运行层锁定基线

| 维度 | 锁定值 | 来源 |
|---|---|---|
| .NET SDK / Workload | `.NET SDK 11.0.100`，`workloadVersion 11.0.100.1`，CoreCLR，`PublishAot=false`，`TrimMode=full` | [global.json](../../global.json)、[DshMobile.Android.csproj](../../src/DshMobile.Android/DshMobile.Android.csproj) |
| Android 边界 | `SupportedOSPlatformVersion = 29`（Android 10+），NDK `27.0.12077973`，ABI `android-arm64;android-x64` | [DshMobile.Android.csproj](../../src/DshMobile.Android/DshMobile.Android.csproj) |
| 运行层锁定 | Debian `trixie`（snapshot `20261110T000000Z`）、Node `26.13.1`、DSH `0.2.2-rc.5`、Runtime Bundle `0.2.1` | [runtime-build.lock](../../runtime-bundle/runtime-build.lock)、[runtime-assets.lock](../../runtime-bundle/runtime-assets.lock) |

---

## 2. PR-00 基线测试与构建数据

在引入任何重构代码前（`cd0fbd3`）实测基线：

| 检查项 | 实测结果 |
|---|---|
| `DshMobile.Core.Tests` | `93` passed, `0` failed, `0` skipped |
| `DshMobile.Runtime.Tests` | `828` passed, `0` failed, `2` skipped（`RemoteSshOpenSshTests` 需真实 Windows OpenSSH） |
| `DshMobile.Application.Tests` | `131` passed, `0` failed, `0` skipped |
| `node tooling/run-node-tests.mjs tests/guest` | `48` passed, `0` failed |
| `node tooling/run-node-tests.mjs tests/tooling` | 全部通过（含 manifest / startup-perf / upstream-update） |
| `dotnet build src/DshMobile.Android -c Release`（`cd0fbd3` 清洁基线） | `0` errors，`116` warnings（含底层 `Core/Runtime/Application` 历史 CA/IL 告警；注：方案草案沿用的 `75` 为 2026-11-16 PR-01 `#44` 时点历史值），Signed APK `39,519,574` bytes（`37.69 MiB`，未内置运行层） |

---

## 3. 真机启动性能基线（Honor PTP-AN10 / Android 17）

引自本仓库已归档真机实测（本机无连接真机，PR-00 未重新采真机）：
- **2026-11-17 A/B/C 各 10 次基线**（[2026-11-17-PTP-AN10-A17-startup-perf.md](../../migration/parity/2026-11-17-PTP-AN10-A17-startup-perf.md)，DSH `0.2.2-rc.3`）：
  - 场景 B `bridge.complete` P50 `16,557.5 ms`（P95 `18,388.6 ms`），其中 `node_init` 占 `97.6%`；
  - 场景 C `bridge.complete` P50 `17,179.4 ms`（P95 `17,588.0 ms`）；
  - 场景 A `web.page_finish` P50 `281.5 ms`（P95 `7,482.1 ms` 含 WebView 冷启离群）。
- **2026-11-21 P1 PRoot 只读前缀缓存生产配对**（[2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md](../../migration/parity/2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md)，DSH `0.2.2-rc.4`）：
  - `bridge.complete` 由均值 `7,797.9 ms` 降至 **`5,842.4 ms`**（P50 **`5,792.5 ms`**，n=12 配对 `−1,955.5 ms` / `−25.1%`，p=0.0005）。
- **2026-11-21 P2 体感启动报告**（[2026-11-21-PTP-AN10-A17-P2-perceived-startup.md](../../migration/parity/2026-11-21-PTP-AN10-A17-P2-perceived-startup.md)）：
  - 冷启点击→READY `4,876 ms` + WebView `web.page_finish` ~`2,900 ms`；开启预热后只剩 WebView ~`2.9 s`，优雅停机 `2.36 s` 无残留。

---

## 4. Markdig 纯托管 Markdown 选型与 `TrimMode=full` 验证（N-00-05）

- **选定包与精确版本**：`Markdig` `1.6.0`（MIT/BSD-2-Clause，纯托管），引入纯 `net11.0` 程序集 [DshMobile.Agent.csproj](../../src/DshMobile.Agent/DshMobile.Agent.csproj)。
- **验证方式**：在 `DshMobile.Agent.Rendering.MarkdownParser` 中启用 PipeTables、TaskLists、AutoLinks、EmphasisExtras 解析标题/段落/GFM 表格/围栏代码块/任务列表，并在 `BridgeHost.RunDiagnosticsAsync()` 中直接调用 `MarkdownParser.InspectSummary` 与 `AgentProtocolJsonContext`（确保 ILLink `TrimMode=full` 不把程序集裁剪掉）。
- **实测结果**：
  - `DshMobile.Agent.Tests`（Release）：`12 passed, 0 failed`（含 `MarkdownParser_ParsesHeadingsFencedCodeTablesAndLists` 与 STJ SourceGen 往返测试）；
  - `dotnet build src/DshMobile.Android -c Release`：`0` errors；**`Markdig` 与 `DshMobile.Agent` 产生的 Trim/AOT/编译器告警均为 `0`**；
  - APK 体积变化：`io.github.maiji1990.dshmobile-Signed.apk` 从 `39,519,574` bytes 增至 `40,289,622` bytes（双 ABI arm64 + x64 合计 **`+770,048` bytes / `+752.0 KiB`**）。

---

## 5. 现有 WebUI 功能与原生替代状态矩阵

> 原则：PR-00 ～ PR-08 期间完整保留 `DshWebActivity` 与全部既有能力不删代码；仅 PR-09 全量真机验收通过后才清理 WebView。

| 功能域 | 现状实现（`cd0fbd3`） | 原生目标切片 | 当前状态 |
|---|---|---|---|
| 会话列表 / 新建 / 搜索 / 重命名 | `DshWebActivity`（官方 WebUI） | PR-05 `ChatsFragment` + `SessionEngine` | 未开始（保留 WebView） |
| 聊天流式输出 / Markdown / 代码 / Diff | `DshWebActivity`（官方 WebUI） | PR-03 `TranscriptEngine` + PR-04 `ChatFragment` | 仅完成 Markdig 选型验证 |
| 图片附件 / 原生斜杠命令 | `DshWebActivity` + `DocumentPicker` | PR-02/04 `session/prompt` + SAF 选图 | 未开始（保留 WebView） |
| 工具审批 / 用户问答 | `DshWebActivity` | PR-03 `RequestsEngine` + PR-04/06 卡片/通知 | 未开始（保留 WebView） |
| Provider / 模型目录 / API Key 凭据配置 | `DshWebActivity`（设置页） | PR-05 `CredentialsFragment` + `credentials/*` | PoC B 已验证底层 RPC |
| 工作区文件浏览 / 编辑 / 归档 / 回收站 | `FilesFragment` + `WorkspaceProvider` V2 | PR-06 增量叠加 `WorkspaceGitService` + Diff | **已有原生 V2** |
| 手机操控 `ui_*` 与 Relay 审批 | `AutomationActivity` + `CsRelay` + `RelayApprovalConfirmActivity` | 保持不变，与 DSH 审批双安全域隔离 | **已有原生实现** |
| 远程主机 SSH | `RemoteActivity` + `RemoteSshSession` | PR-08 原生接入 `IDshGateway` | **已有原生 SSH 壳** |
| 运行层导入 / 在线更新 / 回滚 | `RuntimeActivity` + `RuntimeCoordinator` + `UpdateCoordinator` | PR-08 `IDshHost.ProbeHealthAsync` 抽象探针 | **已有原生实现** |
| Android 17 本地网络权限 | `LocalNetworkAccess`（ADR-0032） | 保持不变 | **已有原生实现** |
| 插件状态与受控管理（L0/L1） | 无（ADR-0028 移除） | PR-07 `PluginsFragment`（仅 L0/L1，不做 L2） | 未开始 |
