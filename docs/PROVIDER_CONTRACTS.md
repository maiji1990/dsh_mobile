# 跨语言契约（Provider Contracts）

## 1. Native Bridge 控制面协议 v1

实现：Java `dshmobile.nativebridge.DshNativeFacade`（`native/native-bridge`）↔ C# `DshMobile.Core.Bridge`（DTO / 序列化）+ `DshMobile.Android` 中的 `NativeBridgeClient`（JNI 回调适配）。

### 1.1 Java 公共 API（稳定面）

```java
public final class DshNativeFacade {
    public static final int PROTOCOL_VERSION = 1;
    public static String getBridgeVersion();
    public DshNativeFacade();
    public void initialize(android.content.Context context);   // 幂等
    public void execute(String requestJson, NativeCallback callback); // 永不在调用线程回调
    public void subscribe(NativeCallback callback);
    public void unsubscribe(NativeCallback callback);
    public int getInFlightCount();
    public void close();                                         // 幂等；之后所有 execute 返回 CLOSED
    // TASK-006（主线程）：托管官方 DSH WebUI；launch token 只在原生侧处理，绝不返回调用方
    public void attachWebUi(android.webkit.WebView webView, WebUiListener listener);
    public void detachWebUi();
    public void reloadWebUi();
    // TASK-007（主线程）：回答 WebUiListener.onFileChooserRequested；null/空 = 取消，多余或迟到的回答被忽略
    public void completeWebFileChooser(android.net.Uri[] uris);
    // TASK-008（主线程）：终端。PTY 数据不经 execute()；会话在进程级存活，页面只挂/卸 View
    public android.view.View createTerminalView(android.content.Context activity, TerminalListener listener); // 无运行环境返回 null
    public void releaseTerminalView();
    public String newTerminalSession();                 // 返回会话 id；无运行环境返回 null
    public void activateTerminalSession(String id);
    public void closeTerminalSession(String id);
    public String terminalSessionsJson();               // [{id,title,running,exited,exitCode,active}]
    public boolean terminalSendKey(String name);        // ESC TAB UP DOWN LEFT RIGHT HOME END PGUP PGDN CTRL_C
    public void setTerminalCtrl(boolean on);            // 一次性 Ctrl 锁存
    public void terminalPaste();
}
public interface NativeCallback { void onEvent(String json); }
public interface WebUiListener {
    void onWebUiState(String state, String detail);                       // detail 不含 token / query
    void onFileChooserRequested(String[] acceptTypes, boolean allowMultiple); // 宿主弹系统选择器并必须回答一次
}
public interface TerminalListener { void onTerminalSessionsChanged(String json); } // 不含终端内容
```

### 1.2 请求

```json
{"protocol":1,"requestId":"<uuid>","method":"bridge.info","deadlineMs":30000,"payload":{}}
```

- `requestId`：调用方生成，**在途唯一**；在途重复返回 `DUPLICATE_REQUEST`（不会执行两次）。
- `deadlineMs`：从 Native 收到请求起算；排队超过期限则不执行、返回 `DEADLINE_EXCEEDED`。`<=0` 视为 `BAD_REQUEST`。
- 所有请求在**单写者**串行执行器上执行，按到达顺序。

### 1.3 响应（经 `execute` 的 callback 恰好回调一次）

```json
{"protocol":1,"requestId":"<uuid>","ok":true,"code":"OK","message":null,"payload":{...}}
```

C# `BridgeClient` 在 `deadlineMs + 2s` 内未收到响应时，本地合成 `DEADLINE_EXCEEDED`；迟到的响应计入 `OrphanResponses` 并丢弃。`ok` 与 `code == "OK"` 不一致的消息视为协议违规，计入 `MalformedMessages`，永不当作成功。

### 1.4 事件（经 `subscribe` 的 callback）

```json
{"protocol":1,"type":"event","event":"bridge.lifecycle","payload":{"state":"INITIALIZED"}}
```

### 1.5 稳定错误码

| code | 含义 | 调用方处理 |
|---|---|---|
| `OK` | 成功 | — |
| `BAD_REQUEST` | JSON 无法解析 / 缺字段 / 参数非法 | 修代码，不重试 |
| `PROTOCOL_MISMATCH` | `protocol` 不是 1 | 升级 Bridge |
| `METHOD_NOT_FOUND` | 未知 method | 能力未实现，UI 显示“不可用” |
| `NOT_INITIALIZED` | 未调用 `initialize` | 先初始化 |
| `DUPLICATE_REQUEST` | 在途 requestId 重复 | 生成新 id |
| `DEADLINE_EXCEEDED` | 超过 deadline | 可重试 |
| `CANCELLED` | 预留给 Native 侧取消长任务（v1 尚无此类方法）。C# 侧调用方取消时 `ExecuteAsync` 抛 `OperationCanceledException` | — |
| `CLOSED` | Facade 已关闭 | 重建 Facade |
| `UNAVAILABLE` | 平台/设备限制导致能力不可用（例如无 Context 时调用 runtime.*） | 显示限制原因，不得伪装成功 |
| `RUNTIME_ERROR` | DSHBox `sandbox-manager` 返回失败；`payload` 带 `upstreamCode`、`recoverable` | 按 upstreamCode 显示与恢复 |
| `INTERNAL` | 未预期异常 | 记录日志 / 上报 |

### 1.6 v1 方法

| method | payload | 返回 payload |
|---|---|---|
| `bridge.info` | `{}` | `bridgeVersion`, `protocol`, `upstream`, `kotlinVersion`, `sdkInt`, `abis[]`, `deviceModel` |
| `bridge.ping` | 任意对象 | `{"echo": <原 payload>, "seq": <long>}` |
| `dshbox.versions.compare` | `{"a":"0.1.1","b":"0.1.1-rc.2"}` | `{"result": -1|0|1}`；由 DSHBox `common` 的 Kotlin `Versions.compare` 计算（验证 C# → Java → DSHBox Kotlin → C#） |
| `runtime.status` | `{}` | `RuntimeStatus`：`sandboxState`、`dshState`、`dshVersion`、`dshUnresponsive`、`runtimeInstalled`、`nodeLayerInstalled`、`hasLaunchToken`、`linkShim`、`endpoint`、`freeBytes` |
| `runtime.prepare` | `{}` | `RuntimeStatus` + `steps[]`：安装 APK 内置层；base/node 在位时装配内置 DSH 层 |
| `runtime.importBundle` | `{"path": "<App 自有存储内的 zip>"}` | `RuntimeStatus` + `dshProvision`；路径必须规范化后位于 filesDir / cacheDir / App 专属外部目录 |
| `runtime.health` | `{}` | `dshProcessRunning`、`portOpen`、`webUiReady`、`lastError` |
| `runtime.diagnostics` | `{}` | `{arch, debian, node, dsh}` → `{ok, output, error}`；**固定命令**，不接受调用方命令 |
| `sandbox.start` / `sandbox.stop` | `{}` | `RuntimeStatus` |
| `dsh.start` | `{}` | `RuntimeStatus` + `dsh{version, baseUrl, ready}` |
| `dsh.stop` | `{}` | `RuntimeStatus` |
| `plugin.list` | `{"profile":"web"}` | `PluginCommandResult{ok, exitCode, output, error}`；output 为 `pnpm list --json --depth 0` |
| `plugin.add` | `{"profile","spec"}`（npm registry spec，白名单校验） | `PluginCommandResult`；需网络；DSH 重启后生效 |
| `plugin.remove` | `{"profile","name"}`（包名，白名单校验） | `PluginCommandResult` |
| `relay.start` | `{}` | `RelayStatus{phase, channelRunning, degradedReason, accessibilityEnabled, allowed[], guestCli, uiTools}`；启动 Relay 并注册 `ui_*`（TASK-010） |
| `relay.stop` | `{}` | `RelayStatus`；停止 Relay 并注销 `ui_*`（运行中的 DSH 保留到重启） |
| `relay.status` | `{}` | `RelayStatus`；`uiTools{registered, loadedByDsh, dshRestartNeeded, conflict, serverName}` |
| `relay.selftest` | `{}` | `PluginCommandResult`；guest 固定命令：`relay doctor` + `ui-facade-check.mjs`（经 MCP stdio 跑 `ui_list_devices` / `ui_snapshot` / `ui_get_element`）；exit 0 通过，5 待审批，4 拒绝或不可用，3 过期 |
| `dsh.probe` | `{"profile": "web\|headless\|sdk\|sdk-minimal\|acp\|plugin"}`（白名单） | `ProbeResult{profile, status: pass\|limited\|fail, needsCredential, shim, checks[{name, ok, ms, detail}]}`；guest 内固定脚本，做真实协议握手，不调用模型 |

`runtime.* / sandbox.* / dsh.*` 在 IO 协程上异步执行（不阻塞控制线程），彼此串行；deadline 内未完成返回 `DEADLINE_EXCEEDED`（底层操作可能仍在收尾）。DSH launch token **不经过**控制面 JSON，只暴露 `hasLaunchToken`。

事件 `runtime.state`：`{sandboxState, dshState, dshVersion}`，状态变化时推送。

后续方法（`terminal.*` 等）在对应 TASK 中追加到此表，**只增不改**；破坏性变更需要 `protocol: 2`。

### 1.7 非目标

`execute` 不承载 PTY 输出、截图、大文件。高吞吐数据走 `ParcelFileDescriptor` / 原生 stream / 原生 View。

## 2. `ui_*` 统一自动化契约

沿用 `maiji1990/ui_mcp`（契约参考 `e098f91`）。实现：guest MCP stdio 服务
`native/native-bridge/src/main/guest-assets/dshmobile/ui-facade.mjs`（serverInfo `dshmobile-ui 0.1.0`），
DSH 中 `serverName: ui`，模型侧工具名 `mcp__ui__<tool>`。设计与取舍见 ADR-0004。

### 2.1 约定

- `deviceId` 固定 `android-local`（省略即此设备；其它值 → `TARGET_NOT_FOUND`）；`provider` 为 `android-relay`；`transport` 为 `local-a11y`。
- V1 只读：只有下表 4 个工具；写工具（`ui_click` / `ui_set_text` / `ui_scroll` / `ui_press_key` …）不列出，调用返回 `ACTION_UNSUPPORTED`。
- 底层：Relay guest CLI `/opt/pilot/bin/relay call ui.snapshot|ui.node`；每次调用需用户在通知栏审批。
- 结果：`content[0].text` 为 JSON，成功时同时给 `structuredContent`；失败为 `isError: true`，正文 `{"error":{code, message, hint, relay?{code, exitCode, retryable}}}`。

### 2.2 工具

| 工具 | 参数 | 返回 |
|---|---|---|
| `ui_list_devices` | — | `{devices:[{deviceId, platform:"android", transport, provider, status:"online"\|"offline", readOnly:true, detail}]}`；只跑 `relay doctor`，不需审批 |
| `ui_snapshot` | `deviceId?`, `mode?: compact\|full`（默认 compact，去掉无文本且不可交互的布局节点）, `maxNodes?: 1..600`（默认 200） | `{snapshotId:"sN", sessionId, target{deviceId, applicationId, windowId:null}, provider, revision, capturedAt, truncated, screen{width,height,densityDpi,display}, totalNodes, elements[]}` |
| `ui_find` | `snapshotId?`（须为最新）, `role?`, `name?`, `text?`, `clickable?`, `exact?`, `limit?: 1..200` | `{snapshotId, count, matches[]}`；只在内存中过滤，不调 Relay |
| `ui_get_element` | `snapshotId`, `refId` | `{snapshotId, element}`；经 `ui.node` 实时回读，补 `nativeId` / `enabled` / 原生动作 |

元素：`{ref:"e<nodeId>", provider, role, name(desc ?? text), text, nativeId, bounds{x,y,width,height}|null, visible, actions[], extra{class, nodeId, …}}`。
快照不含 `enabled`（Relay 快照无此字段，不编造），由 `ui_get_element` 给出。`role` ∈ button / textbox / text / image / checkbox / switch / radio / list / slider / document / group / unknown。

### 2.3 ref 与过期

- 只有最新快照的 ref 有效；旧 `snapshotId` → `STALE_ELEMENT`，**不发底层请求**（单测断言调用次数为 0）。
- 最新快照的 ref 回读时比对类名、标签（desc/text）与位置（容差 24 px）；不符、节点消失或 Relay 报 `treeWarning` → `STALE_ELEMENT`。
- Relay 调用串行（单写者）。

### 2.4 错误映射（保守）

| Relay | ui_* |
|---|---|
| `E_AWAITING_CONSENT`、`E_GATE_WAITING_TURN`（门面先重试最长 90 s） | `APPROVAL_REQUIRED` |
| `E_GATE_HOST_DENIED`、`E_GATE_APPROVAL_TIMEOUT`、`E_GATE_CANCELLED` | `POLICY_DENIED`（不重试） |
| `E_GATE_SYSTEM_MISSING`、`E_GATE_NO_FOREGROUND`、`E_CAPABILITY_UNAVAILABLE_ON_DEVICE`、`E_BACKEND_UNAVAILABLE`、`E_SURFACE_UNAVAILABLE`、`E_TASK_SUSPENDED_BY_HOST`、`E_QUEUE_FULL`、`E_RATE_LIMITED`；CLI 不存在 | `PROVIDER_UNAVAILABLE` |
| `E_SURFACE_SECURE_WINDOW` | `SECURE_CONTENT` |
| `E_NODE_NOT_FOUND` | `ELEMENT_NOT_FOUND`（`ui_get_element` 中为 `STALE_ELEMENT`） |
| `E_NODE_AMBIGUOUS` | `AMBIGUOUS_ELEMENT` |
| `E_TRANSPORT_TIMEOUT`、`E_WAIT_TIMEOUT` | `OPERATION_TIMEOUT` |
| 其它 / 无 JSON 回包 | `OBSERVATION_INCOMPLETE`（附原码） |

### 2.5 注册

开启中继时写入宿主 `--patch` 层（`dsh-mcp-client`，stdio，`command: !!js process.execPath`，
`args: ['/opt/dshbox-cache/dshmobile/ui-facade.mjs']`，`toolCallTimeoutMs: 120000`）；DSH 启动时加载。见 ADR-0004 第 7 条。
