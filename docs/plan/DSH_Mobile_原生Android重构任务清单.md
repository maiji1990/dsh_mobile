# DSH Mobile 原生 Android 重构详细任务清单

> 主方案：[DSH_Mobile_原生Android重构实施方案.md](DSH_Mobile_原生Android重构实施方案.md)  
> 编制基线：2026-11-22（`main` @ `cd0fbd3`，.NET SDK `11.0.100`，`TrimMode=full`，`minSdk=29`）  
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12；**无真机证据的任务不得勾选「真机通过」**，0 用例不算测试通过。

---

## 0. 使用说明与依赖拓扑

### 0.1 任务编号与状态约定

- **编号格式**：`N-<PR两位>-<序号两位>`（如 `N-00-01` 表示 PR-00 第 1 项原子任务；`N-01A-*` / `N-01B-*` 分别对应路线 A / 路线 B 的条件分支任务）。
- **勾选原则**：仅当「代码/文档合入 + 自动化测试通过 + 门禁无回退」后方可打勾 `[x]`；需真机验收的条目须在文末「真机验收登记表」补齐日志/报告链接。
- **每 PR 必跑基线命令**：
  ```bash
  dotnet test tests/DshMobile.Core.Tests
  dotnet test tests/DshMobile.Runtime.Tests
  dotnet test tests/DshMobile.Application.Tests
  dotnet test tests/DshMobile.Agent.Tests        # PR-00 建立后必跑
  node tooling/run-node-tests.mjs tests/guest
  dotnet build src/DshMobile.Android -c Release  # 核对 TrimMode=full 警告不超基线（75）
  ```

### 0.2 PR 依赖与关键路径

```text
PR-00 (基线 + 路线A/B PoC + ADR-0033 + DshMobile.Agent骨架 + 门禁)
  └──▶ PR-01 (持久传输层 + 宿主适配 + P0-P2优化复用)
         └──▶ PR-02 (Gateway Facets + Outcome + Capabilities + Golden契约)
                └──▶ PR-03 (TranscriptEngine/Cache-First + RequestsEngine)
                       ├──▶ PR-04 (纯原生聊天垂直切片，保留WebView回滚)
                       │      └──▶ PR-05 (原生凭据/Provider引导 + Cache-First首屏 + 完整会话管理)
                       │             ├──▶ PR-06 (工作区Git/Diff + 跨会话收件箱/通知)
                       │             └──▶ PR-07 (原生插件目录 L0/L1，无L2 Schema)
                       └───────────────────────▶ PR-08 (孤儿清理/32进程/A17内存/温控/更新探针/远程SSH/性能配对)
                                                   └──▶ PR-09 (切换默认入口 + 彻底清理WebView + 全回归)
```

> **并行机会**：PR-06 与 PR-07 在 PR-05 完成后可并行推进；PR-08 中的 `ProcessTable` 升级（`N-08-01`）与 `WorkLeases` / `IdleStopPolicy`（`N-08-02`）可在 PR-03 完成后提前独立开发。

---

## PR-00：基线锁定、路线裁决 PoC 与架构门禁骨架

- **目标**：用实测数据拍板协议路线（A vs B），锁定 Markdig 选型，搭好 `DshMobile.Agent` 与 CI 架构/安全棘轮门禁。
- **前置依赖**：无（基于 `main` @ `cd0fbd3`）。
- **退出门禁**：ADR-0033 获确认；CI 全绿；Release APK 警告数 ≤ 75；现有 WebView、Relay、SSH、更新、文件功能零退化。

### 任务项

- [x] **N-00-01｜基线盘点与功能对照表**
  - 记录当前 `main` commit SHA、已装 DSH / Node / Debian 版本、现有 `tooling/startup-perf.mjs` A/B/C 真机基线（约 5.8s）、Release 构建告警数（实测清洁基线 116 / 增量 76；PR-01 `#44` 历史为 75）。
  - 产出：`docs/native/BASELINE.md`（含现有 WebUI 全部功能与原生替代状态矩阵，不删代码）。
- [x] **N-00-02｜路线 A PoC（`sdk` stdio + 同进程 Cordis Bridge 所有权验证）**
  - 验证点 1：在 `dsh --profile sdk` 中挂载最小测试 Cordis 扩展，验证其通过 `ctx.agents` / `ctx.sessionPersistence` 列出/读取/取消/切换 `HarnessSdkJsonRpcServer` 创建的会话时，是否会破坏 `this.sessions` 或触发 `assertLiveAgent` 异常。
  - 验证点 2：验证全新零凭据环境启动 `dsh --profile sdk` 时 `initialize` 的真实失败行为与凭据写入可行路径。
  - 产出：`docs/native/POC_ROUTE_A_SDK.md`（附真实运行命令与输出）。
- [x] **N-00-03｜路线 B PoC（无浏览器原生对接裁剪 `web`/Remote API）**
  - 验证点 1：通过受控 `--patch` 禁用 `frontend-static` 与浏览器 UI 插件行，在桌面环境实测启动耗时、内存占用与 `/api` + `/api/remote.mux`（`$events` `ready` baseline）握手（arm64 真机待另行采集）。
  - 验证点 2：验证 `session-controller`（冷读会话列表、历史分页、`requestId` 幂等发消息、取消、切换模型）、`settings-controller`（凭据写入）、`plugin-inventory` 是否可由原生 HTTP + WebSocket 客户端稳定消费。
  - 产出：`docs/native/POC_ROUTE_B_REMOTE.md`（附真实运行命令、耗时对照与 wire 样本）。
- [x] **N-00-04｜裁决并落盘 ADR-0033**
  - 综合 `N-00-02` 与 `N-00-03` 实测证据，正式确定采用**路线 B**，并明确记录：移除 WebView 决策、最低版本 Android 10+（API 29）、对 [ADR-0028](../ADR/0028-remove-native-terminal-and-plugin-pages.md) 插件页边界的修订（仅 L0/L1、不做 L2 Schema）。
  - 产出：`docs/ADR/0033-native-agent-client.md`。
- [x] **N-00-05｜纯托管 Markdown 渲染库选型与 Trim 验证**
  - 引入锁定精确版本的 **Markdig**（`1.6.0`），在 `DshMobile.Android` 上执行 `-c Release`（`TrimMode=full`）验证：零新增 Trim 警告、APK 体积增量可接受（+752.0 KiB）、基础 Markdown/GFM 表格/围栏代码解析正常。
  - 产出：`docs/UPSTREAMS.md` 登记 + 验证记录写入 `docs/native/BASELINE.md`。
- [x] **N-00-06｜创建 `DshMobile.Agent` 与 `DshMobile.Agent.Tests` 骨架**
  - 在 [DshMobile.slnx](../../DshMobile.slnx) 中新增纯 `net11.0` 项目 `src/DshMobile.Agent`（零 Android 引用）与 `tests/DshMobile.Agent.Tests`，接入 `.github/workflows/android-dotnet.yml`。
  - 产出：项目文件、CI 配置更新、`dotnet test` 含 Markdig 与 STJ SourceGen 冒烟断言。
- [x] **N-00-07｜建立首批 CI 自动化架构与安全棘轮门禁**
  - 在 `tests/DshMobile.Agent.Tests/Quality/` 落地：
    - `ArchitectureBoundaryTests`（`DshMobile.Agent` 禁引 `Android.*`/`Java.*`；UI 层禁引底层进程/原始协议）；
    - `TrimAndSerializationGateTests`（协议 DTO 均在 `JsonSerializerContext` 注册，禁 `object` 参数）；
    - `CancellationAndFailureGateTests`（禁 `async` 中裸 `catch (Exception)` 吞 `OperationCanceledException`；禁 UI 绑定 `ex.Message`）；
    - `SavedStateSecurityTests`（禁 `OnSaveInstanceState`/`Bundle` 存高危确认或凭据键）；
    - `LoggingPrivacyTests`（禁未脱敏协议帧或敏感字面量写日志）。
  - 产出：5 套源码扫描测试全绿并纳入 CI。

---

## PR-01：持久传输层与宿主适配（并行入口，不改默认 WebUI）

- **目标**：落地稳定、并发安全、低开销的持久传输通道与宿主封装，复用全部 P0–P2 启动优化，保留原 `DshWebActivity` 不变。
- **前置依赖**：PR-00（ADR-0033 已选定路线 A 或路线 B）。
- **退出门禁**：Fake Server 压力与故障单测全绿；Android 17 arm64 真机完成真实启动→握手→收发→优雅停机→重启且无孤儿进程。

### 公共任务（路线 A / B 均需完成）

- [x] **N-01-01｜复用 P0–P2 优化并修复 `ProotCommandBuilder` 遗留**
  - 确保新原生宿主完整复用 [ProotCommandBuilder.cs](../../src/DshMobile.Runtime/Guest/ProotCommandBuilder.cs) 的 `link-shim.mjs`、`startup-fs-cache-shim.mjs`、`PROOT_RO_CACHE_PREFIXES`、`PROOT_RO_CACHE_NONEXIST=0` 及 `--patch`（含 `ui_*`）；
  - 修复 `inPlaceRestart` 遗漏 `guestCache` 挂载的历史差异，并补齐向量/单测。
- [x] **N-01-02｜扩展 `DshHostPhase` 与 `StartupTrace` 新阶段**
  - 在 [DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs) / `IDshHost` 扩展阶段里程碑（`ProtocolConnecting` / `Initializing`），保持 `Ready` 仅在握手成功后置位；
  - 在 `StartupTrace` 与 `tooling/startup-perf.mjs`（schema v1 兼容）新增 `protocol.connected` / `protocol.initialized` 测点，并同步隐私回归测试。
- [x] **N-01-03｜控制面锁与数据面并发隔离设计**
  - 在 [RuntimeCoordinator.cs](../../src/DshMobile.Application/Runtime/RuntimeCoordinator.cs) 确认数据面通道不持有 `_op` 锁；为 `HostStopping` 增加数据面排空钩子（`AddHostStoppingHook` 多订阅排空：拒绝新请求 → 清理挂起等待 → 关闭连接）。
- [x] **N-01-04｜全量 STJ Source Generation 协议编解码上下文**
  - 在 `DshMobile.Agent/Protocol/` 建立 `[JsonSerializable]` 源生成上下文（[AgentProtocolJsonContext.cs](../../src/DshMobile.Agent/Protocol/AgentProtocolJsonContext.cs)），严格校验零反射、支持字符串与数字 `RpcId`（[RpcId.cs](../../src/DshMobile.Agent/Protocol/RpcId.cs)）、错误对象 `code/message/details/data`。

### 分支 A 任务（仅当 ADR-0033 选择路线 A 时执行）

- [ ] **N-01A-01｜新增持久双向 `ProotSdkProcess`（不动 `ProotGuestProcess`）**
  - `UseShellExecute=false`、保持 stdin 打开、独立 stdout 字节/行读取任务（单行硬上限、严格 UTF-8）、独立 stderr 清洗脱敏任务、`Channel<T>` 单写者有界写入队列。
- [ ] **N-01A-02｜stdout 污染主动防护**
  - 启动参数将 Node 警告导向 stderr；评估并接入受控 preload 将非协议 `console.log` 重定向至 stderr；`RpcLineCodec` 对握手前杂讯与握手后污染分级计数、脱敏告警并设熔断阈值。
- [ ] **N-01A-03｜`DshSdkHost` 与三路并发防死锁**
  - 实现 `initialize` 校验（核对 `serverInfo.name == "deepseek-harness-sdk-runtime"`）、并发 `id` 关联、服务端主动 `approval/request` 与 `user-question/ask` 异步派发（不阻塞读循环）、`shutdown` → `GracefulStop`（SIGTERM 1.8s）→ 进程树清理。
- [ ] **N-01A-04｜路线 A 契约测试与真机握手验收**
  - Fake stdio Server 测试覆盖：交错请求/通知、超长行、非法 JSON、半包 UTF-8、并发写入、超时取消、进程强退、stderr/stdout 不串。
  - 真机验证 `dsh --profile sdk` + `--patch`（含 `ui_*`）启动、握手、退出与重启。

### 分支 B 任务（仅当 ADR-0033 选择路线 B 时执行）

- [x] **N-01B-01｜裁剪型 `web`/Remote `--patch` 资产与主机启动**
  - 生成固定受控 `--patch`（[dsh-web-trim-patch.yml](../../src/DshMobile.Runtime/Guest/Assets/dsh-web-trim-patch.yml)，禁用 `frontend-static` / `modules` 与浏览器 `ui-*` 插件行，保留 `connection` / `session-controller` / `settings-controller` / `plugin-inventory` / `ui_*`），并钉住 SHA-256（`ca9797888b7f6402a21a15dd9bd3308faeb936d50f171a31262d50824d605fd2`）。
- [x] **N-01B-02｜原生 C# `DshRemoteConnection`（HTTP + `/api/remote.mux` WebSocket）**
  - 仅允许 `127.0.0.1`/loopback、禁用代理与自动重定向；实现内存 launch token → 签名 Cookie 交换（token 绝不落日志）；
  - 实现 `/api` 一元 RPC（含 `multipart/form-data` 附件解析）与 `/api/remote.mux` `$events` 逻辑流，严格等待 `{ type: 'ready' }` 首帧后才发布 connection generation；实现指数退避重连与有序排空（`BeginDrainAsync`）。
- [x] **N-01B-03｜路线 B 契约测试与真机连接验收**（桌面与 Fake Server 已绿；Android 17 arm64 真机 **待真机验证**）
  - Fake HTTP/WS Server 测试（[DshRemoteConnectionTests.cs](../../tests/DshMobile.Agent.Tests/Transport/DshRemoteConnectionTests.cs)）覆盖：401 刷新、首帧非 `ready` 拒绝、断线重连（含指数退避与 generation 递增）、取消旧 source 不覆盖新 generation、大消息限额、多路复用流与 `$events/result`、有序排空；已在 Linux x86_64 对接真实 `@deepseek-ai/dsh@0.2.2-rc.5`（含 `dsh-web-trim-patch.yml`）验证 `/connect` + `$events ready` + `/api/settings/describe` & `credentials/describe` 往返；
  - 真机验证裁剪配置启动、握手、收发与优雅停机，并记录与原 `web` 的启动耗时对照。
  - **2026-11-23 真机（M2007J1SC / Android 17 / DSH 0.2.2-rc.5）**：裁剪配置启动、`$events ready` 握手、能力探测、`session/create`、`commands/list`、`session/prompt` 发送与优雅停止均通过，停止后无 guest 残留；启动耗时 n=5 为 9.7–14.0 s（记录基线约 5.9 s，需 `N-08-07` 配对复核）；**真机缺 `protocol.connected` / `protocol.initialized` 测点**（`AgentConnectionScope` 未注入 `StartupTrace`）。收发仅到 DSH 侧，LLM 回合因设备无默认网络以 `TRANSPORT` 失败。见 [ACCEPTANCE_PR00_PR04_DEVICE.md](../native/ACCEPTANCE_PR00_PR04_DEVICE.md)。

---

## PR-02：Gateway Facets、`Outcome<T>`、`DshCapabilities` 与 Golden 契约测试

- **目标**：建立协议无关的领域抽象与能力探测层，用脱敏真实录制样本钉住 DSH 线协议。
- **前置依赖**：PR-01。
- **退出门禁**：纯 `net11.0` 契约测试 100% 通过；真机无 UI 端到端测试可发送文本/图片、触发斜杠命令并完成审批不死锁。

### 任务项

- [x] **N-02-01｜强类型错误与 `Outcome<T>` 基础设施**
  - 在 `DshMobile.Agent/Domain/` 实现 [Failures.cs](../../src/DshMobile.Agent/Domain/Failures.cs)（`DshFailureCode`、`DshFailure`、`Outcome<T>`、`Unit` 及 `DshFailureMapper` 错误码映射表，纯托管、可单测）。
- [x] **N-02-02｜小 Facet Gateway 接口与 `DshCapabilities`**
  - 在 [Gateways.cs](../../src/DshMobile.Agent/Domain/Gateways.cs) 拆分为 `ISessionGateway`、`IPromptGateway`、`IApprovalGateway`、`IQuestionGateway`、`IProviderGateway`、`ICredentialGateway`、`IPluginGateway`、`IEventGateway` 与组合接口 `IDshGateway`；
  - [DefaultDshGateway](../../src/DshMobile.Agent/Domain/Gateways.cs) 默认实现返回 `DshFailureCode.Unavailable`；[DshCapabilities.cs](../../src/DshMobile.Agent/Domain/DshCapabilities.cs) 由运行时握手 + 能力探测动态生成，禁止按硬编码版本号猜测。
- [ ] **N-02-03｜实现协议 Gateway 适配器（补齐全部真实特性）**（纯 `net11.0` 契约与桌面实节点已通过；Android 17 arm64 真机无 UI 端到端 **待真机验证**，按第 0.1 节暂不勾选）
  - 在 [RemoteDshGatewayClient.cs](../../src/DshMobile.Agent/Protocol/RemoteDshGatewayClient.cs) 与 [RemoteProtocolCodec.cs](../../src/DshMobile.Agent/Protocol/RemoteProtocolCodec.cs) 已完成全套 Route B Facet 适配器（Fake Server 全量契约测试与 Linux x86_64 `@deepseek-ai/dsh@0.2.2-rc.5` 冒烟已过）：
  - 支持文本与图片附件（`PromptAttachmentImage` / `ImageAttachmentValidator` 校验 MIME 与大小上限）；
  - 每次 prompt 自动注入并规范化设备当前 **IANA `clientTimeZone`**（`IanaTimeZoneResolver`）；
  - 支持 Route B 原生斜杠命令 `commands/list` & `commands/execute`（`/compact`、`/permission`、`/plan`、`/goal`）；
  - `session/create`（含 `sessionId` 恢复）严格按 schema 发送（`WhenWritingNull`，绝不带未知字段）；
  - 正确解析 `turn/end` 的 `reason` 判定轮次失败，兼容 `session/wait` 返回 `{}` 空对象映射为 `AmbiguousEmptyResponse`。
  - 真机无 UI 端到端测试（发送文本/图片、触发斜杠命令并完成审批不死锁）：**基本通过（联网复测）**——2026-11-23 真机跑通 capability 探测、会话创建/续接、`commands/list`、文本流式（Markdown 表格 + 代码块）、图片视觉问答、工具卡与审批三态（允许 / Hold-to-undo 撤销 / 拒绝），无死锁；遗留：审批卡工具名 `unknown` 且参数未加载（F9）、芯片斜杠命令缺 `/` 报 `command.invalid_syntax`（F10）、拒绝后零执行缺少设备侧独立证据。见 [ACCEPTANCE_PR00_PR04_DEVICE.md](../native/ACCEPTANCE_PR00_PR04_DEVICE.md)。
- [x] **N-02-04｜脱敏 Golden Fixtures 与协议兼容矩阵文档**
  - 从真实 `@deepseek-ai/dsh@0.2.2-rc.5` 录制并脱敏 golden fixtures 存入 [route-b-golden-v0.2.2-rc.5.json](../../tests/DshMobile.Agent.Tests/Fixtures/route-b-golden-v0.2.2-rc.5.json)；
  - 覆盖未知事件/未知字段容错（降级为 `UnknownGatewayEvent` / `SessionFollowUnknownUpdate` / `TurnEndReasonKind.Unknown` / `IsKnownType=false` 不崩溃）、协议版本/格式不匹配返回 `ProtocolMismatch`。
  - 产出：[PROTOCOL_COMPATIBILITY.md](../native/PROTOCOL_COMPATIBILITY.md)。

---

## PR-03：`TranscriptEngine`（含 Cache-First）+ `RequestsEngine`

- **目标**：打造纯 C# 零 Android 依赖的核心聊天与审批引擎，用确定性计数器锁死长流式渲染性能。
- **前置依赖**：PR-02。
- **退出门禁**：所有乱序/重连/审批防诈尸单测及 `Stream6000` 性能门禁全绿。

### 任务项

- [x] **N-03-01｜`SessionKey` 作用域模型与不可变 `ChatSnapshot`**
  - 在 [TranscriptModels.cs](../../src/DshMobile.Agent/Engines/TranscriptModels.cs) 定义 `SessionKey(RuntimeId, ProfileId, SessionId)`、`MessageKey`、`MessagePart`（含 `UnknownMessagePart` 保留）、`ToolPhase`、`RunPhase`、`ChatRow`（稳定 `ItemId`）、`ChatSnapshot(Revision, ...)` 与 `TranscriptCounters`。
- [x] **N-03-02｜`StreamingBlocks` 16 行硬切片增量切块器**
  - 在 [StreamingBlocks.cs](../../src/DshMobile.Agent/Engines/StreamingBlocks.cs) 实现单遍按行顶级 Markdown 块切分：已闭合块（空行/标题/列表项/闭合围栏）永久不可变缓存；
  - 强制超过 16 行的未闭合围栏或长段落按行切为 settled chunk + ≤16 行可变 tail 块；保留嵌套列表深度 `ListDepth` 与连续标记 `ContinuedFromPrevious/ContinuesToNext`。
- [x] **N-03-03｜`TranscriptReducer` 与 Turn 级前缀复用**
  - 在 [TranscriptReducer.cs](../../src/DshMobile.Agent/Engines/TranscriptReducer.cs) 与 [TranscriptEngine.cs](../../src/DshMobile.Agent/Engines/TranscriptEngine.cs) 实现每会话单线程串行 `Channel`、重复事件幂等、有界 `deferredParts`（超阈值置位 `NeedsCalibration` 并触发历史校准）；
  - `ToolPhaseTransitions` 严格单向迁移；按 `turn/end.reason` 落定轮次成败；已稳定历史 Turn 缓存派生行，流式更新仅重算最后 Turn 尾部；子 Agent 事件通过父子验证挂接。
- [x] **N-03-04｜50ms 前缘+后缘合并发布器与 `skippedWhileHidden`**
  - 在 [CoalescingPublisher.cs](../../src/DshMobile.Agent/Engines/CoalescingPublisher.cs) 实现：静默 ≥50ms 后首条 delta 立即发布（前缘零延迟），50ms 窗口内变更合并为窗口末一次发布（后缘）；
  - 观察者计数为 0 时继续 reduce 内存模型但跳过派生发布并累计 `SkippedWhileHidden`，恢复订阅（`0 -> 1`）触发补发同步。
- [x] **N-03-05｜Cache-First `detachedTranscript` 与 `KeptTranscript` 脱敏快照存储**
  - 在 [SecretRedactor.cs](../../src/DshMobile.Agent/Engines/SecretRedactor.cs) 与 [KeptTranscriptStore.cs](../../src/DshMobile.Agent/Engines/KeptTranscriptStore.cs) 实现全字段凭据脱敏、原子写、STJ SourceGen 序列化、`NotFound/Loaded/Corrupt` 分态读取与最近 N 条脱敏展示消息保留；
  - `TranscriptEngine.CreateDetached` 复用同一 reducer 离线渲染缓存，并由 [HistoryReconciler.cs](../../src/DshMobile.Agent/Engines/HistoryReconciler.cs) 完成“先订阅记水位 → 拉快照 → 水位合并”无缝交接（失败保留部分历史标志不伪造完整历史）。
- [x] **N-03-06｜发送状态机与重复执行保护**
  - 在 [SendStateMachine.cs](../../src/DshMobile.Agent/Engines/SendStateMachine.cs) 维护 `Draft -> Dispatching(ClientOpId) -> Enqueued(RequestId) -> Running -> SettledSuccess/SettledFailure | UnknownOutcome | RequiresUserConfirmation`；
  - 路线 B 启用 `requestId` 幂等重试；非幂等通道遇 `UnknownOutcome` 先查历史核对并需用户确认后方可重发。
- [x] **N-03-07｜`RequestsEngine`（参数回溯 + Hold-to-undo + 已结算账本）**
  - 在 [RequestsEngine.cs](../../src/DshMobile.Agent/Engines/RequestsEngine.cs) 实现 `approval/request` 按 `callId` 回溯 `TranscriptEngine` 脱敏参数摘要（缺失则显式 `ArgumentsReady = false`）；
  - 实现 `Waiting -> Held(Hold-to-undo) -> Sending(原子单次认领) -> Settled` 与 `FlushHeldAsync()`、代次已结算账本防诈尸，以及 `user-questions` 与 `CancelRequest` 迟到答复拒绝。
- [x] **N-03-08｜`TranscriptPerfGateTests`（`Stream6000`）与故障回归单测**
  - 在 [TranscriptPerfGateTests.cs](../../tests/DshMobile.Agent.Tests/Engines/TranscriptPerfGateTests.cs)、[StreamingBlocksTests.cs](../../tests/DshMobile.Agent.Tests/Engines/StreamingBlocksTests.cs)、[TranscriptFaultAndReconcileTests.cs](../../tests/DshMobile.Agent.Tests/Engines/TranscriptFaultAndReconcileTests.cs) 与 [RequestsEngineTests.cs](../../tests/DshMobile.Agent.Tests/Engines/RequestsEngineTests.cs) 全部锁定并全绿。

---

## PR-04：纯原生聊天垂直切片（并行入口，可回滚）

- **目标**：交付第一个可真实对话、带流式 Markdown、工具卡、图片与审批问答的纯原生 Material 3 聊天页（作为并行入口，不删 `DshWebActivity`）。
- **前置依赖**：PR-03。
- **退出门禁**：真机不打开 `DshWebActivity` 完成一轮真实带工具调用/审批/图片/斜杠命令的对话；通过横竖屏、深色模式、中文 IME 与长文回看测试。

### 任务项

- [x] **N-04-01｜纯 C# `ChatViewModel` 与 `IChatActions`（驻留 `DshMobileApplication` ConnectionScope）**
  - 在 [ChatContracts.cs](../../src/DshMobile.Agent/Presentation/ChatContracts.cs)、[ChatViewModel.cs](../../src/DshMobile.Agent/Presentation/ChatViewModel.cs) 与 [AgentConnectionScope.cs](../../src/DshMobile.Android/Bridge/AgentConnectionScope.cs) 暴露不可变 `ChatUiState` 与 `SendText/SendWithImages/RunSlashCommand/AnswerApproval/UndoApproval/AnswerQuestion/CancelSession/RefreshHistory`；
  - 绑定到 `BridgeHost.Agent` 应用级 ConnectionScope，免疫 `ChatActivity` 深色模式（`UiMode`）与横竖屏重建。
- [x] **N-04-02｜Markdown 托管渲染器（Markdig → Android `SpannableStringBuilder`）**
  - 在 [MarkdownRenderCache.cs](../../src/DshMobile.Agent/Rendering/MarkdownRenderCache.cs) 将 `StreamingBlocks` 的 settled/tail 块经 Markdig 解析为轻量 IR 并按 `ItemId` 与单调版本号缓存（旧任务结果不得覆盖新版本）；
  - 在 [ChatMarkdownBinder.cs](../../src/DshMobile.Android/Chat/ChatMarkdownBinder.cs) 与 XML 视图模板（`item_chat_md_text.xml`、`view_chat_code_block.xml`、`view_chat_diff_block.xml`、`view_chat_table_block.xml`）实现等宽代码块一键复制与横向滚动、Diff 行高亮、表格展示；外链经安全确认框交受控系统浏览器。
- [x] **N-04-03｜`ChatFragment` 与多 `ViewType` `RecyclerView`**
  - 实现 Material 3 XML 布局 [fragment_chat.xml](../../src/DshMobile.Android/Resources/layout/fragment_chat.xml)、[activity_chat.xml](../../src/DshMobile.Android/Resources/layout/activity_chat.xml) 及 `item_chat_*.xml` 视图模板，在 [ChatTimelineAdapter.cs](../../src/DshMobile.Android/Chat/ChatTimelineAdapter.cs) 落地 `UserMessageHolder`、`AssistantBlockHolder`、`ReasoningHolder`、`ToolCallHolder`、`CommandHolder`、`ApprovalHolder`、`QuestionHolder`、`NoticeHolder`（全 XML 声明，不在代码动态拼建 UI）；
  - 使用稳定 `ItemId`（64 位稳定哈希）与 `DiffUtil` 局部刷新，禁止流式更新引起整表闪动。
- [x] **N-04-04｜滚动锚点、图片附件选择与原生斜杠命令栏**
  - 在 [ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs) 实现「跟随底部 / 用户回看锁止 + 回到底部浮钮」逻辑；
  - 接入 SAF 系统图片选择器（`ImageAttachmentValidator` 校验格式与大小上限转 `PromptAttachmentImage`）；
  - 提供快捷原生斜杠命令入口（`/compact`、`/permission`、`/plan`、`/goal`，按 `DshCapabilities.SlashCommands` 门控）。
- [ ] **N-04-05｜并行入口与真机验收**（并行入口代码与 XML 布局已合入，[ACCEPTANCE_PR04.md](../native/ACCEPTANCE_PR04.md) 已建档；Android 17 arm64 真机 **待真机验证**）
  - 在首页 [fragment_home.xml](../../src/DshMobile.Android/Resources/layout/fragment_home.xml) / [HomeFragment.cs](../../src/DshMobile.Android/Home/HomeFragment.cs) 提供「打开原生会话（实验）」与既有「打开 DSH（Web）」并行入口；
  - 真机验收并补齐 `docs/native/ACCEPTANCE_PR04.md`（含中文 IME、深色切换、旋转、复制选择、返回键、真实审批与拒绝）。
  - **2026-11-23 真机（M2007J1SC / Android 17 / DSH 0.2.2-rc.5）与后续缺陷修复**：初次真机验收（含联网复测）发现 `F1`–`F6`、`F9`–`F14`；当前已在代码层面全部完成修复（含 `waterfall.request`/`cancel`、晚到 `tool/call` 回填、rc.5 事件全集与 `source.kind` 过滤、`session/title`、`FromSessionError`、斜杠 `/` 前缀、宿主重启重连恢复 `PrepareForHostStart`、`ProtocolHandshakeAsync` 测点打通、横屏 IME 收起、移除 `ConfigChanges.UiMode` 与 `Rows.Fmt` 消除 `%1$s` 及 `CS8604` 警告），桌面全部单测与 `Release` 构建已通过，**待重装 APK 真机复测**；设备中文 IME 亦待补。见 [ACCEPTANCE_PR00_PR04_DEVICE.md](../native/ACCEPTANCE_PR00_PR04_DEVICE.md) 与 [ACCEPTANCE_PR04.md](../native/ACCEPTANCE_PR04.md)。

---

## PR-05：原生 Provider / 凭据配置 + Cache-First 首屏 + 完整会话管理

- **目标**：补齐脱离 WebView 必需的首次零配置闭环，上线 0.2s Cache-First 首屏，并打通完整会话列表、历史分页、取消与模型切换。
- **前置依赖**：PR-04。
- **退出门禁**：全新零凭据安装无需 WebView 即可完成配置并对话；强杀重启 0.2s 显示上次对话并无缝校准。

### 任务项（2026-11-23 Android 17 真机：初轮暴露 F15–F19，修复后复测 **F15–F19 全部通过**；仅「全新零凭据安装」场景未重装验证，见 [ACCEPTANCE_PR05.md](../native/ACCEPTANCE_PR05.md) 第 7 节）

- [ ] **N-05-01｜原生 Provider / 凭据配置与首次引导页（P0 必补）**（桌面与构建已完成，**待真机验证**）
  - 落地 `CredentialsViewModel` / `CredentialsActivity` / `CredentialsFragment` / `CredentialsAdapter` 与 Material 3 XML 布局（`activity_credentials.xml`、`fragment_credentials.xml`、`item_credential_entry.xml`、`dialog_credential_input.xml`、`dialog_credential_onboarding.xml`）：支持配置 DeepSeek API Key 及通过 `settings/describe` 发现的 Provider 凭据、查看凭据状态（已配置/未配置，**绝不回显完整明文密钥**）、一键清除凭据；
  - 写入路径严格走 DSH 官方 `credentials/set` / `credentials/delete`（经 `RemoteDshGatewayClient`），不破坏 `WorkspaceProvider` 对 `.dsh/` 的双向不可触保护，严禁落日志与 `OnSaveInstanceState`（已通过 `SavedStateSecurityTests` 与 `LoggingPrivacyTests` 门禁）。
  - **真机（2026-11-23 修复后）通过**：`DEEPSEEK_API_KEY` 显示「已配置 · 来源：file」并给出脱敏掩码；自定义引用新增后立即出现在列表、可清除（F15/F16 关闭）。仅「全新零凭据首次安装」未重装验证。
- [ ] **N-05-02｜模型目录与运行中模型 / Reasoning Effort 切换**（桌面与构建已完成，**待真机验证**）
  - 接入 `IProviderGateway`（`ChatViewModel.RefreshModelCatalogAsync` / `SelectModelAsync`、`ModelPickerDialog`、`dialog_model_picker.xml` / `item_model_picker_row.xml`），在聊天顶栏与模型选择器展示可用 Provider/模型/Reasoning Effort，按 `DshCapabilities.SwitchModelInSession` 门控；凭据缺失时引导跳转 `N-05-01`。
  - **真机（2026-11-23）通过**：模型与档位列表正常；切换档位后顶栏 `· max`，切换到 `deepseek-v4-pro` 后模型实际回复 `final`。
- [ ] **N-05-03｜`ChatsFragment` 会话列表与完整生命周期操作**（桌面与构建已完成，**待真机验证**）
  - 实现 `SessionsViewModel`、`ChatsFragment`、`ChatsAdapter` 与 XML 布局（`fragment_chats.xml`、`item_session_row.xml`、`dialog_session_rename.xml`、`bottom_nav.xml` 新增 `nav_chats`）：支持会话列表、新建、关键词搜索、重命名、Fork、运行中/待审批角标、冷读分页加载（`LoadOlderHistoryAsync` + `TranscriptReducer.PrependOlderEvents`）、按能力启用会话取消（`CancelSession` 不可用时明示 `Unsupported`，绝不以杀进程假装取消）。
  - **真机（2026-11-23 修复后）通过**：列表/搜索/重命名/Fork/运行中角标/停止轮次均可用；点击行标题即可打开会话并显示历史（F17/F18 关闭）。
- [ ] **N-05-04｜Cache-First 首屏路径全面接通**（桌面与构建已完成，**待真机验证**）
  - 将 `MainActivity`、`ChatActivity` 与 `ChatFragment` 接入 `KeptTranscriptStore.TryLoadMostRecentAsync` + `ChatViewModel.LoadMostRecentCachedSessionAsync`：冷启优先渲染最近缓存对话与草稿框，顶部展示真实 `DshHostPhase` 进度条，DSH 就绪后通过 `WaitForIdleDrainAsync` 与增量校准实现无重复行交接；
  - 在 `ChatActivity` 通过 `StartupTrace` 记录 `native.activity_visible` 与 `native.cache_first_rendered`。
  - **真机（2026-11-23 修复后）通过**：测点 `native.activity_visible` = 90.7 ms、`native.cache_first_rendered` = 96.2 ms；DSH 停止时缓存正文可见，启动并连接后正文保留、无空态与重复行（F17 关闭）。
- [ ] **N-05-05｜旧 Web Profile 数据只读盘点与保守迁移提示**（桌面与构建已完成，**待真机验证**）
  - 在 `DshMobile.Runtime.Migration.WebProfileInventory` 生成只读兼容清单（严格基于元数据统计 `sessions/`、`settings.json`、`mcp.json` 与 `.credentials.yaml` 存在性，绝不读取凭据内容、绝不删除或改名原始目录）；在 `ChatsFragment` 展示可关闭只读提示横幅与明细弹窗（`WebProfileInventoryTests` 全绿）。
  - **真机（2026-11-23）通过**：横幅 +「查看详情」列出 `--root-projects-- [需迁移/只读保留]`，可关闭，未读取凭据内容。

---

## PR-06：工作区 Git 集成与跨会话待办收件箱

- **目标**：在既有文件工作台 V2 上补齐 Git 与 Diff 查看能力，并落地跨会话审批/问答收件箱与后台通知联动。
- **前置依赖**：PR-05。
- **退出门禁**：路径安全负面测试通过；后台通知栏与页内审批并发点击仅生效一次；拒绝后零工具执行。

### 任务项

- [ ] **N-06-01｜工作区 Git 状态与 Diff 服务**（桌面单测通过；**2026-11-23 真机部分通过**，见 `docs/native/ACCEPTANCE_PR06.md` §4）
  - 复用现有 [WorkspaceProvider.cs](../../src/DshMobile.Core/Files/WorkspaceProvider.cs) 与 [WorkspaceFileSystem.cs](../../src/DshMobile.Core/Files/WorkspaceFileSystem.cs)（保持 `.dsh/` 双向不可触与 symlink 拒绝策略），新增只读 [WorkspaceGitService.cs](../../src/DshMobile.Core/Files/WorkspaceGitService.cs) 与 [ProotGitCommandExecutor.cs](../../src/DshMobile.Runtime/Guest/ProotGitCommandExecutor.cs)（分支、变更文件列表、单文件 Unified Diff，严格校验仓库根/相对路径越界并强制输出大小与行数上限标注截断）。
  - **真机（2026-11-23）**：真实仓库工作区分支、`M`/`?` 变更与 Unified Diff 行号/增删高亮通过；**未覆盖**：超长 Diff 截断、二进制 Diff。
- [ ] **N-06-02｜`FilesFragment` 增量接入 Git 页签与原生 Diff 查看器**（桌面单测通过；**2026-11-23 真机部分通过**，见 `docs/native/ACCEPTANCE_PR06.md` §4）
  - 在 [fragment_files.xml](../../src/DshMobile.Android/Resources/layout/fragment_files.xml)、[item_git_change.xml](../../src/DshMobile.Android/Resources/layout/item_git_change.xml)、[item_git_diff_line.xml](../../src/DshMobile.Android/Resources/layout/item_git_diff_line.xml) 与 [DiffLineView.cs](../../src/DshMobile.Android/Files/DiffLineView.cs) 增加 Git 状态视图与原生 Diff 查看器（行号、增删高亮、超长截断提示、主线程零解析），并通过 [WorkspacePathExtractor.cs](../../src/DshMobile.Agent/Presentation/WorkspacePathExtractor.cs) 支持从聊天工具卡点击安全工作区路径直达预览/Diff。
  - **真机（2026-11-23）**：Git 页签切换、变更列表与 Diff 查看器通过；登记 **F23（低）**（行点击区域偏小）；**未覆盖**：聊天工具卡点击路径直达预览/Diff。
- [x] **N-06-03｜跨会话待办收件箱（`InboxSheet/Fragment`）与导航角标**（**2026-11-23 真机通过**，见 `docs/native/ACCEPTANCE_PR06.md` §4）
  - 新增纯托管 [InboxViewModel.cs](../../src/DshMobile.Agent/Presentation/InboxViewModel.cs) 聚合 `RequestsEngine` 全局待审批与待问答请求，落地 XML 布局 [sheet_inbox.xml](../../src/DshMobile.Android/Resources/layout/sheet_inbox.xml) 与 [InboxBottomSheet.cs](../../src/DshMobile.Android/Inbox/InboxBottomSheet.cs)，在 [MainActivity.cs](../../src/DshMobile.Android/MainActivity.cs) 顶部横幅与 `nav_chats` 角标实时提示；点击条目直达 `ChatActivity` 对应会话并定位卡片。
  - **真机（2026-11-23）**：`ask_user_question` 触发后，首页顶部横幅「共有 1 项待处理（审批 0 · 问答 1）」、会话角标「待确认 1」、收件箱列表与「跳转会话」直达卡片均实测通过。
- [ ] **N-06-04｜后台审批 / 问答系统通知通道**（桌面单测通过；**2026-11-23 真机仅问答通过，审批未验证**，见 `docs/native/ACCEPTANCE_PR06.md` §4）
  - 新增 [BackgroundRequestNotificationCoordinator.cs](../../src/DshMobile.Agent/Presentation/BackgroundRequestNotificationCoordinator.cs) 与 [AgentRequestNotifications.cs](../../src/DshMobile.Android/Notifications/AgentRequestNotifications.cs)：App 处于后台收到 `approval/request` 或 `user-questions/request` 时向独立通道 `dshmobile_agent_requests` 发高优先级脱敏通知（绝不含敏感参数全文与密钥）；
  - 快捷操作使用 `Exported = false` 的 `AgentRequestNotificationReceiver` + `PendingIntentFlags.Immutable` + 一次性随机数并经 `RequestsEngine` 原子认领（与页内卡片防双发，拒绝后零工具执行）；与 Relay `ui_*` 审批通知通道隔离，绝不跨域放行。见 [ACCEPTANCE_PR06.md](../native/ACCEPTANCE_PR06.md)。
  - **真机（2026-11-23）**：后台问答通知（渠道 `dshmobile_agent_requests`、`vis=PRIVATE`）通过；**未验证**：因 `approval/policy=never` 无法构造 `approval/request`，通知「允许一次/拒绝」快捷按钮与「拒绝后零执行」待补样本。

---

## PR-07：原生插件目录与受控管理（L0 / L1）

- **目标**：恢复轻量、可维护的原生插件状态视图与受控管理入口，不重蹈 ADR-0028 维护两套插件语义的覆辙。
- **前置依赖**：PR-05（可与 PR-06 并行）。
- **退出门禁**：插件报错/装卸失败不影响宿主稳定；需重启 DSH 时提示清晰；无自创 L2 表单解析代码。

### 任务项

- [ ] **N-07-01｜`PluginInventoryService` 与受控变更服务（L0 / L1）**（桌面单测通过；**2026-11-23 真机 L0/启停闭环通过，真实安装/卸载未覆盖**，见 `docs/native/ACCEPTANCE_PR07.md` §5）
  - 仅经官方 `dsh-host-plugin-inventory` / `plugin_manager` 或受控 argv CLI（沿用 `GuestCommandLine` 逐 token 单引号转义）读取插件名称、版本、启用态、错误摘要及技能目录；
  - 安装/卸载/开关操作严格白名单校验 + 用户显式确认，不在 Android 侧重写 npm 安装逻辑。
  - **真机（2026-11-23）**：L0 清单合并、L1 受控启停切换、`RestartWebHostAsync` 重启恢复闭环通过，提交非法包名零安装触发；**未覆盖**：真实第三方 npm 安装/卸载（需改写 `profiles/web/cordis.patch.yml`）与 Snackbar 校验失败提示窗口捕获。
- [ ] **N-07-02｜`PluginsFragment`（Material 3）**（桌面与 Release 构建通过；**2026-11-23 真机 L0/启停通过，自定义 WebUI 未覆盖**，见 `docs/native/ACCEPTANCE_PR07.md` §5）
  - 展示已装插件清单、状态标签、报错摘要、启停/装卸入口，以及明确的「需要重启 DSH 生效」横幅；
  - 对携带自定义 WebUI 的第三方插件如实显示“此插件后端正常运行，但暂无原生管理界面”，不伪装兼容、不内嵌 WebView。
  - **真机（2026-11-23）**：「更多 → 插件目录」、未启动提示、清单/保护徽标、启停确认框、重启横幅触发与清除通过；登记 **F20（低，文案）**、**F21（低，提示）**；**未覆盖**：携带自定义 WebUI 的第三方插件提示文案（环境无此类插件）。
- [x] **N-07-03｜回归测试与边界核对**
  - 单测覆盖：非法包名拒绝（零进程启动）、安装/卸载失败后状态恢复、重启提示触发与宿主重启清除、错误日志经 `SecretRedactor` 脱敏（不含插件配置密钥），以及零 L2 动态表单 / 零 WebView 边界校验（`Runtime`/`Application`/`Agent` 单测及 5 组 `Quality` 门禁已通过；真机交互待测）。

---

## PR-08：系统治理、可靠性、在线更新探针与远程主机适配

- **目标**：把原生客户端的后台稳定性、进程清理、Android 17 适配、在线更新探针与远程 SSH 全部收口到生产级，为删除 WebView 扫清一切阻断。
- **前置依赖**：PR-05（核心项），PR-06 / PR-07（全回归）。
- **退出门禁**：Android 17 真机完成后台/锁屏/强杀/断网/在线更新回滚/远程主机/32 进程与 A/B/C 性能配对验收。

### 任务项

- [x] **N-08-01｜升级 `ProcessTable` 为 `NativeOrphanReaper` 多条件孤儿回收**（桌面假 `/proc` 单测与 `DshHost` 启停接线已绿；2026-11-24 真机：`idle-stop` 优雅停机与 `am force-stop` 后 guest 进程均 0 残留；仅杀 App 进程的收养场景因非 root 无法制造，reaper 仍只有单测证据）
  - 修正 [ProcessTable.cs](../../src/DshMobile.Runtime/Supervision/ProcessTable.cs) 使用 `LastIndexOf(')')` 解析 `stat`，补齐 `uid`、`startTicks`、`TracerPid`、`/proc/<pid>/exe`（兼容 ` (deleted)`）；
  - 在 [NativeOrphanReaper.cs](../../src/DshMobile.Runtime/Supervision/NativeOrphanReaper.cs) 实现保守多条件交集孤儿识别（`uid == appUid && parent == 1 && tracer == 0 && exe matches libproot-loader*.so && !inLiveTrees`）与发信号前 `startTicks` 二次校验，并在 [DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs) 启动前与停止后执行安全清理 + [SupervisionTests.cs](../../tests/DshMobile.Runtime.Tests/SupervisionTests.cs) 完整离线 `/proc` 假树单测。
- [x] **N-08-02｜`WorkLeaseManager` 与 `IdleStopPolicy`**（桌面实现、单测与 Release 裁剪构建已通过；2026-11-24 Android 17 真机：后台 3 min 阈值触发 `idle-stop: graceful ok (Ready)` 且 guest 进程 0 残留，Chat 租约活跃时后台 4 min 不误杀，运行环境页显示「活跃租约 1 项：Chat」）
  - 已在 [WorkLeaseManager.cs](../../src/DshMobile.Application/Runtime/WorkLeaseManager.cs) 实现带单租约上限（默认 10m）与连续总上限（默认 30m）的 `WorkLeaseManager`（覆盖 `Chat / Approval / Setup / Update / Reconcile`），单调时钟倒退/负值即清空租约并归零计数；
  - 已在 [IdleStopPolicy.cs](../../src/DshMobile.Application/Runtime/IdleStopPolicy.cs)、[SystemGovernanceMonitor.cs](../../src/DshMobile.Android/Governance/SystemGovernanceMonitor.cs) 与 [BridgeHost.cs](../../src/DshMobile.Android/Bridge/BridgeHost.cs) 实现并接线 `IdleStopPolicy`（仅后台 + 无活跃租约 + 确认 Agent 全 Idle + 超过阈值才触发有序优雅停机），并与 `WarmupPolicy` 协同 + [GovernancePolicyTests.cs](../../tests/DshMobile.Application.Tests/GovernancePolicyTests.cs) 单测覆盖。
- [x] **N-08-03｜Android 12+ 32 Phantom Process 预算监控与引导**（桌面实现、单测与 Release 裁剪构建已通过；2026-11-24 真机：31/32 显示「已接近上限…可能触发 SIGKILL(137)」并成功跳转开发者选项。`exit 137` 的 UI 归因仍未复现——32 上限时系统即时回收新进程，登记 F25，映射仍仅单测）
  - 已基于 [PhantomProcessMonitor.cs](../../src/DshMobile.Runtime/Supervision/PhantomProcessMonitor.cs) 统计同 UID 子进程数，≥28 在 [RuntimeActivity.cs](../../src/DshMobile.Android/RuntimeUi/RuntimeActivity.cs) 运行环境页告警并经 `SystemGovernanceMonitor` 提供 API 34+ 一键跳开发者选项引导（只引导不篡改设置）；
  - 已在 [ProotGuestProcess.cs](../../src/DshMobile.Runtime/Guest/ProotGuestProcess.cs)、[DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs) 与 [Failures.cs](../../src/DshMobile.Agent/Domain/Failures.cs) 捕获 `exit 137` 归类为 `DshFailureCode.PhantomProcessLimit` + [GovernanceRuntimeTests.cs](../../tests/DshMobile.Runtime.Tests/GovernanceRuntimeTests.cs) / [RemoteGatewayAdapterTests.cs](../../tests/DshMobile.Agent.Tests/Transport/RemoteGatewayAdapterTests.cs) 覆盖。
- [ ] **N-08-04｜Android 17 Memory Limiter 死因分类与 `HeatTierPolicy` 温控阶梯**（桌面实现、单测与 Release 裁剪构建已通过；**真机未验证**：2026-11-24 未能触发 MemoryLimiter，低电模拟不改变 `WarmupPower` 判断（F26）、中重度温控未制造；面板仅显示 Nominal / 未检测到异常终止）
  - 已在 [HistoricalExitClassifier.cs](../../src/DshMobile.Application/Runtime/HistoricalExitClassifier.cs) 与 `SystemGovernanceMonitor` 接入启动读取 `ActivityManager.GetHistoricalProcessExitReasons()`，识别 `MemoryLimiter:AnonSwap` 映射为 `DshFailureCode.MemoryLimited`（仅存固定安全诊断键，不存原始系统描述）；
  - 已在 [HeatTierPolicy.cs](../../src/DshMobile.Application/Runtime/HeatTierPolicy.cs)、[WarmupPolicy.cs](../../src/DshMobile.Application/Runtime/WarmupPolicy.cs) 与 `SystemGovernanceMonitor` 接入 `PowerManager.IOnThermalStatusChangedListener` / 限频 `GetThermalHeadroom(30)`（≥30s）与电量状态，在中重度发热/低电时抑制后台预热与并发、不强杀活跃会话。
- [ ] **N-08-05｜抽象并切换在线更新健康探针（ADR-0024 / ADR-0029）**（桌面实现、单测与 Release 裁剪构建已通过；**真机未验证**：2026-11-24 检查更新因 GitHub 清单不可达返回 `MANIFEST_FETCH_FAILED`，无更新层可导入，探测提交/回滚/强杀均未覆盖）
  - 已在 [DshHost.cs](../../src/DshMobile.Runtime/Hosting/DshHost.cs) 定义并实现 `IDshHost.ProbeHealthAsync(ct)`，并将 [RuntimeCoordinator.cs](../../src/DshMobile.Application/Runtime/RuntimeCoordinator.cs) 与 [UpdateCoordinator.cs](../../src/DshMobile.Application/Updates/UpdateCoordinator.cs) 的 `probing` 阶段由 `StartHostAsync` + `StopHostAsync` 抽象切换至 `ProbeHealthAsync`；
  - 已更新并补充 [RuntimeCoordinatorTests.cs](../../tests/DshMobile.Application.Tests/RuntimeCoordinatorTests.cs)、[RuntimeCoordinatorIsolationTests.cs](../../tests/DshMobile.Application.Tests/RuntimeCoordinatorIsolationTests.cs) 与 [UpdateCoordinatorTests.cs](../../tests/DshMobile.Application.Tests/UpdateCoordinatorTests.cs) 覆盖 DSH/Runtime 换层探测成功提交、探测失败自动回滚与强杀恢复单测（真机待测）。
- [x] **N-08-06｜远程主机 `RemoteSshSession` 原生会话接入**（桌面实现、单测与 Release 裁剪构建已通过；2026-11-24 真机：SSH 指纹核对固定 → 启动远端 DSH（HTTP 401 + token）→ 原生对话 `REMOTE_OK` → 杀会话后 `transport.timeout` 与未确认发送 → 重连 `RECOVERED_OK` → 更换主机密钥被拒绝。**远程端为本机 WSL sshd + `adb reverse` 隧道，非真实 LAN**）
  - 已在 [RemoteSshSession.cs](../../src/DshMobile.Runtime/Remote/RemoteSshSession.cs) 实现 `IDshTokenSource` 与内存 `LoopbackBaseUri`，扩展 [RemoteConnectionOptions.cs](../../src/DshMobile.Agent/Transport/RemoteConnectionOptions.cs) / [DshRemoteConnection.cs](../../src/DshMobile.Agent/Transport/DshRemoteConnection.cs) 动态回环连接并在 [AgentConnectionScope.cs](../../src/DshMobile.Android/Bridge/AgentConnectionScope.cs)、[RemoteActivity.cs](../../src/DshMobile.Android/Remote/RemoteActivity.cs) 与 [ChatActivity.cs](../../src/DshMobile.Android/Chat/ChatActivity.cs) 接入原生会话入口（保持 Keystore 不可导出私钥、主机指纹固定、`HOST_KEY_CHANGED` 拒绝、仅本地 `127.0.0.1` 转发不变）+ `RemoteGatewayAdapterTests` 覆盖；
  - 已于 2026-11-24 在本机 WSL sshd + `adb reverse` 测试隧道上完成真机连接 → 启动远端 DSH（HTTP 401 + token）→ 原生对话 `REMOTE_OK` → 杀会话 `transport.timeout` → 重连 `RECOVERED_OK` → `HOST_KEY_CHANGED` 拒绝；真实 LAN / 公网路径待补。
- [x] **N-08-07｜真机统一性能配对采集（`tooling/startup-perf.mjs`）**（2026-11-24 完成：A/B/C 各 10 次，`bridge.complete` B P50 16.83 s / C 17.98 s，`node_init` 占 95.5% / 94.8%）
  - 已在 Xiaomi M2007J1SC / Android 17 上按 [STARTUP_PERF.md](../STARTUP_PERF.md) 完成 A/B/C 各 10 次采集（PTP-AN10 的 2026-11-17 基线仍见 `migration/parity/`），报告见 [PERFORMANCE_BASELINE.md](../../docs/native/PERFORMANCE_BASELINE.md)；RSS 与空闲 CPU 对照尚未纳入本工具输出。
  - 产出：`docs/native/PERFORMANCE_BASELINE.md`。

---

## PR-09：默认入口切换与彻底清理 WebView

- **目标**：完成默认路径切换，彻底移除业务 WebView 代码与资源，启用零 WebView CI 门禁，完成全量真机覆盖安装回归。
- **前置依赖**：PR-00 ～ PR-08 全部合入，且 P0 场景 T01–T11 具备真实设备验收记录。
- **退出门禁**：全仓/Manifest/依赖图/APK 零业务 WebView；覆盖安装旧版 APK 无数据丢失；所有测试与 CI 绿灯。

### 任务项

- [ ] **N-09-01｜切换默认主入口至原生会话**
  - 更新 `MainActivity` 默认 Tab 与导航结构（`会话 | 工作区 | 更多` + 600dp `NavigationRailView`），移除并行旧 Web 入口开关；确保升级保留导航恢复兼容（沿用 tag 恢复机制，不触发旧 Fragment 崩溃）。
- [ ] **N-09-02｜彻底删除业务 WebView 栈**
  - 先全局检索引用确认无遗留后删除：[DshWebActivity.cs](../../src/DshMobile.Android/DshWebActivity.cs)、[CSharpWebUiController.cs](../../src/DshMobile.Android/WebUi/CSharpWebUiController.cs)、`activity_web.xml`、废弃的 `WebUiPolicy` / `DshOriginPolicy` 及对应文案与旧 WebView 单测；
  - 检查 `AndroidManifest.xml` 与 `DshMobile.Android.csproj`，确认无无用权限与依赖；外部网页预览统一走受控系统浏览器。
- [ ] **N-09-03｜启用 `NoBusinessWebViewGateTests` 永久 CI 门禁**
  - 在 `tests/DshMobile.Agent.Tests/Quality/` 启用源码、XML、Manifest 扫描，永久禁止业务路径重新引入 `Android.Webkit.WebView`。
- [ ] **N-09-04｜同步仓库文档与验收证据**
  - 更新 [ARCHITECTURE.md](../ARCHITECTURE.md)、[FEATURES.md](../FEATURES.md)、[SECURITY.md](../SECURITY.md)、[COMPATIBILITY.md](../COMPATIBILITY.md)、[README.md](../../README.md) 及 [MIGRATION.md](../MIGRATION.md)；
  - 完成从旧 WebView Release APK 覆盖安装到新原生 Release APK 的真机 T01–T12 全套验收，归档报告至 `docs/native/ACCEPTANCE_MATRIX.md`。

---

## 附表 A：P0 验收场景（T01–T12）与任务映射

| 场景 ID | 验收目标 | 归属 PR / 核心任务 | 当前状态 |
|---|---|---|---|
| **T01** | 全新安装 → 运行层就绪 → 原生凭据配置 → 首次真实对话 | PR-04 / PR-05（`N-04-03`、`N-05-01`） | `[ ]` 待测 |
| **T02** | 已安装重启 → 0.2s Cache-First 首屏 → 后台就绪无闪烁校准 | PR-03 / PR-05（`N-03-05`、`N-05-04`） | `[ ]` 待测 |
| **T03** | 入队回执与 `turn/end` 分离，失败轮次如实报错不假成功 | PR-02 / PR-03（`N-02-03`、`N-03-03`） | `[ ]` 待测 |
| **T04** | 后台到达审批/问答 → 通知 + 参数回溯 + Hold-to-undo + 拒绝零执行 | PR-03 / PR-06（`N-03-07`、`N-06-04`） | `[ ]` 待测 |
| **T05** | 多并发 Session 与 Subagent 隔离，隐藏页跳过派生 | PR-03 / PR-04（`N-03-01`、`N-03-04`） | `[ ]` 待测 |
| **T06** | 运行中杀 DSH/断线 → 恢复校准无重复消息、不盲目重发 | PR-03 / PR-05（`N-03-06`） | `[ ]` 待测 |
| **T07** | 6,000 词 + 400 行代码块流式不卡主线程，回看不被抢滚 | PR-03 / PR-04（`N-03-02`、`N-03-08`、`N-04-04`） | `[ ]` 待测 |
| **T08** | Relay `ui_*` 双安全域隔离（`FLAG_SECURE`）+ 插件装卸边界 | PR-01 / PR-06 / PR-07（`N-01-01`、`N-07-01`） | `[ ]` 待测 |
| **T09** | Android 17 loopback / LAN（ADR-0032）/ 公网 / SSH 远程原生对话 | PR-08（`N-08-06`） | `[~]` SSH 远程原生对话、断线、重连与 `HOST_KEY_CHANGED` 已于 2026-11-24 通过（测试隧道）；LAN / 公网路径待测 |
| **T10** | 从旧 WebView 版本覆盖安装保留工作区、凭据与可恢复会话 | PR-05 / PR-09（`N-05-05`、`N-09-04`） | `[ ]` 待测 |
| **T11** | 停机/强杀无孤儿进程；32 进程上限与 A17 内存限制清晰归因 | PR-08（`N-08-01`、`N-08-03`、`N-08-04`） | `[~]` 停机与 `force-stop` 零残留、31/32 预警已过；32 上限下 `exit 137` 归因与 A17 MemoryLimiter 归因待补 |
| **T12** | DSH/Runtime 在线更新探针与回滚通过；全仓与 APK 零业务 WebView | PR-08 / PR-09（`N-08-05`、`N-09-02`、`N-09-03`） | `[ ]` 待测 |

---

## 附表 B：CI 自动化门禁启用进度表

| 门禁测试 | 引入 PR | 收紧/强制节点 | 状态 |
|---|---|---|---|
| `ArchitectureBoundaryTests` | PR-00（`N-00-07`） | PR-00 起零容忍 | `[x]` |
| `TrimAndSerializationGateTests` + Release 警告检查 | PR-00（`N-00-07`） | 每个 PR 必过（零新增 Trim 警告） | `[x]` |
| `CancellationAndFailureGateTests` | PR-00（`N-00-07`） | PR-00 起零容忍 | `[x]` |
| `SavedStateSecurityTests` | PR-00（`N-00-07`） | PR-00 起零容忍 | `[x]` |
| `LoggingPrivacyTests` | PR-00（`N-00-07`） | PR-00 起零容忍 | `[x]` |
| `TranscriptPerfGateTests`（`Stream6000`） | PR-03（`N-03-08`） | PR-03 起锁定上限 | `[x]` |
| `NoBusinessWebViewGateTests` | PR-09（`N-09-03`） | PR-09 起永久生效 | `[ ]` |

---

## 附表 C：真机验收证据登记表（无证据一律写「待真机验证」）

| 阶段 | 设备 / 系统 / 构建 | APK SHA-256 / Commit | DSH / Node 版本 | 结论 | 报告路径 |
|---|---|---|---|---|---|
| PR-00 基线与 PoC | M2007J1SC / LineageOS / Android 17 (API 37) / CP2A.260605.016 | `14AEEEF2…C1F619A` / `df5e942`+PR-04 工作树 | DSH `0.2.2-rc.5` / Node 26.13.1 | 通过（入口/层版本/WebView 回滚无回退） | `docs/native/ACCEPTANCE_PR00_PR04_DEVICE.md` |
| PR-01 持久传输握手 | 同上 | 同上 | 同上 | 部分通过：启动→握手→停止→无孤儿；缺 `protocol.*` 测点；启动 9.7–14.0 s 待配对 | `docs/native/ACCEPTANCE_PR00_PR04_DEVICE.md` |
| PR-02 Gateway 契约 & 无 UI 端到端 | 同上 | 同上 | 同上 | 基本通过（联网）：文本/图片/审批三态真机跑通；F9 审批参数、F10 芯片斜杠命令待修 | `docs/native/ACCEPTANCE_PR00_PR04_DEVICE.md` |
| PR-04 原生聊天切片 | 同上 | 同上 | 同上 | 修复后真机复测：**PASS 18 / FAIL 0 / INFO 2**；F1/F2/F4/F5/F9/F10/F11/F12/F13/F14 通过，**F6（横屏 + 输入法）仍未修复**、中文 IME 未验证 | `docs/native/ACCEPTANCE_PR04.md` |
| PR-05 零凭据引导 & Cache-First | M2007J1SC / LineageOS / Android 17 (API 37) / CP2A.260605.016 | `CC7FD87E…3340EBF2` / PR-05 工作树 | DSH `0.2.2-rc.5` / Node 26.13.1 | 修复后复测通过：F15–F19 关闭；`native.cache_first_rendered`=96.2 ms；仅「全新零凭据安装」未重装验证 | `docs/native/ACCEPTANCE_PR05.md` |
| PR-06 Git / Diff / 收件箱 / 通知 | M2007J1SC / LineageOS / Android 17 (API 37) / CP2A.260605.016 | `B6E7FD99…594686` / `pr06-device-test` | DSH `0.2.2-rc.5` / Node 26.13.1 | 部分通过：Git/Diff、收件箱与后台问答通知通过；审批动作（`approval/policy=never`）、超长/二进制 Diff 与工具卡点击未覆盖；登记 F22–F23 | `docs/native/ACCEPTANCE_PR06.md` |
| PR-07 插件目录 L0 / 受控 L1 | M2007J1SC / LineageOS / Android 17 (API 37) / CP2A.260605.016 | `57F522B9…2A511D` / `pr07-device-test` | DSH `0.2.2-rc.5` / Node 26.13.1 | 部分通过：L0 清单、L1 启停与重启闭环通过；真实装卸与自定义 WebUI 未覆盖；登记 F20–F21 | `docs/native/ACCEPTANCE_PR07.md` |
| PR-08 治理/更新/远程/性能配对 | M2007J1SC / LineageOS 24.0 / Android 17 (API 37) / 非 root | `C0A35590…B430E8` / `feat/native-pr08-governance-remote-probe` | DSH `0.2.2-rc.5` / Node 26.13.1 | 部分通过：`N-08-02`、`N-08-03` 31/32 预警、`N-08-06`（测试隧道）、`N-08-07`（A/B/C）通过；`N-08-04`/`N-08-05`/`exit 137`/LAN 待补；登记 F24–F26 | `docs/native/ACCEPTANCE_PR08.md`、`docs/native/PERFORMANCE_BASELINE.md` |
| PR-09 覆盖安装 & 零 WebView 终验 | 待真机验证 | — | — | 待测 | `docs/native/ACCEPTANCE_MATRIX.md` |
