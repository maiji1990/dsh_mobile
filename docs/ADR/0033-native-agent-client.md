# ADR-0033：纯原生 Android Agent 客户端架构与协议路线裁决（选择路线 B）

- **状态**：已接受（2026-11-22，PR-00 基线与 PoC 裁决）
- **关联**：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first 源码归属）、[ADR-0018](0018-remove-dshbox.md)（纯 C# 宿主）、[ADR-0021](0021-product-ui-material3.md)（Material 3 界面体系）、[ADR-0026](0026-file-providers-trash-conflicts-archives.md)（工作区文件台 V2）、[ADR-0028](0028-remove-native-terminal-and-plugin-pages.md)（插件页修订边界）、[ADR-0030](0030-proot-startup-fastpath.md) / [ADR-0031](0031-p2-perceived-startup.md)（P0–P2 启动快路径）、[ADR-0032](0032-android-17-local-network-permission.md)（Android 17 局域网权限）
- **实测依据**：[路线 A PoC 报告](../native/POC_ROUTE_A_SDK.md)、[路线 B PoC 报告](../native/POC_ROUTE_B_REMOTE.md)、[重构基线盘点](../native/BASELINE.md)

---

## 背景

`dsh_mobile` 当前依赖 `DshWebActivity`（Android System `WebView`）承载 DSH 官方前端 WebUI，存在以下结构性限制：
1. 冷启动在 PRoot/DSH 初始化（约 5.8s）之外叠加 WebView 进程冷启与主文档加载（实测可达 2.9s–7.5s），无法实现 Cache-First 秒开；
2. 后台审批、长代码块流式渲染、移动端 Hold-to-undo 防误触、中文 IME 与深色/配置变更保留受限于 WebView 页面生命周期；
3. 需在不重写 DSH AgentLoop / Session Log / Cordis Core（[AGENTS.md](../../AGENTS.md) 规则 1）的前提下，将聊天、凭据配置、会话管理、收件箱、工作区 Git 与轻量插件管理演进为纯原生 Material 3 客户端。

PR-00 对两条候选协议路线在 `@deepseek-ai/dsh@0.2.2-rc.5` 上进行了实测对比。

---

## 决策

### 1. 协议路线裁决：选择 **路线 B（无浏览器原生对接裁剪 `web` / Remote API）**

放弃路线 A（`dsh --profile sdk` stdio + 自研 Cordis Bridge），正式采用 **路线 B**（原生 C# 客户端经 `127.0.0.1` 对接裁剪后的 `dsh --profile web` Remote API：`/connect` + `/api/*` + `/api/remote.mux`）：

1. **单一官方会话所有者**：`ctx.sessionController` 是唯一会话所有者；路线 A 实测证明同进程 Bridge 调用 `ctx.agents.resume` 会被单写者句柄锁拒绝（`session "..." is already owned by an active write handle`）。
2. **协议能力完备且匹配锁定版本**：在 `0.2.2-rc.5` 中，`session-controller` 与 `settings-controller` 原生提供 `session/list`、`session/create`、`session/events`（冷读分页）、`session/follow`（opening snapshot + 增量事件 + `assistantStream`）、`session/prompt`（`requestId` 持久化幂等 + `clientTimeZone` + inline 图片）、`session/cancel`、`session/modelCatalog`、`session/selectModel`、`credentials/describe|set|unset`（零凭据引导闭环）及 `pluginInventory/list`；而 `sdk-jsonrpc-server` 缺少上述多数接口且拒绝 `timeZone`。
3. **传输与安全模型复用**：继续复用 `DshHost`（`127.0.0.1` loopback 不受 Android 17 `ACCESS_LOCAL_NETWORK` 拦阻，launch token 仅存宿主内存）、`ProotCommandBuilder` 的 ADR-0030 只读缓存与 `startup-fs-cache-shim.mjs`、ADR-0031 `DshHostPhase` / `GracefulStop`，以及 `RemoteSshSession`（ADR-0012 / ADR-0013）。
4. **生命周期约束**：`/connect?token=<launchToken>&interactions=answer` 交换得到的 `v2` Cookie 绑定到 `/api/remote.mux` WebSocket Peer 生命周期；`DshRemoteConnection` 必须在每个连接代次中先建立并保持 `/api/remote.mux` + `$events` `{ type: "ready" }` 栅栏，断线后重新执行 `/connect` 交换新 Cookie 并递增 generation。
5. **契约钉值约束**：为控制上游升级改动 Remote 内部协议的风险（规则 8），自 PR-02 起必须以脱敏真实录制 Golden Fixtures 钉住所有消费到的 `/api/*` 路由与 `$events` / `session/follow` 信封格式。

### 2. 分层与程序集归属

- 新增纯 `net11.0` 类库 `src/DshMobile.Agent`（`IsTrimmable=true`、`IsAotCompatible=true`，零 `Android.*` / `Java.*` 引用），承载 `Protocol/`（全量 `System.Text.Json` Source Generation）、`Domain/`（`Outcome<T>`、`DshFailure`、`DshCapabilities`、小 Facet Gateway 接口）、`Engines/`（`TranscriptEngine`、`RequestsEngine`、`SessionEngine`）、`Rendering/`（纯托管 Markdown AST/IR）与 `Presentation/`（纯 C# ViewModel / UiState / Actions）。
- 新增 `tests/DshMobile.Agent.Tests`，自 PR-00 起启用 5 项 CI 源码/架构/安全棘轮门禁（`ArchitectureBoundaryTests`、`TrimAndSerializationGateTests`、`CancellationAndFailureGateTests`、`SavedStateSecurityTests`、`LoggingPrivacyTests`）。
- 选定 **Markdig**（锁定精确版本 `1.6.0`）作为纯托管 Markdown 解析库，实测兼容 `.NET 11 TrimMode=full`。

### 3. 平台基线与渐进式移除 WebView

- **最低系统版本**：保持 `SupportedOSPlatformVersion = 29`（Android 10+），主真机验证目标为 Android 17 arm64。
- **渐进式切换与零 WebView 终态**：PR-01 ～ PR-08 期间保留 `DshWebActivity` 作为可回滚并行入口；在全部 P0 验收场景（T01–T12，含原生凭据配置、在线更新探针 `IDshHost.ProbeHealthAsync`、远程主机 `RemoteSshSession` 原生对接）真机通过后，于 PR-09 切换默认主入口并彻底移除 `DshWebActivity`、`CSharpWebUiController`、废弃 WebView 策略与布局，并启用 `NoBusinessWebViewGateTests`。外部网页预览显式交由受控系统浏览器处理。

### 4. 对 ADR-0028（插件管理边界）的修订

[ADR-0028](0028-remove-native-terminal-and-plugin-pages.md) 曾因“维护两套插件语义与前端 UI”移除原生插件页。本 ADR 予以如下受控修订：
- 一期仅恢复 **L0（只读目录与状态：`pluginInventory/list`）+ L1（复用 DSH 官方规则的受控启停/装卸及明确重启提示）**；
- **明确不做 L2（不自创第三方插件动态表单 Schema，不内嵌插件 WebUI）**；携带自定义 WebUI 的插件明确提示“插件后端正常运行，但暂无原生配置界面”。
- 原生终端页继续保持移除状态（ADR-0028 不变）。

---

## 后果

- 正面：`DshMobile.Agent` 完全脱离 Android SDK 可在桌面秒级单测；复用官方 `session-controller` / `settings-controller` 避免自研会话控制器与 write-handle 冲突；Cache-First + 流式 16 行切块可彻底消除 WebView 冷启动与卡顿问题。
- 约束：需通过 `--patch` 禁用浏览器 UI 行（PoC 实测启动快 ~249ms、RSS 降 ~15–20 MiB），并在上游 DSH 升级时执行 Golden Fixtures 回归对照。
