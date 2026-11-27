# DSH Mobile 协议兼容矩阵（Route B / `@deepseek-ai/dsh@0.2.2-rc.5`）

- **制定依据**：[ADR-0033](../ADR/0033-native-agent-client.md)、[路线 B PoC 报告](POC_ROUTE_B_REMOTE.md)、[runtime-build.lock](../../runtime-bundle/runtime-build.lock) 锁定基线（`@deepseek-ai/dsh@0.2.2-rc.5`、`sessionHeader.version = 4`）。
- **脱敏 Golden Fixture**：[route-b-golden-v0.2.2-rc.5.json](../../tests/DshMobile.Agent.Tests/Fixtures/route-b-golden-v0.2.2-rc.5.json)。

---

## 1. 认证与长连接协议

| 通道 | 契约要求 | 客户端规则与兼容说明 |
|---|---|---|
| `GET /connect?token=<launchToken>&interactions=answer` | 返回 `302` + `Set-Cookie: dsh-auth-<port>=v2.<payload>.<sig>; HttpOnly` | 仅允许 `127.0.0.1`/loopback；禁用代理与自动重定向；token 绝不落日志 |
| `WS /api/remote.mux` | 必须伴随 `Cookie: dsh-auth-...` 长驻打开 | `v2` Cookie 绑定 WebSocket Peer 生命周期；关闭 WS 即释放 Peer，后续 `/api/*` 返回 `401` |
| `$events` 逻辑流 | 客户端首先打开 `$events`，必须收到首帧 `item.value.type == "ready"`（含 `clientId` 与 `host.home`） | 首帧非 `ready` 直接拒绝建立代次（`RemoteProtocolException`）；断线重连生成单调递增 `GenerationId` |

---

## 2. Gateway Facets ↔ Route B 端点映射与参数约束

> 注意：`@deepseek-ai/dsh@0.2.2-rc.5` 的 Typert 控制器对大多数方法采用具名外层包装（`{ request: ... }` 或 `{ _request: ... }`），且启用严格 schema 校验：**禁止携带未声明字段**。

| Facet | 方法 | Route B 端点 / 流 | Wire 参数外层封装 | 关键兼容与容错规则 |
|---|---|---|---|---|
| `ISessionGateway` | `ListSessionsAsync` | `POST /api/session/list` | `{ "_request": { "cursor"? } }` | 返回 `SessionSummary[]`；保留 `parentSessionId`/`origin`/`formatStatus` |
| `ISessionGateway` | `CreateOrResumeSessionAsync` | `POST /api/session/create` | `{ "request": { "workspaceId"?, "cwd"?, "sessionId"?, "agentPreset"? } }` | 严格 `WhenWritingNull`，绝不附带未知字段（对应路线 A `session/resume` 约束） |
| `ISessionGateway` | `GetSessionStateAsync` | `POST /api/session/state` | `{ "request": { "sessionId" } }` | 读取 `cursor`、`active`、`running`、`queue`、`pendingInput` |
| `ISessionGateway` | `LoadSessionEventsAsync` | `POST /api/session/events` | `{ "request": { "address": { "kind": "session", "sessionId" }, "throughSeq"?, "beforeSeq"?, "maxMessages"? } }` | 未知事件 `type` 保留为 `IsKnownType = false`，不抛异常 |
| `ISessionGateway` | `WaitForSessionAsync` | `POST /api/session/wait` | `{ "request": { "sessionId", "timeoutMs"?, "afterSeq"?, "maxMessages"? } }` | 若上游返回空对象 `{}`（历史兼容行为）→ 返回 `AmbiguousEmptyResponse`，绝不误判为已完成 |
| `ISessionGateway` | `RenameSessionAsync` / `ForkSessionAsync` | `POST /api/session/rename` / `fork` | `{ "request": { ... } }` | 透传服务端校验失败至 `DshFailureMapper` |
| `ISessionGateway` | `ArchiveSessionAsync` / `UnarchiveSessionAsync` | `POST /api/workspace/archiveSession` / `unarchiveSession` | `{ "request": { "sessionId", "stopActivity"? } }` / `{ "request": { "sessionId" } }` | 严格 `WhenWritingNull`（仅 `stopActivity=true` 时序列化该字段）；返回最新 `archivedSessionIds`；`workspace/session-active` → `DshFailureCode.SessionActive`，`session/not-found` → `DshFailureCode.SessionNotFound` |
| `ISessionGateway` / `IEventGateway` | `GetArchivedSessionIdsAsync` / `FollowWorkspaceAsync` | `WS /api/remote.mux` (`workspace/follow`) | `{}` | 首帧 `baseline` 携带 `value.archivedSessionIds` 与 `value.pinnedSessionIds`，增量帧支持 `archived` 与 `replace`，未知帧降级为 `UnknownWorkspaceFollowFrame`；`ProbeCapabilitiesAsync` 探测不支持时 `ArchiveSessions=false` 不阻断连接 |
| `IPromptGateway` | `SendPromptAsync` | `POST /api/session/prompt` | `{ "request": { "requestId", "sessionId", "mode": "queue"\|"steer", "content", "clientTimeZone" } }` | 发送前校验图片 MIME（`png`/`jpeg`/`webp`/`gif`）与大小上限；自动规范化并注入合法 IANA `clientTimeZone` |
| `IPromptGateway` | `ListCommandsAsync` / `ExecuteCommandAsync` | `POST /api/commands/list` / `commands/execute` | `{ "agentId", "line", "submittedAttachments" }` | Route B 下用 `commands/execute` 执行 `/compact`、`/permission`、`/plan`、`/goal` 等原生斜杠命令并返回 `commandId` |
| `IPromptGateway` | `CancelTurnAsync` | `POST /api/session/cancel` | `{ "request": { "sessionId" } }` | 幂等请求取消当前活跃轮次 |
| `IApprovalGateway` | `ReplyApprovalAsync` | `POST /api/$events/result` | `{ "clientId", "eventId", "outcome": { "kind": "result", "value": "allowed-once"\|"rejected"\|"cancelled" } }` | Fail-closed：仅允许显式放行 `"allowed-once"`；绝不跨域继承 Relay `ui_*` 权限 |
| `IQuestionGateway` | `ReplyOpenQuestionAsync` / `RejectOpenQuestionAsync` / `ReplyContinuedQuestionAsync` | `POST /api/$events/result` 或 `POST /api/userQuestions/answer` | Waterfall 用 `$events/result`；延续态用 `{ "agentId", "callId", "answer" }` | 支持单选、多选、`custom` 与拒绝撤销 |
| `IProviderGateway` | `GetModelCatalogAsync` / `SelectSessionModelAsync` | `POST /api/session/modelCatalog` / `session/selectModel` | `selectModel`: `{ "request": { "sessionId", "provider", "model", "reasoningEffort"? } }` | `session/model-unavailable` → `DshFailureCode.SessionBusy` |
| `ICredentialGateway` | `DescribeCredentialsAsync` / `SetCredentialAsync` / `UnsetCredentialAsync` | `POST /api/credentials/describe` / `set` / `unset` | `{ "refs" }` / `{ "ref", "value" }` / `{ "ref" }` | 只回显脱敏状态，明文密钥仅走 HTTPS/loopback 请求体，不落盘日志 |
| `IPluginGateway` | `GetPluginInventoryAsync` | `POST /api/pluginInventory/list` | `{}` | 一期仅开放只读清单（`PluginMutation = false`） |
| `IEventGateway` | `FollowSessionAsync` / `ForwardedEvent` | `WS /api/remote.mux` (`session/follow` + `$events`) | `session/follow`: `{ "request": { "address", "activate": true, "assistantStream": true } }` | 未知 follow frame 与未知转发事件均降级为 `Unknown*` 变体，绝不崩溃 |

---

## 3. 已知事件与未知事件 / `turn/end.reason` 退化矩阵

- **已知 `SessionWireEvent.Type`**：已对齐 `0.2.2-rc.5` `KNOWN_SESSION_EVENT_TYPES`（63 种会话事件，含 `turn/start`、`turn/end`、`user/message`、`assistant/*`、`tool/*`、`command/*`、`session/title`、`agent/inbox/spliced`、`session/end-seed`、`session-log-deepseek/delivery-accepted`、`deliverables/presented`、`workspace/changes`、`job/*`、`request/context`、`llm/retry*`、`compaction/*`、`hook/*`、`subagent/*`、`team/*` 等）；其中 `user/message` 仅当 `source.kind == "user"`（兼容无 `source` 的旧夹具并排除 `<system-reminder>`）渲染为聊天气泡，`session/title` 更新会话标题，其余运行时内部状态事件在时间线静默吸收，不再产生 `UnknownEventRow`/`NoticeRow` 刷屏。
- **`$events` `waterfall` / `cancel` / `emit`**：网关转发 `approval/request` 与 `user-questions/request`（`type: "waterfall"`）将请求体放在 `request` 字段（而非 `emit` 使用的 `args`），解码器按 `payload.Request ?? payload.Args` 读取并在 `type: "cancel"` 时移除对应待审批/问答卡片；若 `tool/call` 晚于 `approval/request` 到达，`RequestsEngine` 通过 `TranscriptEngine.ToolInvocationRecorded` 回调自动回填 `ToolName` 与脱敏参数。
- **未知 `SessionWireEvent.Type`**：映射为 `SessionWireEventView(IsKnownType: false)`，原样保留 `Seq`、`TimeUnixMs`、`Data` 供后续 PR-03 `Unknown` 降级处理。
- **`turn/end.reason.kind` 映射**：
  - `completed` / `max-tokens` / `forked`：`IsFailure = false`
  - `error` / `aborted` / `blocked` / `interrupted`：`IsFailure = true`（附 `ErrorCode`/`ErrorMessage`）
  - 其余未识别枚举：`TurnEndReasonKind.Unknown`（不抛异常、保留 `RawKind`）。
- **协议代次/格式不匹配**：若握手 `$events` 首帧非 `ready` 或核心 RPC 结构缺失关键必填字段，返回 `DshFailureCode.ProtocolMismatch` / `CorruptHistory`（`session/migration-required`），拒绝静默伪造成功。

---

## 4. 真机环境验证（2026-11-23，M2007J1SC / Android 17）

- **必须使用 `0.2.2-rc.5` 层**：设备上原装的 `0.2.2-rc.3` 层会出现未登记事件与消息段；升级到 `0.2.2-rc.5` 后 `turn/end.reason` 能给出 `TRANSPORT` / `MISSING_CREDENTIAL` 等真实原因。针对初次真机验收暴露的 `F3`、`F5`/`F11`、`F9`、`F12`、`F13` 等 `0.2.2-rc.5` 兼容缺陷，客户端已补齐上述事件表、`waterfall.request`/`cancel`、`source.kind`、`session/title` 与 `DshFailureMapper.FromSessionError`，并通过桌面单测；**2026-11-23 真机复测**：事件卡片、系统提示气泡、审批工具名/参数、会话标题均已按预期工作（`F5`/`F11`/`F12`/`F13`/`F9` 关闭），`F3` 尚未取得端到端样本。
- **凭据 refs 必须完整**：设备 `.dsh/.credentials.yaml` 若只有 record 而缺 `refs`，DSH 报 `MISSING_CREDENTIAL`；客户端已通过 `DshFailureMapper.FromSessionError` 映射为 `session_error.credentials_missing`（单测覆盖；真机未复现该错误，尚无端到端样本）。
- **设备无默认网络**时，`session/prompt` 仍能正常入队（`turn/start`→`request/header`→`assistant/attempt`），失败由 `llm/retry` 退避重试后以 `turn/end.reason=error(TRANSPORT)` 结束——这是当前可用的"发送链路"真机证据。

详见 [ACCEPTANCE_PR00_PR04_DEVICE.md](ACCEPTANCE_PR00_PR04_DEVICE.md)。
