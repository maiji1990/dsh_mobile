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
| `dsh.probe` | `{"profile": "web\|headless\|sdk\|sdk-minimal\|acp\|plugin"}`（白名单） | `ProbeResult{profile, status: pass\|limited\|fail, needsCredential, shim, checks[{name, ok, ms, detail}]}`；guest 内固定脚本，做真实协议握手，不调用模型 |

`runtime.* / sandbox.* / dsh.*` 在 IO 协程上异步执行（不阻塞控制线程），彼此串行；deadline 内未完成返回 `DEADLINE_EXCEEDED`（底层操作可能仍在收尾）。DSH launch token **不经过**控制面 JSON，只暴露 `hasLaunchToken`。

事件 `runtime.state`：`{sandboxState, dshState, dshVersion}`，状态变化时推送。

后续方法（`terminal.*` 等）在对应 TASK 中追加到此表，**只增不改**；破坏性变更需要 `protocol: 2`。

### 1.7 非目标

`execute` 不承载 PTY 输出、截图、大文件。高吞吐数据走 `ParcelFileDescriptor` / 原生 stream / 原生 View。

## 2. `ui_*` 统一自动化契约

沿用 `maiji1990/ui_mcp`，TASK-010 引入时在此登记 DTO 版本与 `deviceId` 约定（`android-local` 等）。
