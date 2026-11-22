# 路线 A PoC 报告：`dsh --profile sdk`（stdio JSON-RPC）+ 同进程 Cordis Bridge（N-00-02）

- **测试日期**：2026-11-22
- **验证版本**：`@deepseek-ai/dsh@0.2.2-rc.5`（对应 [runtime-build.lock](../../runtime-bundle/runtime-build.lock) 锁定版本），宿主 Node.js `v22.23.3`（x86_64 Linux 桌面 PoC 环境；Android ARM64 真机待另行采集）
- **参照协议源码**：`@deepseek-ai/dsh-sdk-protocol@0.2.2-rc.5`、`@deepseek-ai/dsh-sdk-jsonrpc-server@0.2.2-rc.5`、`@deepseek-ai/dsh-sdk-app@0.2.2-rc.5`

---

## 1. 验证项目与实测结果

### 1.1 验证点 1：同进程 Cordis Bridge 会话所有权冲突

**测试方法**：启动 `dsh --profile sdk --patch <bridge-probe.yml>`，通过 stdio JSON-RPC 发送 `session/working-directory/get` 创建会话 `poc-collide`（由 `HarnessSdkJsonRpcServer` 持有），再由挂载的 Cordis 测试插件 `poc-bridge-probe2` 调用 `ctx.agents.resume({ resumeSessionId: session.id, ... })` 模拟 Bridge 接管/切换模型/恢复会话。

**实测输出**：

```text
[probe] session/created id=poc-collide
[probe] Bridge calling ctx.agents.resume(poc-collide) to switch model/resume...
[probe] Bridge ctx.agents.resume threw: session "poc-collide" is already owned by an active write handle
```

**源码核验结论（`dsh-sdk-jsonrpc-server/lib/index.js`）**：
1. `HarnessSdkJsonRpcServer` 私有维护 `sessions = new Map()` 与 `assertLiveAgent(rec, sessionId)`（检查 `this.ctx.agents.get(rec.handle.agent.id) !== rec.handle.agent`）。
2. `ctx.agents` 对每条 `sessionId` 强制单写者句柄锁（`session "<id>" is already owned by an active write handle`）。因此**同进程自研 Bridge 插件无法在 `HarnessSdkJsonRpcServer` 持有会话期间安全调用 `ctx.agents.resume` 或替换 Agent 句柄**；反向由 Bridge 先打开会话再由 `sdk` 调用 `session/resume` 同样被拒绝。
3. 实测 `dsh-sdk-jsonrpc-server` `handleRequest()` 仅暴露 8 个方法：`initialize`、`session/prompt`、`session/query`、`session/resume`、`session/wait`、`session/working-directory/get`、`session/working-directory/set`、`shutdown`。缺失能力包括：`session/list`、历史分页（`session/events`）、`session/cancel`、模型目录与切换（`session/modelCatalog` / `session/selectModel`）、`credentials/*`、`settings/*`、`pluginInventory/list` 及服务端 `requestId` 持久化幂等键。

### 1.2 验证点 2：零凭据首次启动 `initialize` 与 `timeZone` 实测

**测试方法**：清空 `DSH_HOME`、不设 `DEEPSEEK_API_KEY`，启动 `dsh --profile sdk` 并发送 `initialize`（`provider: "deepseek-official"`, `model: "deepseek-chat"`）。随后测试向 `session/query` 传入 `timeZone: "Asia/Shanghai"`。

**实测输出**：
- 零凭据 `initialize`：**成功**（`ms1: 3120`），返回 `{"serverInfo":{"name":"deepseek-harness-sdk-runtime","version":"0.0.1"}}`。说明无凭据时 `sdk` 可完成握手，但在真机未配置 API Key 时发起对话会失败，且 `sdk` 无官方 `credentials/*` RPC，仍需额外通过 CLI 或挂载 `settings-controller` 写入凭据。
- `session/query` 透传 `timeZone`：**在 `0.2.2-rc.5` 中被严格拒绝**，返回 JSON-RPC 错误：
  ```json
  {"code": -32603, "message": "session/query requires only a non-empty sessionId and contentBlocks"}
  ```
  核验 `dsh-sdk-protocol@0.2.2-rc.5/lib/types/types.d.ts`：`InitializeParams`、`SessionPromptParams`、`SessionQueryParams`、`SessionResumeParams` 均未包含 `timeZone` 或 `permissionMode` 字段（该特性存在于更新的上游分支，尚未进入 `0.2.2-rc.5`）。

---

## 2. 路线 A 风险与结论

| 评估维度 | 路线 A 实测结论 |
|---|---|
| 会话所有权 | **存在单写者写句柄冲突**（`already owned by an active write handle`），自研 Bridge 无法与 `HarnessSdkJsonRpcServer` 并发管理同一会话 |
| 功能完整性 | 缺失会话列表、冷历史分页、取消、模型目录/切换、凭据管理、插件清单与 `requestId` 幂等，全部需自研非标准扩展 |
| 协议特性（`0.2.2-rc.5`） | 不支持 `timeZone` 与 `session/resume` `permissionMode` |
| 远程 SSH 兼容性 | 需重写 `RemoteSshSession`（由端口转发改为 SSH stdio 管线与命令白名单扩展） |

**PoC 结论**：在 `0.2.2-rc.5` 基线下，路线 A 触发既定“高风险一票否决”条件，不作为首选路线（详细裁决见 [ADR-0033](../ADR/0033-native-agent-client.md)）。
