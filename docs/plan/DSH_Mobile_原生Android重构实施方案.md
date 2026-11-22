# DSH Mobile 原生 Android 重构实施方案（V2）

> 方案版本：V2.0  |  编制基线：2026-11-22（`main` @ `cd0fbd3`，合并 PR #71 / ADR-0032）  
> 技术路线：`.NET 11 for Android`（CoreCLR / `TrimMode=full`）+ C# + Android XML / Material 3 + 原版 DSH（PRoot / Debian / Node）  
> 性质：**技术架构与分阶段实施计划**；不是已提交代码或已完成真机验收的产物。所有兼容性、启动耗时与帧率结论均须以真机证据为准（[AGENTS.md](../../AGENTS.md) 规则 5、9）。

---

## 0. 执行摘要与核心变更

**重构目标**：将 `dsh_mobile` 从「C# Android 宿主 + PRoot 内运行 DSH Web + WebView 承载聊天/设置」演进为**完全原生 Android 客户端**。保留现有 C# / .NET 11 Android 宿主、Material 3 界面体系、自建 PRoot 与 P0–P2 启动快路径（ADR-0014 / 0030 / 0031）、Debian / Node / 原版 DSH 运行层、签名在线更新事务（ADR-0022 / 0024 / 0029）、文件工作台 V2（ADR-0026）、Interlock Relay `ui_*` 手机操控（ADR-0004 / 0008 / 0010）、远程 SSH（ADR-0012 / 0013）与 Android 17 本地网络权限（ADR-0032）；**移除业务 WebView（`DshWebActivity`）依赖**。

**相对 V1 草案的六项关键改进**（基于对 [`Eslamasabry/opencode-mobile-next`](https://github.com/Eslamasabry/opencode-mobile-next)、[`deepseek-ai/deepseek-harness`](https://github.com/deepseek-ai/deepseek-harness) 源码与本仓库最新代码的交叉核验）：

1. **前置协议路线裁决（路线 A vs 路线 B）**：不再盲目锁定「`sdk` stdio + 自研 Cordis Native Bridge」。PR-00 必须先以 PoC 对比 **路线 A（`sdk` stdio + 同进程 Bridge）** 与 **路线 B（裁剪版 `web`/Remote API，无浏览器直接对接 `/api` + `/api/remote.mux`）**，避免重造整套 `dsh-api-session-controller` / `settings-controller` 或撞上 `HarnessSdkJsonRpcServer` 私有会话表所有权冲突。
2. **补齐 P0 级产品闭环**：新增「原生 Provider / 模型凭据配置与首次引导」切片（解决删除 WebView 后新装用户无处填 API Key 的死结），并覆盖在线更新 `probing` 探针与远程主机 `RemoteSshSession` 的原生切换。
3. **彻底吸收 OpenCode Mobile Next 的真机工程精华**：将 **Cache-First 首屏（`detachedTranscript`）**、**16 行硬切片流式 Markdown（`StreamingBlocks`）**、**50ms 前缘/后缘合并与无观察者跳过派生**、**审批 Hold-to-undo 与已结算账本**、**`NativeOrphanReaper` 多条件孤儿清理**、**Android 32 Phantom Process 预算 + Android 17 Memory Limiter 归因 + 温控阶梯** 以及 **CI 自动化架构/安全棘轮门禁（Ratchet Tests）** 全面落入设计。
4. **严格对齐 DSH 真实协议与 `.NET 11 TrimMode=full`**：补齐 `SdkEncodedImageBlock` 图片附件、`timeZone` 强制透传、`session/query` 原生斜杠命令、`approval/request` 按 `callId` 回溯 `session.event` 参数、`turn/end` `reason` 终态判定；协议序列化强制采用 `System.Text.Json` Source Generation（严禁 `object` 反射）。
5. **深度复用本仓库已有成果，不另起炉灶**：修正目标系统为 **Android 10+（API 29）**；复用 `ProotCommandBuilder` 的 ADR-0030 只读缓存与 `startup-fs-cache-shim.mjs`、ADR-0031 的 `DshHostPhase` / `WarmupPolicy` / `GracefulStop`、以及 `StartupTrace` + `tooling/startup-perf.mjs` 统一观测流水线；控制面 `_op` 锁与数据面 RPC 并发彻底隔离。
6. **务实裁剪低收益范围**：对齐 [ADR-0028](../ADR/0028-remove-native-terminal-and-plugin-pages.md) 教训，插件管理一期只做 **L0（目录/状态）+ L1（复用官方规则的启停/装卸）**，**砍掉自创的 L2 第三方插件表单 Schema**。

| 基准项 | 当前确认值 |
|---|---|
| 仓库基线 | `maiji1990/dsh_mobile`，`main` @ `cd0fbd3` |
| .NET 工具链 | .NET SDK `11.0.100`，workload `11.0.100.1`，CoreCLR，`TrimMode=full`，`PublishAot=false` |
| NDK / 系统边界 | NDK `27.0.12077973`，`SupportedOSPlatformVersion = 29`（Android 10+），主验收机 Android 17 arm64 |
| 参照项目 | `Eslamasabry/opencode-mobile-next`（`master` 分支 `native/` 与 `docs/design/native-rewrite-2026-11-01/`） |
| 上游 DSH 协议依据 | `deepseek-ai/deepseek-harness`（`dsh-sdk-protocol`、`dsh-sdk-jsonrpc-server`、`dsh-sdk-app`、`dsh-client-connection`、`dsh-api-session-controller`） |

---

## 1. 架构决策（ADR-0033 预案）与协议路线裁决

### 1.1 目标与非目标

**产品目标：**

1. 在 **Android 10+（API 29+）** 设备上，使用纯原生 Material 3 界面完成 Agent 会话、实时流式输出、图片附件、工具调用展示、工具审批、结构化用户问答、模型与凭据配置、会话管理、工作区文件/Git 查看、技能与受控插件管理。
2. **Cache-First 秒开体验**：App 启动 0.2s 内直接渲染本地缓存的最近会话与工作区，允许立即阅读与起草输入；后台通过 PRoot 内的 Debian/Node.js 启动原版 DSH（复用 P0–P2 约 5.8s 快路径），就绪后无闪烁交接并增量校准。不把“UI 可打开”伪装为“Agent 已就绪”。
3. 原生 UI、领域引擎与后端传输彻底分离；本地 DSH 与远程 SSH DSH 经同一 `IAgentGateway` 抽象接入，不重写页面。
4. 完整应对系统回收、深色模式/配置变更、服务异常退出、DSH 重启、连接断开、32 进程上限与 Android 17 内存限制，确保**状态可恢复、不重复执行危险工具、不遗留孤儿进程**。
5. 完整保留 Cordis 插件后端运行能力、`ui_*` 手机操控单写者契约与签名在线更新回滚事务。

**明确非目标：**

- 不重写 DSH TypeScript AgentLoop、Session Log 或 Cordis Core（[AGENTS.md](../../AGENTS.md) 规则 1）；
- 不迁移 Kotlin / Jetpack Compose，不引入 MAUI、Gradle 或第三方桥接 AAR（规则 2 / ADR-0001 / ADR-0018）；
- 不通过截屏、无障碍读屏或解析 TUI 文本来驱动 Agent；
- 不自动把第三方 Cordis 插件的 HTML/WebUI 转换为 Android XML，**一期不自创 L2 第三方插件表单 Schema**；
- 不把 PRoot 描述或依赖为内核安全沙箱；不承诺 Android 8/9 兼容（受限于 API 29 基线与 Interlock Relay）。

### 1.2 核心协议路线对比（PR-00 必须裁决）

核验 `deepseek-harness` 真实源码后，当前存在两条可落地的原生接入路线，**禁止未经 PoC 验证就假定某条路线无缺口**：

| 维度 | 路线 A：`dsh --profile sdk`（stdio JSON-RPC）+ 自研 Cordis Native Bridge | 路线 B：无浏览器原生对接裁剪 `web`/Remote API（`/api` + `/api/remote.mux`） |
|---|---|---|
| **协议定位** | `sdk` 是上游明确承诺 wire-stable 的公开协议（`types.ts`） | `dsh-client-connection` + `dsh-api-session-controller` 是官方 Web/Desktop 前端使用的内部 Remote 协议 |
| **会话列表 / 历史 / 取消 / 换模型** | SDK 均缺失；`dsh-sdk-app` 未加载 `session-controller` / `settings-controller` / `agentPresets`；**必须在自研 Bridge 中补齐** | **官方后端全部原生提供**（含冷读列表、历史分页、follow opening 快照、`turnOutline`、取消、模型目录与切换、preset、Agent Teams） |
| **会话所有权风险** | **高**：`HarnessSdkJsonRpcServer` 私有持有 `sessions` Map，Bridge 经 `ctx.agents` 操纵易触发 `assertLiveAgent` 脱节 | **无**：单一官方 `ctx.sessionController` 作为唯一会话所有者 |
| **Prompt 幂等去重** | SDK 无服务端幂等键，断线只能标 `UnknownOutcome` | `session-controller` **原生支持 `requestId` 持久化幂等去重** |
| **凭据与模型配置** | SDK 无接口，需额外通过 CLI 或 Bridge 暴露 `credentials` / `settings` | `dsh-api-settings-controller` 原生支持设置与凭据域读写 |
| **远程主机兼容** | 需重写 `RemoteSshSession`（由端口转发改为 SSH stdio 流式管线） | **天然兼容现有 `RemoteSshSession`**（远端 `dsh --profile web` + 本地回环转发） |
| **网络/进程特性** | 纯 stdio 管道，不占回环端口；但需防护插件污染 stdout | 仅监听 `127.0.0.1` + 启动 token 换签名 Cookie（Android 17 loopback 不受 `ACCESS_LOCAL_NETWORK` 拦阻）；需 `--patch` 禁用 `frontend-static` / 浏览器 UI 行以省启动开销 |
| **上游升级风险** | 低（SDK 公开线协议稳定），但自研 Bridge 依赖 Cordis 内部服务同样有上游兼容成本 | 中（Remote 方法名与事件 envelope 需在 DSH 升级时做契约钉值校验，类似现有 `UpstreamPinnedValuesTests`） |

**裁决规则（写入 ADR-0033）：**
- **PR-00 必须完成一项 2 天内的轻量 PoC**：
  1. 实测路线 B：用 `--patch` 禁用 `frontend-static` 与浏览器 UI 名录启动 DSH，测其 `httpReadyMs` 与 `/api` + `/api/remote.mux`（`$events` `ready` baseline + `session-controller`）的协议清晰度与可钉值性；
  2. 实测路线 A：在 `dsh --profile sdk` 中挂载最小 Cordis 扩展，验证其能否安全读取/取消/切换 `HarnessSdkJsonRpcServer` 拥有的活跃会话且不触发 `assertLiveAgent` 异常，并验证零凭据首次启动如何通过 `initialize`。
- **无论选择路线 A 还是路线 B，上层架构（`DshMobile.Agent` 的 Gateway Facets、Capabilities、TranscriptEngine、RequestsEngine、Cache-First 与原生 Android UI）完全一致**；只有底层 `IDshGateway` 的传输适配器不同。下文同时给出两条路线在传输层的落地规范。

### 1.3 必须遵守的硬性约束

- **C#-first 与单一实现（AGENTS.md 规则 2）**：Android XML / Material 3 只做薄 View，业务逻辑、状态机、权限判定与测试全部留在纯 `net11.0` C# 类库中。
- **零反射与 Full Trim 安全**：[DshMobile.Android.csproj](../../src/DshMobile.Android/DshMobile.Android.csproj) 启用 `TrimMode=full`。所有 JSON 协议序列化必须使用 `System.Text.Json` Source Generation（`[JsonSerializable]`），严禁 `object` 参数序列化。
- **单运行时所有者 + 锁分离**：本地最多存在一个活跃 DSH 进程；`RuntimeCoordinator._op` 控制面锁仅串行启停/导入/更新/回滚，**绝不阻塞数据面 RPC 与事件流**。
- **不伪造成功（规则 5）**：缺失能力由 `DshCapabilities` 隐藏/禁用或返回明确 `Unsupported`；所有跨层异步操作返回强类型 `Outcome<T>`，不吞 `OperationCanceledException`，绝不把原始 `ex.Message` 直接展示到 UI。
- **安全失败关闭（规则 11）**：审批超时、切后台、界面销毁、连接断开一律不得默认允许；敏感数据（凭据、token、Prompt 正文、文件内容、审批敏感参数）严禁写入日志、CSV、遥测或 `OnSaveInstanceState`。

### 1.4 参照 OpenCode Mobile Next 的取舍边界

| OpenCode Mobile Next 做法 | 本项目取舍 |
|---|---|
| Kotlin Multiplatform + Compose | **不照搬**：保持 `.NET 11 for Android` + C# + Android XML / Material 3 |
| OpenCode 2 HTTP/SSE 协议与 Ubuntu RootFS | **不照搬**：沿用本仓库自建 PRoot + Debian 13 + Node + DSH 层与签名更新链 |
| `KitWebPreview.kt` 内嵌 WebView 预览 | **不照搬**：彻底不依赖业务 WebView；HTML/网页显式交给受控系统浏览器 |
| `Gateways` 小 Facet 拆分 + `Outcome<T>` + `ServerCapabilities` | **完整复刻**：避免上帝接口，UI 只认能力位不认后端类型 |
| `TranscriptEngine` + `detachedTranscript` Cache-First + 50ms 合并 + `skippedWhileHidden` + `TranscriptCounters` | **完整复刻**：用 Cache-First 掩盖 PRoot ~5.8s 冷启动，用单测计数器防性能退化 |
| `StreamingBlocks` 16 行硬切片 | **完整复刻**：彻底解决长代码块/长回复流式卡顿与拷贝爆炸 |
| `RequestsEngine` Hold-to-undo 缓冲窗 + 已结算账本 | **完整复刻**：防移动端误触审批、防断线重拉导致已答复卡片复活 |
| `WorkLeases` + `IdleStopPolicy` + `NativeOrphanReaper` + 32 进程预算 + Android 17 Memory Limiter + `HeatTierPolicy` | **完整复刻**：补齐本仓库 `ProcessTable` 与后台长期运行的系统治理短板 |
| `native/quality/` 自动化架构/安全棘轮门禁（`Konsist` / `RestoreGuards` / `PrivacyLeakGuard` / `CacheFirstGate`） | **用 C# 源码扫描 xUnit 复刻**：把规范焊进 CI，只降不升 |

---

## 2. 现状基线与协议核验清单

### 2.1 本仓库现有资产与必须衔接的关键点

| 现有组件 | 现状与关键事实 | 重构衔接要求 |
|---|---|---|
| [DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs) | 已实现 sandbox keepalive、DSH 启动、launch token、`DshHostPhase`（ADR-0031）、`GracefulStop`（SIGTERM 1.8s）、健康循环 | 复用 `DshHostPhase` 与 `GracefulStop`；抽象 `IDshHost` 探针以同时支持在线更新验证与新原生宿主 |
| [ProotCommandBuilder.cs](../../src/DshMobile.Runtime/Guest/ProotCommandBuilder.cs) | 已接入 ADR-0030 只读 dentry 缓存（`PROOT_RO_CACHE_PREFIXES=/opt/dshapp/runtime:/usr/local`、`PROOT_RO_CACHE_NONEXIST=0`）、`startup-fs-cache-shim.mjs`（生产冷启动 17.7s → **5.8s**）及 `--patch`（含 `ui_*`） | **新进程启动必须复用上述全部优化与 `--patch`**，并修复 `inPlaceRestart` 遗漏 `guestCache` 挂载的历史差异 |
| [ProotGuestProcess.cs](../../src/DshMobile.Runtime/Guest/ProotGuestProcess.cs) | 面向一次性命令，启动即关闭 stdin 且合并 stdout/stderr | **保持不变**（服务诊断、`dsh-probe`、PTY 自检）；若走路线 A，新增独立 `ProotSdkProcess` |
| [ProcessTable.cs](../../src/DshMobile.Runtime/Supervision/ProcessTable.cs) | `stat.IndexOf(") ")` 解析有缺陷；仅看 `ppid` + `cmdline`，缺 `uid` / `startTicks` / `TracerPid` / `exe` | 升级为 `NativeOrphanReaper` 多条件交集判定，清理 PRoot 被系统 SIGKILL 后遗留的孤儿 guest 进程 |
| [RuntimeCoordinator.cs](../../src/DshMobile.Application/Runtime/RuntimeCoordinator.cs) | 单把 `_op` 锁串行启停/导入/更新/回滚；更新 `probing` 硬编码 `StartHostAsync(DshProfile.Web)` + HTTP 401 | 保持唯一运行时编排权；数据面不持 `_op` 锁；更新 `probing` 抽象为 `IDshHost.ProbeHealthAsync` |
| [WorkspaceProvider.cs](../../src/DshMobile.Core/Files/WorkspaceProvider.cs) | 已实现文件工作台 V2（ADR-0026：`.dsh` 双向不可触、冲突检测、回收站撤销、安全压缩解压、3 万文件列表） | 直接复用为原生「工作区」页基础，增量叠加 Git 状态与 Diff 视图 |
| [RemoteSshSession.cs](../../src/DshMobile.Runtime/Remote/RemoteSshSession.cs) | Keystore 不可导出 ECDSA + 主机指纹固定；当前远端启动 `dsh --profile web` + 本地回环转发 | PR-09 删除 WebView 前必须明确远程主机的原生会话对接（路线 B 可直接复用；路线 A 需扩展 SSH 通道） |
| [dsh-probe.mjs](../../src/DshMobile.Runtime/Guest/Assets/dsh-probe.mjs) / `dsh-e2e.mjs` | 真机已证 `sdk` / `sdk-minimal` 握手 5.1s 通过；实测发现 **失败轮次 `session/wait` 仍返回 `{}`，错误在 `turn/end` 的 `reason` 里**（[VNEXT_STATUS.md](../VNEXT_STATUS.md)） | 将该真实行为纳入契约测试与 `TranscriptReducer` 终态判定 |
| [MainActivity.cs](../../src/DshMobile.Android/MainActivity.cs) | Material 3 三栏（首页/文件/更多）+ 600dp `NavigationRailView`；`ConfigurationChanges` 未包含 `UiMode`（深色切换会重建） | 平滑演进为原生导航壳；ViewModel 存活于 `DshMobileApplication` ConnectionScope，不受 Activity 重建影响 |
| [STARTUP_PERF.md](../STARTUP_PERF.md) / `tooling/startup-perf.mjs` | 已有 `StartupTrace` + 脱敏 `startup-perf` JSON schema v1 + 12 列 CSV / P50 / P95 工具链 | **直接扩展阶段 id**（新增 `sdk.*` / `chat.*` / `cache.*`），不另造互不兼容的埋点格式 |

### 2.2 DSH 真实协议核验补充（相对 V1 漏掉的细节）

核验 [`packages/sdk/protocol/src/types.ts`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/sdk/protocol/src/types.ts) 与 [`packages/sdk/server/src/server.ts`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/sdk/server/src/server.ts)：

1. **图片附件已原生支持**：`session/prompt` 和 `session/query` 均接受 `SdkEncodedImageBlock`（`image/png|jpeg|webp|gif` base64），服务端自动提交至持久化附件存储。原生聊天一期即可支持选图发送。
2. **必须透传 `timeZone`**：`session/prompt` 与 `session/query` 支持 `timeZone`（`UTC` 或标准 IANA 名称如 `Asia/Shanghai`）。不传时 PRoot 容器常上报 UTC，客户端每次发送必须传入 Android 设备当前 IANA 时区。
3. **原生斜杠命令立即可用**：`session/query` 已原生分发 `/compact`、`/permission`、`/plan`、`/goal` 等命令并通过 `session.query.completed` 通知完成，无需等 Bridge 即可在原生 UI 提供对应入口。
4. **`session/resume` 严格校验**：支持可选 `permissionMode`，但服务端会直接拒绝任何未知参数字段，且已被定时提醒恢复的会话禁止覆盖 `permissionMode`（需改走 `/permission`）。
5. **审批载荷不含工具参数**：`approval/request` 仅含 `sessionId`、`toolName`、`callId?`、`reason?`。客户端必须通过 `callId` 从已收到的 `session.event` 流中关联对应工具调用的参数（命令/路径）再渲染给用户；未关联到时必须醒目提示“参数尚未加载，请谨慎授权”。
6. **轮次失败判定**：不能只看 `session/wait` 返回 `{}`，必须解析 `session.event` 中 `turn/end` 的 `reason` 字段识别失败与中断。

---

## 3. 目标总体架构与项目分层

```text
DshMobile.Android (net11.0-android, Material 3 + Android XML)
  ├── Shell: MainActivity (BottomNav / 600dp NavigationRail) + Global Banner/Inbox Badge
  ├── Screens: Chats / Chat / Onboarding+Credentials / Workspace(Files+Git) / Inbox / Plugins / Runtime / Remote
  └── Platform Services: ForegroundService, Relay (ui_*), Keystore SSH, SAF, Notifications,
                         ExitClassifier (A17 MemoryLimiter), Thermal/Battery, ProcessBudget

        ▲ 仅依赖不可变 UiState 与强类型 Actions（ConnectionScope 持有，不受重建影响）
        │
DshMobile.Agent (新增，纯 net11.0，零 Android 依赖，完整 xUnit 单测)
  ├── Protocol/        # STJ SourceGen DTO、线协议编解码（JSON-RPC stdio 或 Remote HTTP/WS）
  ├── Domain/          # Outcome<T>, DshFailure, DshCapabilities, Gateway Facet 接口
  ├── Engines/
  │     ├── TranscriptEngine   # 单会话串行、detachedTranscript Cache-First、50ms 合并、16行 StreamingBlocks
  │     ├── RequestsEngine     # 审批/问答状态机、Hold-to-undo、已结算账本、callId 参数关联
  │     ├── SessionEngine      # 会话目录、HistoryReconciler、UnknownOutcome / 幂等追踪
  │     └── Provider/Credential/Plugin Services
  └── Presentation/    # 纯 C# ViewModel / UiState / Actions / Reducer（无 Android 类型）

        ▲ 仅经 Gateway Facets + IDshRuntimeHost 交互（绝不持有 RuntimeCoordinator._op 锁）
        │
DshMobile.Application (现有 net11.0)
  └── RuntimeCoordinator (唯一控制面编排：启停、journal、导入、在线更新探针、回滚、WarmupPolicy、WorkLeases、IdleStop)

DshMobile.Runtime (现有 net11.0)
  ├── Hosting/         # IDshHost, DshHost / DshSdkHost (复用 DshHostPhase / GracefulStop)
  ├── Guest/           # ProotCommandBuilder (复用 ADR-0030 缓存 + --patch), ProotGuestProcess, ProotSdkProcess
  ├── Supervision/     # ProcessTable (升级 NativeOrphanReaper 语义), ProcessBudget, DshHealthPolicy
  └── Layers / Updates / Automation (Relay) / Remote (SSH.NET)

DshMobile.Native (现有 net11.0)  ·  DshMobile.Core (现有 net11.0，含 WorkspaceProvider V2、StartupTrace、LogRedactor)
```

### 3.1 分层精简原则

保留当前 `DshMobile.slnx` 的清晰结构，**一期仅新增 1 个托管类库 `DshMobile.Agent` 与 1 个测试项目 `tests/DshMobile.Agent.Tests`**（内部分 `Protocol/`、`Domain/`、`Engines/`、`Presentation/` 命名空间），不一次性拆出 4 个新程序集，待模块成熟后再按需拆分。

### 3.2 强类型错误、能力模型与 Facet 接口

借鉴 OpenCode Mobile Next 的 `Outcome` + `Gateways.kt` Facet 拆分 + `ServerCapabilities.kt`：

```csharp
public enum DshFailureCode
{
    Unavailable, Unsupported, Unauthorized, CredentialsMissing, InvalidState,
    SessionBusy, StaleRequest, ProcessExited, TransportLost, Timeout,
    ProtocolMismatch, CorruptHistory, StorageUnavailable, PhantomProcessLimit, MemoryLimited
}

public sealed record DshFailure(DshFailureCode Code, string? DiagnosticKey = null, bool Retryable = false);

public readonly record struct Outcome<T>(bool IsOk, T? Value, DshFailure? Error);

public sealed record DshCapabilities(
    bool Prompt,
    bool PromptImages,
    bool SlashCommands,
    bool StreamingEvents,
    bool Approvals,
    bool UserQuestions,
    bool ResumeKnownSession,
    bool ListSessions,
    bool HistoryReplay,
    bool CancelSession,
    bool SwitchModelInSession,
    bool ManageCredentials,
    bool ServerPromptIdempotency,
    bool PluginInventory,
    bool PluginMutation,
    bool GitOperations);
```

- **Gateway 拆分为小 Facet 接口**：`ISessionGateway`、`IPromptGateway`、`IApprovalGateway`、`IQuestionGateway`、`IProviderGateway`、`ICredentialGateway`、`IPluginGateway`、`IEventGateway`，由 `IDshGateway` 组合；不支持的 Facet 给出默认实现返回 `DshFailureCode.Unavailable`，UI 永远只读 `DshCapabilities`，不判断后端类型。
- **取消异常铁律**：所有 `async` 边界方法必须显式 `catch (OperationCanceledException) { throw; }`（或使用 `when (ex is not OperationCanceledException)`），严禁吞掉取消或把 `ex.Message` 暴露给 UI 文案。

---

## 4. 运行时与持久传输层（复用 P0–P2 优化）

### 4.1 传输层实现要点（按 PR-00 裁决落地其一）

#### 若采用路线 A：`ProotSdkProcess`（持久 stdio JSON-RPC 2.0）
1. **复用优化参数**：由 `ProotCommandBuilder` 构造完整 argv，**必须继承** `link-shim.mjs`、`startup-fs-cache-shim.mjs`、`PROOT_RO_CACHE_PREFIXES=/opt/dshapp/runtime:/usr/local`、`PROOT_RO_CACHE_NONEXIST=0`、以及现有 `--patch`（含 `ui_*` MCP 注入），严禁通过用户拼接 Shell 启动。
2. **三路并发防死锁**：
   - **读通道**：独立后台循环按行读取 stdout（单行硬上限、严格 UTF-8、STJ SourceGen 解析），永不阻塞在用户点击上；
   - **写通道**：`Channel<RpcOutbound>` 单写者有界队列串行写 stdin 并 `FlushAsync()`；
   - **业务通道**：服务端主动 `approval/request` 与 `user-question/ask` 异步投递给 `RequestsEngine`，在等待 `session/wait` 期间仍可正常应答审批。
3. **stdout 污染主动防护**：启动 Node 时将警告导向 stderr，并评估以受控 preload（同 `startup-fs-cache-shim.mjs` 机制）将第三方插件的 `console.log` 重定向至 `stderr`，仅保留专用协议句柄写 stdout。`RpcLineCodec` 对非 JSON 行区分「握手前杂讯」与「握手后污染」，脱敏计数并在超阈值时标记协议异常。
4. **安全停机**：先发 `shutdown` RPC（有界等待）→ 若未退则复用 ADR-0031 `GracefulStop`（SIGTERM 1.8s，让 Node flush compile cache 与释放 writer lock）→ 最后升级进程树清理。

#### 若采用路线 B：无浏览器对接裁剪 DSH Remote API（`/api` + `/api/remote.mux`）
1. **复用并裁剪 `web` 启动链**：保持现有 `DshHost` 进程管理、launch token 内存保管、ADR-0030/0031 全部快路径与优雅停机；通过受控 `--patch` 禁用 `frontend-static` 与浏览器 UI 名录（不加载前端静态资源与浏览器壳），进一步压缩启动耗时与 RSS。
2. **原生 C# Connection 适配器**：
   - 使用禁用代理、禁止重定向、仅允许 `127.0.0.1` 的 `SocketsHttpHandler` 完成 launch token → 签名 Cookie 交换；
   - 接入 `/api` 一元 RPC 与 `/api/remote.mux` WebSocket，严格遵守 `$events` 首帧 `{ type: 'ready' }` 栅栏后再拉取快照，避免 baseline 与增量事件竞态；
   - 复用官方 `session-controller` 的 `requestId` 幂等去重、冷读会话列表、历史分页、取消、模型目录与设置/凭据管理；
   - 以脱敏 Golden Fixtures 钉住消费到的 Remote 路由与事件 envelope，上游升级时由契约测试第一时间暴露变更。

### 4.2 控制面与数据面协作规范

- **双面解耦**：`RuntimeCoordinator._op` 仅串行 `Start / Stop / Import / Update / Rollback / Probe`。活跃会话的 prompt、流式读取、审批回复走独立数据面，绝不申请 `_op`。
- **有序停机钩子**：`RuntimeCoordinator.StopHostAsync` 在触发进程停止前，先通过 `HostStopping` 异步通知 `DshAgentSessionHost`：拒绝新发送 → `RequestsEngine.FlushOrCancelAllAsync()` → 落盘 `KeptTranscript` → 关闭传输通道。
- **在线更新探针抽象**：将 `RuntimeCoordinator` / `UpdateCoordinator` 中硬编码的 `StartHostAsync(DshProfile.Web)` + HTTP 401 检查抽象为 `IDshHost.ProbeHealthAsync(ct)`，确保 PR-09 移除 WebView 后，DSH 层与 Runtime 层在线更新的 `probing` 自动回滚机制 100% 正常工作。
- **状态机统一**：直接扩展 [DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs#L25-L36) 的 `DshHostPhase`，新增协议握手阶段（`ProtocolConnecting` / `Initializing`），`Ready` 永远只在协议握手校验通过后置位。

### 4.3 `ProcessTable` 升级（吸收 `NativeOrphanReaper`）

修复 [ProcessTable.cs](../../src/DshMobile.Runtime/Supervision/ProcessTable.cs#L51) 当前仅按 `IndexOf(") ")` 与 `cmdline` 匹配的隐患：
1. 解析 `/proc/<pid>/stat` 改用 **`LastIndexOf(')')`**，同步读取 `ppid`、`state`、`startTicks`（第 22 字段，防 PID 复用误杀）、`utime/stime` 与 `rssPages`；
2. 读取 `/proc/<pid>/status` 的 `Uid` 与 `TracerPid`，以及 `/proc/<pid>/exe` 符号链接目标（兼容 ` (deleted)` 后缀）；
3. **保守孤儿判定**：仅当 `uid == appUid && pid > 1 && pid != appPid && parent == 1 && tracer == 0 && state != 'Z' && exe 匹配 libproot-loader*.so && 不在当前活跃进程树中` 时，才认定为 PRoot 被系统 SIGKILL 遗留的孤儿 guest 进程并安全回收；任何 `/proc` 字段不可读一律跳过不误杀。

---

## 5. Cache-First、Transcript Engine 与增量流式渲染

### 5.1 Cache-First 首屏架构（隐藏 5.8s 冷启动）

借鉴 `opencode-mobile-next` 的 `detachedTranscript` + `KeptTranscript`：
1. 每次会话进入空闲、切后台或停机前，将每个活跃会话的**最近 N 条已脱敏展示消息**（如 50 条）与会话摘要元数据写入私有本地快照存储（原子写、不存密钥与审批令牌）。
2. App 冷启动打开会话页时，**不展示全屏 Loading / 骨架屏**，而是立刻以同一套 `TranscriptReducer` 启动 `detachedTranscript`，0.2s 内画出真实消息流并允许用户浏览历史或起草下一条消息（顶部以轻量状态条如实显示 `DshHostPhase` 进度，发送按钮标明待运行时就绪）。
3. DSH 就绪并建立实时订阅后，无缝切换至 live `TranscriptHandle`，触发 `HistoryReconciler` 增量合并，不闪烁、不跳动滚动位置。

### 5.2 Transcript Engine 与 16 行硬切片流式渲染

```text
实时事件流 -> ProtocolEventAdapter（解码、作用域校验、Unknown 容错）
           -> 每会话单线程 Channel（串行，幂等）
           -> TranscriptReducer（Message/Part upsert、有界 deferredParts、Turn 级前缀缓存、16 行 StreamingBlocks）
           -> CoalescingPublisher（50ms 前缘+后缘合并；subscriptionCount == 0 时 skippedWhileHidden）
           -> ChatViewModel（不可变 ChatSnapshot + 稳定 ItemId + Revision）
           -> Android RecyclerView（ListAdapter + DiffUtil 局部 Payload 刷新）
```

**关键工程规则：**
1. **作用域隔离**：`SessionKey = (RuntimeId, ProfileId, SessionId)`，消息/Part/ToolCall/Request 全部绑定 `SessionKey`，防止重装或切换远程主机时串台。
2. **16 行硬切片（`StreamingBlocks`）**：流式文本按行单遍切分为顶级 Markdown 块；已闭合块永久不可变复用；**任何未闭合代码块或长段落超过 16 行即强制在行尾切出永久不可变的 settled chunk，仅保留最后 ≤16 行作为可变 tail 块**，保证每 token 重排与拷贝均为 $O(1)$。
3. **Turn 级持久化前缀复用**：已稳定历史 Turn 缓存其派生的 `ChatRow` 列表，流式输出只重算最后一个 Turn 的尾部，确保 100 轮长对话不随轮数线性变慢。
4. **纯托管 Markdown 渲染选型**：在 C# 侧使用纯托管、支持 AOT/Trim 的 **Markdig**（锁定精确版本，PR-00 验证 `TrimMode=full` 零反射告警）将 settled/tail 块解析为轻量 IR，在 Android 侧渲染为 `SpannableStringBuilder` + 专用 `CodeBlockView` / `DiffLineView` / `TableBlockView`（多 `ViewType` 复用，主线程零解析）。
5. **确定性计数器单测门禁**：参照 `TranscriptCounters` 与 `Stream6000`，为每个 `TranscriptHandle` 内置计数器（`Publishes`、`Derives`、`TailReparses`、`TailChars`、`RowWrites`、`HistoryVisits`、`SkippedWhileHidden`），在 `DshMobile.Agent.Tests` 中用 6,000 词 + 400 行代码块流式压测锁死 $O(1)$ 增量边界。
6. **滚动与阅读锚点**：仅在“跟随底部”态自动贴底；用户向上滑动即退出跟随，显示回到底部浮钮，流式更新绝不抢占阅读位置。

### 5.3 History Reconciler 与发送去重

- **先订阅，再快照，后合并**：建立实时监听并记录水位 → 拉取历史页 → 丢弃版本低于实时事件的旧字段 → 若上游无可靠游标则做一次版本复核；无法读取完整历史时明确展示「局部缓存 / 待校准」，绝不伪造完整历史。
- **发送状态机**：`Draft -> Dispatching(ClientOpId) -> Enqueued(MessageId) -> Running -> Settled(turn/end.reason)`，断线无回执进入 `UnknownOutcome`：
  - 若采用路线 B（`session-controller` 支持 `requestId` 幂等去重），可在限定窗口内安全幂等重试；
  - 若采用路线 A（SDK 无服务端幂等键），**严禁自动重发**，先拉历史核对是否已入队，用户手动重发前明示可能重复执行工具。

---

## 6. 原生审批、用户问答与双安全域隔离

### 6.1 审批参数回溯与失败关闭

- 收到 `approval/request` 后，立即按 `callId` 从同会话已到达的 `session.event` 中检索对应的工具调用输入（如 shell 命令、文件路径、修改摘要），生成脱敏后的安全展示卡片；**若未匹配到参数，必须显式提示“未获取到完整工具参数，请谨慎确认”**。
- 仅 `allowed-once` 允许放行；超时、取消、切后台、进程退出一律映射为 `rejected` / `cancelled` 或等待失败关闭，绝不自动放行，一期不自建客户端永久白名单。

### 6.2 `RequestsEngine` 状态机（吸收 Hold-to-undo 与已结算账本）

```text
Waiting -> Held(短暂防误触撤销窗，可 Undo 回 Waiting) -> Sending(原子单次派发) -> Settled(记入已结算账本)
Waiting / Held -> Cancelled(服务端撤销或会话结束) / TransportLost
```

1. **防误触撤销窗**：用户在手机上点击「允许/拒绝」后提供短时可撤销缓冲（例如 1.5s，仅在卡片点击生效；切走页面、锁屏或关闭连接时立即 `flushHeld` 发出），避免小屏误点高危命令。
2. **跨入口单次派发**：通知栏快捷操作与聊天页内审批卡并发点击时，经原子认领共享同一条在途写入，保证每条 `RequestId` 最多回包一次。
3. **已结算账本（Settled Ledger）**：记录本连接代次已结算的 `RequestId`，断线重连后拉取待处理队列时自动过滤，彻底杜绝已答复卡片“诈尸”。
4. **用户问答**：完整支持 `AskUserQuestionItem`（单选、多选、自定义输入、明确取消 `{ cancelled: true }`）；收到 `user-question.cancelled` 立刻撤卡并丢弃迟到答案。

### 6.3 与 Relay `ui_*` 的双安全域隔离

DSH 工具审批（guest 内工具执行）与 Android Interlock Relay 审批（宿主机无障碍/录屏/设备写操作，ADR-0008）分属两个独立安全域：**绝不跨域继承授权**。涉及 `ui_*` 的调用仍完整经过 `RelayApprovalConfirmActivity`（`FLAG_SECURE`、过滤遮挡触摸、128 位一次性令牌、`selfWindowVisible` 写互锁）。

---

## 7. 原生 Android UI（Material 3）与关键补齐页面

### 7.1 导航演进与页面清单

基于现有 [MainActivity.cs](../../src/DshMobile.Android/MainActivity.cs) 的 Material 3 底栏 + 600dp `NavigationRailView` 平滑演进，建议主导航为 **`会话 | 工作区 | 更多`**（保持 3 栏简洁，**收件箱**以顶部待处理横幅 + 导航红点角标直达，避免小屏底部过挤；宽屏 600dp+ 会话页支持左列表右聊天双栏）：

| 页面 | 核心能力 | 关键实现与衔接 |
|---|---|---|
| **会话列表 (`ChatsFragment`)** | 新建、搜索、运行中/需审批角标、Cache-First 最近摘要 | `RecyclerView + ListAdapter + DiffUtil`；复用 `KeptTranscript` 索引 |
| **聊天详情 (`ChatFragment`)** | Cache-First 首屏、流式 Markdown/代码/Diff、工具卡、子 Agent 入口、审批/问答卡、图片选择、原生斜杠命令、输入栏 | 多 `ViewType` `RecyclerView` + Markdig 托管解析；ViewModel 驻留 `DshMobileApplication` ConnectionScope |
| **凭据与 Provider 引导（新增，P0 必补）** | 首次配置 DeepSeek API Key / 兼容 Provider / 账号登录、可用模型选择与连通校验 | 走 DSH 官方 credentials / settings 服务或受控 CLI 写入；严禁日志与 `OnSaveInstanceState` 留痕；不破坏 `.dsh/` 文件工作台保护 |
| **待办收件箱 (`InboxSheet/Fragment`)** | 跨会话聚合所有待审批与待问答卡片 | 直连 `RequestsEngine` 全局流，支持跳转原会话 |
| **工作区 (`WorkspaceFragment`)** | 文件浏览、文本编辑/冲突检测、压缩解压、回收站 + **新增 Git 状态/Diff** | **直接复用现有 `WorkspaceProvider` V2**（ADR-0026），增量叠加只读 Git 状态与 Diff 查看器 |
| **插件管理 (`PluginsFragment`)** | **L0** 目录/状态/报错 + **L1** 开关、受控安装卸载、需要重启 DSH 的明确提示 | 复用 DSH 官方 `plugin` / `plugin_manager` 语义，不自建 npm 解析器、**不做 L2 表单 Schema**（ADR-0033 记录与 ADR-0028 的衔接边界） |
| **更多 / 运行环境 / 远程主机 / 手机操控** | 复用现有全部页面 + 新增进程预算/温控状态展示 | 保留签名在线更新、回滚、诊断、Android 17 局域网权限入口、SSH 主机与 Relay 控制台 |

### 7.2 生命周期与状态保存铁律

- ViewModel 与 Engine 归属于 `DshMobileApplication` 的 ConnectionScope，Fragment 仅订阅状态流并派发命名 `Action`，彻底免疫 `UiMode`（深色模式）、横竖屏与折叠屏重建。
- **恢复态安全**：`OnSaveInstanceState` 仅保存非敏感导航坐标（当前 Tab、会话 ID、草稿文本引用），**严禁保存破坏性确认框状态（删除/停止/卸载/撤销）及任何 API Key / Token / 密码**（由 CI 静态扫描门禁强制检查）。

---

## 8. 后台生命周期、系统治理、网络与迁移

### 8.1 工作租约与空闲停机

- 引入 `WorkLeaseManager`（覆盖 `Chat / Approval / Setup / Update / Reconcile`），具备单租约上限与总连续上限，**单调时钟倒退或异常即清零关闭租约**，绝不因时钟跳变导致永久持有唤醒/前台服务。
- 引入 `IdleStopPolicy`（默认保守可配置）：仅当 App 在后台、无活跃租约、且确认所有 Agent 处于 `Idle` 且无待审批请求并超过空闲阈值时，才执行有序优雅停机。
- 继续复用 ADR-0031 的 `WarmupPolicy`（低电量 ≤15% 未充电、省电模式、用户主动停止 `_userStopped` 均不预热）。

### 8.2 Android 12+ 进程预算、温控阶梯与 Android 17 Memory Limiter

1. **32 Phantom Process 预算监控**：基于升级后的 `ProcessTable` 统计当前 App UID 下的子进程数（排除主进程），接近上限（如 ≥28）在运行环境页与状态条给出提示，并为 API 34+ 提供一键跳转开发者选项关闭「限制子进程」的引导（仅引导，不越权修改系统设置）；捕获 guest `exit 137` 时明确提示系统子进程数上限而非“未知崩溃”。
2. **Android 17 Memory Limiter 死因分类**：App 重启恢复时读取 `ActivityManager.GetHistoricalProcessExitReasons()`，若 `Reason == Other` 且描述命中 `MemoryLimiter:AnonSwap`，归类为 `DshFailureCode.MemoryLimited` 并以清晰文案告知用户后台内存配额限制（不存储/上报原始系统描述文本）。
3. **温控与电量阶梯（`HeatTierPolicy`）**：监听 `PowerManager.AddThermalStatusListener` 与限频 `GetThermalHeadroom(30)`（≥30s 间隔），在 `Moderate/Severe` 或低电量未充电时暂停后台预热与非必要并发任务，但绝不粗暴强杀正在执行的活跃会话。

### 8.3 Android 17 本地网络权限与远程主机衔接

- 严格保留 [ADR-0032](../ADR/0032-android-17-local-network-permission.md) 的 `ACCESS_LOCAL_NETWORK` 判定与 UI 入口；`127.0.0.1` loopback、局域网（私有网段/`.local`）与公网独立测试，权限拒绝如实报 `LAN_PERMISSION_DENIED`，绝不混淆为 DSH 协议错误。
- **远程主机（[RemoteSshSession.cs](../../src/DshMobile.Runtime/Remote/RemoteSshSession.cs)）原生适配**：
  - 若采用路线 B：远端继续启动固定白名单命令 `dsh --profile web --port <port>`，经现有本地回环端口转发直接接入同一 `DshRemoteGateway`，远程主机**直接获得完整原生聊天 UI**；
  - 若采用路线 A：在 `RemoteDshRules` 白名单中受控扩展远端 `dsh --profile sdk` 的流式 SSH 通道适配器，保持不可导出 Keystore 私钥与主机指纹固定不变。

### 8.4 覆盖安装与旧数据保护

1. 升级启动前对既有 `user-data` / `.dsh` 做只读盘点（版本、会话数、大小，不含凭据内容），**绝不删除或静默改名用户旧目录**。
2. 沿用同一 `DSH_HOME = /root/projects/.dsh` 持久化格式；对未能通过官方接口验证可恢复的旧会话，如实标示“不支持直接恢复 / 需迁移”，保留原始磁盘文件可随时回退旧版 APK。
3. APK、Runtime 基础层、DSH 层版本独立记录；运行中禁止不受控换层，换层前必须确认停止并保留 `probing` 回滚槽。

---

## 9. 自动化架构与安全门禁（C# Ratchet Tests）

复刻 `opencode-mobile-next` 的 `native/quality/` 机制，在 `tests/DshMobile.Agent.Tests`（及现有测试套件）中建立源码与架构扫描测试，采用 **Ratchet（只降不升基线）** 策略接入 CI：

| 门禁测试 | 检查内容 |
|---|---|
| `ArchitectureBoundaryTests` | `DshMobile.Agent` 零 `Android.*` / `Java.*` 引用；`DshMobile.Android` UI 层禁止引用 `ProotSdkProcess`、原始 JSON-RPC/WebSocket 实现或 `Process.Start` |
| `TrimAndSerializationGateTests` | 协议 DTO 全部登记在 `JsonSerializerContext` 中，禁止协议方法签名出现 `object` 参数；CI 校验 `dotnet build src/DshMobile.Android -c Release` 无新增 trim/AOT 警告 |
| `CancellationAndFailureGateTests` | 扫描 `async` 方法禁止裸 `catch (Exception)` 吞掉 `OperationCanceledException`；禁止 ViewModel/UI 绑定原始 `ex.Message` |
| `SavedStateSecurityTests` | 扫描 `OnSaveInstanceState` / `Bundle.Put*`，禁止保存含 `confirm/delete/stop/revoke/password/token/apikey/secret/credential` 语义的键 |
| `LoggingPrivacyTests` | 禁止生产代码绕过 `LogRedactor` / 脱敏日志通道直接写敏感字段或未脱敏协议帧 |
| `NoBusinessWebViewGateTests`（PR-09 启用） | 扫描 `DshMobile.Android` 源码、布局 XML、Manifest 与依赖图，确认业务路径零 `WebView` 引用 |
| `TranscriptPerfGateTests` | `Stream6000`（6,000 词 + 400 行未闭合代码块 + 100 轮历史）压测断言 `Publishes`、`TailChars`、`RowWrites`、`HistoryVisits` 严格受限 |

---

## 10. 可执行的垂直切片 PR 路线图

每个 PR 必须包含 **源码 + 单元测试/契约测试 + 文档更新 + 真机证据（按规则 9 如实标注未测项）**，严禁跨切片混改。

### PR-00：基线锁定、路线裁决 PoC 与架构门禁骨架
- **任务**：
  1. 锁定基线（`cd0fbd3`、DSH/Node 版本、现有 `startup-perf.mjs` 5.8s 基线与功能矩阵）；
  2. 执行「路线 A（`sdk` stdio + Bridge 所有权）vs 路线 B（裁剪 `web`/Remote API）」2 天 PoC，输出实测数据并正式落盘 **`docs/ADR/0033-native-agent-client.md`**；
  3. 验证 **Markdig** 在 `.NET 11 TrimMode=full` Release 构建下的兼容性与体积影响并锁定精确版本；
  4. 建立 `DshMobile.Agent` 项目骨架与首批架构/安全门禁测试。
- **门禁**：CI 全绿；Release APK 警告数 ≤ 75；ADR-0033 获确认；现有 WebView 与全部功能零退化。

### PR-01：持久传输层与宿主适配（并行入口，不改默认 WebUI）
- **任务**：
  - 按 ADR-0033 裁决落地 `ProotSdkProcess` + `DshSdkHost`（路线 A，含同进程 Bridge 会话所有权实测通过）或裁剪版 `DshApiHost` + `DshRemoteConnection`（路线 B）；
  - 完整复用 `ProotCommandBuilder` 的 ADR-0030 缓存、`startup-fs-cache-shim.mjs`、`--patch`（含 `ui_*`）、ADR-0031 的 `DshHostPhase` 与 `GracefulStop`；修复 `inPlaceRestart` 遗漏 `guestCache` 挂载；
  - 全部协议 DTO 使用 STJ SourceGen；透传 `timeZone`；接入 `StartupTrace` 新阶段。
- **门禁**：Fake Server 双向并发/断流/超长行/污染/超时单测全绿；Android 17 arm64 真机完成真实握手、发包、退出与重启且无孤儿进程。

### PR-02：Gateway Facets、`Outcome<T>`、`DshCapabilities` 与 Golden 契约测试
- **任务**：落地小 Facet 接口、`DshCapabilities`、脱敏真实录制 Golden Fixtures（覆盖文本、图片 `SdkEncodedImageBlock`、斜杠命令 `session/query`、审批、用户问答、取消、`turn/end` 错误 `reason`、协议不兼容降级）。
- **门禁**：纯 `net11.0` 契约测试覆盖全部成功/失败路径；真机端到端 CLI/测试能完成真实对话与审批不死锁。

### PR-03：`TranscriptEngine`（含 Cache-First）+ `RequestsEngine`
- **任务**：
  - 落地 `TranscriptReducer`、`StreamingBlocks`（16 行硬切片）、50ms 前缘/后缘合并发布器、`skippedWhileHidden`、`detachedTranscript` + `KeptTranscript` 本地脱敏快照、`HistoryReconciler`；
  - 落地 `RequestsEngine`（`callId` → `session.event` 参数关联、Hold-to-undo 缓冲窗、单次原子派发、已结算账本、`user-question.cancelled`）。
- **门禁**：`Stream6000` 性能计数器门禁、乱序/重连/审批防诈尸/多子 Agent 隔离单测全绿。

### PR-04：纯原生聊天垂直切片（并行入口，可回滚）
- **任务**：
  - 落地 `ChatFragment`、Material 3 XML、多 `ViewType` `RecyclerView`、`MarkdownTextView`（Markdig + `SpannableStringBuilder`）、`CodeBlockView`、`ToolCallHolder`、`ApprovalHolder`、`QuestionHolder`、图片选图发送、原生斜杠命令入口；
  - ViewModel 挂载到 `DshMobileApplication` ConnectionScope。
- **门禁**：不打开 `DshWebActivity` 即可在真机完成一轮真实带工具审批/图片/流式输出的完整对话；验证横竖屏、深色模式切换、中文 IME、返回键、向上回看不被抢滚。

### PR-05：原生 Provider / 凭据配置 + Cache-First 首屏 + 完整会话管理
- **任务**：
  - 落地**原生 Provider / API Key 凭据配置与首次引导页**（通过官方 credentials 服务/CLI，无明文落日志/恢复态）；
  - 接通 `ChatsFragment` 会话列表、历史分页、会话取消、模型目录与切换、以及 App 启动 0.2s `detachedTranscript` Cache-First 首屏交接。
- **门禁**：全新安装（零凭据）不打开 WebView 完成配置并对话；强杀重启 App 0.2s 显示上次对话并无缝校准，历史不重影、不重复发送。

### PR-06：工作区 Git 集成与跨会话收件箱
- **任务**：
  - 在现有 `WorkspaceProvider` V2（`FilesFragment`）上增量增加 Git 状态、变更列表与原生 `DiffLineView`；
  - 落地全局待审批/问答收件箱与系统通知联动（后台到达审批时发通知，拒绝后零工具执行）。
- **门禁**：工作区 `.dsh` 双向保护与路径越界负面测试全绿；后台通知审批与页内审批并发点击仅生效一次。

### PR-07：原生插件目录与受控管理（L0 / L1）
- **任务**：在明确遵循 ADR-0033 对 ADR-0028 边界的前提下，提供 L0 插件状态清单与 L1 受控开关/装卸（复用 DSH 官方规则 + 变更后明确提示重启 DSH）；无 L2 自定义界面插件如实提示“后端可运行，无原生配置页”。
- **门禁**：插件报错/装卸失败不拖垮宿主 UI，变更重启提示准确，无任意脚本执行。

### PR-08：系统治理、可靠性、在线更新探针与远程主机适配
- **任务**：
  - 升级 `ProcessTable` 为 `NativeOrphanReaper` 多条件孤儿回收；
  - 接入 `WorkLeaseManager`、`IdleStopPolicy`、32 Phantom Process 监控与 `exit 137` 归因、Android 17 `ApplicationExitInfo` `MemoryLimiter` 归因、`HeatTierPolicy`；
  - 抽象并切换在线更新 `IDshHost.ProbeHealthAsync` 探针，验证 DSH/Runtime 更新与回滚；
  - 适配远程主机 `RemoteSshSession` 接入原生聊天 UI；
  - 使用 `tooling/startup-perf.mjs` 完成与旧 WebView 基线的 A/B/C 各 10 次真机配对对比。
- **门禁**：Android 17 真机后台/锁屏/强杀/断网/更新回滚/远程主机全回归通过，无孤儿进程与静默丢审批。

### PR-09：默认入口切换与彻底清理 WebView
- **任务**：所有 P0 场景真机通过后，默认入口切换至原生会话；确认无残余引用后删除 `DshWebActivity`、`CSharpWebUiController`、Web 布局及废弃 `WebUiPolicy` 代码与测试；启用 `NoBusinessWebViewGateTests`；同步更新 `ARCHITECTURE.md`、`FEATURES.md`、`SECURITY.md`、`README.md`。
- **门禁**：源码/Manifest/依赖图/构建产物均零业务 WebView；覆盖安装旧版 APK 完整保留工作区、凭据与可恢复会话。

---

## 11. 测试矩阵、性能观测与风险控制

### 11.1 测试分层

| 层级 | 工具与手段 | 核心验证范围 |
|---|---|---|
| Pure `.NET` Unit | xUnit + `FakeTimeProvider` | `TranscriptReducer`、`StreamingBlocks`、`RequestsEngine`、`WorkLeases`、`IdleStopPolicy`、`ProcessTable` 孤儿判定、`DshCapabilities` |
| Architecture & Security Gates | C# 源码/程序集扫描 xUnit | 分层边界、Full Trim 序列化、禁止吞取消、恢复态安全、日志隐私、无业务 WebView、`Stream6000` 计数器 |
| Wire Contract | Fake DSH Server + 脱敏真实录制 Fixtures | 帧解析、并发 RPC、服务端主动请求、图片/斜杠命令、`turn/end` `reason`、协议不兼容 |
| Component / Chroot | PRoot / arm64 Linux + 真实 DSH | 进程启停、优雅停机、stdout 纯净度、`--patch` 注入、在线更新 `probing` 回滚 |
| Device E2E | Android 17 arm64 真机（及至少一台旧 API 设备） | 冷/热启动、Cache-First 交接、IME/深色/旋转、后台审批、覆盖安装、Android 17 LAN/loopback、SSH 远程、Relay `ui_*` |

### 11.2 P0 必过验收场景

- **T01 零配置冷启闭环**：全新安装 → 运行层就绪 → 原生凭据配置 → 首次真实 Agent 对话成功。
- **T02 Cache-First 秒开恢复**：已安装重启 → 0.2s 呈现上次缓存会话 → 后台 DSH 就绪并无闪烁校准 → 不重复发送上轮消息。
- **T03 入队与完成分离**：`session/prompt` 入队回执与 `turn/end` 终态严格分离；失败轮次准确展示失败原因而非假成功。
- **T04 后台审批与防误触**：切后台收到工具审批/问答 → 通知可见、参数按 `callId` 正确展示、Hold-to-undo 可撤回、拒绝后零工具执行、重连不诈尸。
- **T05 多会话与子 Agent 隔离**：并发多会话与 Subagent 事件不串台，隐藏会话跳过 UI 派生。
- **T06 异常崩溃与去重恢复**：运行中杀掉 DSH 或断连接 → 恢复校准不生成重复消息，不自动重发未确认危险指令。
- **T07 长消息流畅度**：6,000 词 + 400 行代码块连续流式输出不卡主线程、无明显掉帧，向上翻阅时不被强行拉回底部。
- **T08 双安全域与插件边界**：`ui_*` 仍过 Relay 独立审批与 `FLAG_SECURE`；插件安装失败可恢复且不崩溃宿主。
- **T09 Android 17 网络与远程主机**：`127.0.0.1`、局域网（`ACCESS_LOCAL_NETWORK`）、公网与 SSH 远程原生对话分别真机验证。
- **T10 覆盖安装数据无损**：从旧 WebView 版本覆盖安装，工作区、认证与可恢复会话完整保留，不可恢复旧会话如实提示不删盘。
- **T11 系统资源与孤儿清理**：停止或强杀 PRoot 后无残留孤儿进程；逼近 32 进程上限或触发 Android 17 内存限制时清晰归因。
- **T12 在线更新与零 WebView**：DSH/Runtime 在线更新探针与失败自动回滚通过；全仓与 APK 零业务 WebView。

### 11.3 统一性能观测（对接现有 `StartupTrace` + `tooling/startup-perf.mjs`）

不另起独立日志格式，直接沿用 [STARTUP_PERF.md](../STARTUP_PERF.md) 的 `startup-perf` JSON schema v1 与隐私禁记清单，在现有阶段基础上增补原生测点：

```text
# 复用现有阶段
coordinator.* / guest_assets.* / host.* / sandbox.* / dsh.spawn / bridge.complete

# 新增原生测点（同一 runId，脱敏输出）
native.activity_visible         # 原生首页可见
native.cache_first_rendered     # detachedTranscript 首屏绘制完成（Cache-First 核心体感指标）
protocol.connected              # stdio / mux 通道建立
protocol.initialized            # 握手校验通过（等价原 host.ready）
session.reconcile_complete      # 历史校准完成
chat.first_interactive          # 原生输入框可发送
prompt.dispatched -> prompt.ack -> agent.first_event -> assistant.first_text -> agent.turn_settled
```

所有性能结论须按 A/B/C 场景各 ≥10 次真机采集，使用 `tooling/startup-perf.mjs` 报告 P50/P95、配对差值与样本量；不以单次数据或模拟器结果宣称收益。

### 11.4 风险登记与停机条件

| 风险 | 严重度 | 缓解策略与停机条件 |
|---|---|---|
| 路线 A 中 Bridge 与 `HarnessSdkJsonRpcServer` 私有会话表冲突 | **高** | PR-00 PoC 一票否决；若无法证明安全共享所有权，直接切换路线 B 或先推上游 API |
| 路线 B 中 DSH 上游升级改动内部 Remote 协议 | **中** | 用脱敏 Golden Fixtures 与 CI 契约测试钉死消费到的每个路由/事件；遵循规则 8 升级前先跑兼容对照 |
| Full Trim 下 JSON 反射或 Markdig 裁剪崩溃 | **高** | 强制 STJ SourceGen，禁止 `object` 参数；每个 PR 必跑 `Release` 构建与真机冒烟 |
| PRoot 进程被系统 SIGKILL 遗留满载 CPU 孤儿 | **高** | `ProcessTable` 升级 `NativeOrphanReaper` 多条件判定 + 32 进程预算监控 + `WorkLeases` |
| 删除 WebView 后遗漏凭据配置 / 更新探针 / 远程主机 | **高** | 设为 PR-09 硬前置门禁（PR-05 与 PR-08 未验收禁止合入 PR-09） |
| 插件 stdout 污染导致 SDK 协议中断（路线 A） | **中** | 受控 preload 隔离 `console.log` 至 stderr + 握手前后分级告警；无法隔离则拒绝 Ready 并指明来源 |

---

## 12. 最终验收定义（Definition of Done）

满足以下全部条目并附真机证据后，方可宣告「DSH Mobile 原生 Android 重构完成」：

- [ ] ADR-0033 已通过 PoC 裁决并落盘，CI 全部架构/安全/性能门禁测试持续全绿，Release 构建无新增 Trim 警告。
- [ ] 不依赖 `DshWebActivity` 或任何业务 WebView，即可完成首次凭据配置、新建/恢复会话、流式对话、图片附件、斜杠命令、工具调用、审批与用户问答。
- [ ] Cache-First 首屏生效（冷启动 0.2s 内可见最近缓存对话），后台 DSH 复用 P0–P2 快路径就绪后无闪烁校准。
- [ ] 审批支持参数回溯、防误触撤销窗与已结算防诈尸，且与 Relay `ui_*` 审批域严格隔离；断线/超时绝不自动放行。
- [ ] 覆盖安装旧版 APK 保留用户工作区、凭据与可恢复会话；在线更新探针与失败自动回滚真机通过；远程主机原生对话通过。
- [ ] Android 17 真机通过 `ACCESS_LOCAL_NETWORK` 分层测试、切后台/锁屏/强杀恢复、`NativeOrphanReaper` 无孤儿检查及 A/B/C 性能配对报告。
- [ ] 全仓源码、布局、Manifest 与构建产物无业务 WebView 依赖（网页预览仅走受控系统浏览器）。

---

## 13. 参考索引

- **本仓库**：[AGENTS.md](../../AGENTS.md)、[ARCHITECTURE.md](../ARCHITECTURE.md)、[FEATURES.md](../FEATURES.md)、[SECURITY.md](../SECURITY.md)、[STARTUP_PERF.md](../STARTUP_PERF.md)、[VNEXT_STATUS.md](../VNEXT_STATUS.md)、[ADR-0028](../ADR/0028-remove-native-terminal-and-plugin-pages.md)、[ADR-0030](../ADR/0030-proot-startup-fastpath.md)、[ADR-0031](../ADR/0031-p2-perceived-startup.md)、[ADR-0032](../ADR/0032-android-17-local-network-permission.md)。
- **OpenCode Mobile Next**：[`native/AGENTS.md`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/AGENTS.md)、[`Gateways.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/core/domain/src/commonMain/kotlin/io/github/eslamasabry/oc/core/domain/Gateways.kt)、[`ServerCapabilities.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/core/domain/src/commonMain/kotlin/io/github/eslamasabry/oc/core/domain/capabilities/ServerCapabilities.kt)、[`TranscriptEngine.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/engine/transcript/src/commonMain/kotlin/io/github/eslamasabry/oc/engine/transcript/TranscriptEngine.kt)、[`StreamingBlocks.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/engine/transcript/src/commonMain/kotlin/io/github/eslamasabry/oc/engine/transcript/internal/StreamingBlocks.kt)、[`RequestsEngine.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/engine/requests/src/commonMain/kotlin/io/github/eslamasabry/oc/engine/requests/RequestsEngine.kt)、[`WorkLeases.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/android/linux/src/main/kotlin/io/github/eslamasabry/opencode_mobile/WorkLeases.kt)、[`NativeOrphanReaper.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/android/linux/src/main/kotlin/io/github/eslamasabry/opencode_mobile/NativeOrphanReaper.kt)、[`KonsistRulesTest.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/quality/src/test/kotlin/io/github/eslamasabry/oc/quality/KonsistRulesTest.kt)、[`CacheFirstGate.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/quality/src/test/kotlin/io/github/eslamasabry/oc/quality/CacheFirstGate.kt)、[`RestoreGuardsTest.kt`](https://github.com/Eslamasabry/opencode-mobile-next/blob/master/native/quality/src/test/kotlin/io/github/eslamasabry/oc/quality/RestoreGuardsTest.kt)。
- **DeepSeek Harness**：[`packages/sdk/protocol/src/types.ts`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/sdk/protocol/src/types.ts)、[`packages/sdk/server/src/server.ts`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/sdk/server/src/server.ts)、[`packages/bundle/sdk-app/cordis.patch.yml`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/bundle/sdk-app/cordis.patch.yml)、[`packages/client/connection/README.zh.md`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/client/connection/README.zh.md)、[`packages/api/session-controller/README.zh.md`](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/api/session-controller/README.zh.md)。
