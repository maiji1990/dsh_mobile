# 路线 B PoC 报告：无浏览器原生对接裁剪 `web` / Remote API（N-00-03）

- **测试日期**：2026-11-22
- **验证版本**：`@deepseek-ai/dsh@0.2.2-rc.5`（对应 [runtime-build.lock](../../runtime-bundle/runtime-build.lock) 锁定版本），宿主 Node.js `v22.23.3`（x86_64 Linux 桌面 PoC 环境；Android ARM64 真机待另行采集）
- **参照协议源码**：`@deepseek-ai/dsh-web-app`、`@deepseek-ai/dsh-client-connection`、`@deepseek-ai/dsh-api-gateway`、`@deepseek-ai/dsh-api-session-controller`、`@deepseek-ai/dsh-api-settings-controller`、`@deepseek-ai/dsh-host-plugin-inventory`

---

## 1. 验证点 1：裁剪 `--patch` 与启动耗时 / 内存对照

通过 `--patch` 禁用浏览器前端插件表项（59 个纯浏览器 `ui-*`、`modules`、`open-in-app`、`directory-picker`、`client-hmr`、`desktop-product-telemetry`、`product-analytics`、`harness-dev-context`、`entry-customization`），保留全部后端控制器（`connection`、`session-controller`、`settings-controller`、`plugin-inventory`、`workspace-controller`、`mcp-manager`、`schedule`、`web-runtime`）。

> **CLI 参数顺序发现**：`--patch <path>` 是 `dsh` 根命令选项，**必须放在 `--no-open` / `--port` 等 web 应用参数之前**（`dsh --profile web --patch <file> --no-open --port <port>`）；若置于 `--port` 之后会被 `dsh-web-app/startup` 报 `error: unknown option '--patch'`。

桌面 Node `v22.23.3` 连续 3 次启动实测（`dsh.spawn` → stdout 输出 launch token URL）：

| 变体 | 3 次耗时（ms） | P50（ms） | 进程 `VmRSS`（KiB） |
|---|---:|---:|---:|
| 基线 `web --no-open` | `4531, 4526, 4535` | **4531** | `378200 – 393564`（~370–384 MiB） |
| 裁剪 `web --patch trim-web.yml --no-open` | `4262, 4317, 4282` | **4282（−249 ms）** | `363864 – 370628`（~355–362 MiB） |
| 零凭据裁剪 `web --patch`（空 `DSH_HOME`） | `4419` | `4419` | — |

---

## 2. 验证点 2：`/connect`、`/api/remote.mux` 与核心 Remote RPC 消费验证

### 2.1 认证与生命周期关键发现

1. **`GET /connect?token=<launchToken>&interactions=answer`** 返回 `302` 并通过 `Set-Cookie` 签发 `v2` HMAC 签名 Cookie（`dsh-auth-<sha256(authority)>=v2.<payload>.<sig>`），重定向至 `./api/remote.mux`。
2. **重要发现：`v2` Cookie 绑定 WebSocket Peer 生命周期**。核验 `dsh-client-connection/lib/index.js`（`admitConnection`）与实测：一旦 `/api/remote.mux` WebSocket 关闭，`HostConnectionService` 立即释放并销毁对应 `Peer`，随后同一 `v2` Cookie 访问 `/api/*` 会返回 `401 unauthorized`。因此原生 `DshRemoteConnection` 必须**保持 `/api/remote.mux` WebSocket 在连接代次内常驻打开**，断线重连时重新通过 `/connect?token=...` 交换新 `v2` Cookie 并递增 generation。
3. **`$events` 就绪栅栏**：向 `/api/remote.mux` 发送 `{"type":"open","streamId":"ev-1","endpoint":"$events","payload":{"args":{}}}`，立即收到首帧：
   ```json
   {"type":"item","streamId":"ev-1","value":{"type":"ready","clientId":"...","host":{"home":"..."}}}
   ```

### 2.2 实测通过的 Remote API 能力矩阵（`0.2.2-rc.5`）

| 能力域 | 实测端点 | 实测结果与关键发现 |
|---|---|---|
| 会话列表 / 创建 / 状态 | `session/list`、`session/create`、`session/state` | 全部 `200 ok: true`；返回 `sessionId`、`agentPreset`、`cursor`、`running`、`pendingInput` |
| 历史冷读与实时跟随 | `session/events`（参数 `{ request: { address: { kind: "session", sessionId } } }`）、`session/follow`（WS 流） | `session/events` 返回 `eventsCount: 3`；`session/follow` 首帧返回 `snapshot`（含 `cursor: 2`），随后实时推送 `event` 帧 |
| Prompt 幂等与时区 | `session/prompt`（含 `requestId`、`mode: "queue"`、`clientTimeZone: "Asia/Shanghai"`） | 首次返回 `{"ok":true,"value":{"accepted":true}}`；重复发送同一 `requestId` 幂等返回 `accepted: true` |
| 会话取消与模型目录 | `session/cancel`、`session/modelCatalog`、`session/selectModel` | `session/cancel` 返回 `accepted: true`；`modelCatalog` 返回完整 provider/model/reasoning efforts；`selectModel` 在无凭据时准确返回 `session/model-unavailable` |
| 凭据与设置（P0 补齐） | `credentials/describe`、`credentials/set`、`credentials/unset`、`settings/describe` | 使用 `ref: "deepseek_api_key"` 实测写入成功，`describe` 返回 `{"configured":true,"source":"file","writable":true,"masked":"••••••••••••••••••"}`（天然脱敏不回显明文），`unset` 清除成功 |
| 插件清单与转发事件 | `pluginInventory/list`、`$events` allowlist（`@deepseek-ai/dsh-api-remotes`） | `pluginInventory/list` 返回 `203` 个条目；`$events` 原生包含 `approval/request`（waterfall）与 `user-questions/request`（waterfall）并经 `$events/result` 回包 |

---

## 3. 路线 B 结论

路线 B 在 `0.2.2-rc.5` 下无需自研同进程 Bridge 即可原生提供**全部聊天、历史冷读分页、`requestId` 幂等、`clientTimeZone`、图片附件、模型切换、凭据写入、插件清单及服务端事件流**，同时直接复用现有 `DshHost`（`127.0.0.1` + launch token）与 `RemoteSshSession`（ADR-0012 / ADR-0013）。详见正式决策 [ADR-0033](../ADR/0033-native-agent-client.md)。
