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

- **已知 `SessionWireEvent.Type`**：`turn/start`、`turn/end`、`step/start`、`step/end`、`user/message`、`developer/message`、`system/message`、`assistant/message`、`assistant/attempt`、`tool/call`、`tool/result`、`request/header`、`approval/policy`、`approval/asked`、`approval/decided`、`permission/preset`、`sandbox/mode`、`command/run`、`command/done`。
- **未知 `SessionWireEvent.Type`**：映射为 `SessionWireEventView(IsKnownType: false)`，原样保留 `Seq`、`TimeUnixMs`、`Data` 供后续 PR-03 `Unknown` 降级处理。
- **`turn/end.reason.kind` 映射**：
  - `completed` / `max-tokens` / `forked`：`IsFailure = false`
  - `error` / `aborted` / `blocked` / `interrupted`：`IsFailure = true`（附 `ErrorCode`/`ErrorMessage`）
  - 其余未识别枚举：`TurnEndReasonKind.Unknown`（不抛异常、保留 `RawKind`）。
- **协议代次/格式不匹配**：若握手 `$events` 首帧非 `ready` 或核心 RPC 结构缺失关键必填字段，返回 `DshFailureCode.ProtocolMismatch` / `CorruptHistory`（`session/migration-required`），拒绝静默伪造成功。
