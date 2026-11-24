# 架构概览

权威设计见 [plan/DSH_Mobile_NET11_完整架构与实施方案.md](plan/DSH_Mobile_NET11_完整架构与实施方案.md)。本文记录**当前代码实际落地**的部分。

> 长期路线为 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)（C#-first）。PR-10（[ADR-0018](ADR/0018-remove-dshbox.md)）删除了过渡期的
> DSHBox 子模块、Bridge AAR、`DshNativeFacade` / Kotlin 旧桥与全部影子比对；过渡期结构见本文件在 `128c2ea` 及之前的版本。

## 分层

```text
DshMobile.Android (net11.0-android)   Activities / Fragments（Material 3 三栏：会话 | 工作区 | 更多）、BridgeHost（纯 C# 组合根）、
        │                             AndroidForegroundService、AndroidBundledAssets、SystemGovernanceMonitor、
        │                             C# Relay 组合根（CsRelay）、原生聊天 / 凭据 / 插件 / 工作区 Git / 远程主机界面
        │ Microsoft.Extensions.DependencyInjection（AddDshMobileApplication）
DshMobile.Agent (net11.0)             纯托管原生客户端层（零 Android 引用，ADR-0033）：协议 STJ 源生上下文、RemoteDshGateway、
        │                             TranscriptEngine / Cache-First（KeptTranscriptStore）、RequestsEngine、
        │                             Markdig 渲染缓存、Sessions / Chat / Credentials / Plugins / Inbox ViewModels
DshMobile.Application (net11.0)       RuntimeCoordinator：状态机 + journal、DshHost 启停、导入 / 准备 / 回滚 / 更新探测、
        │                             WorkLeaseManager / IdleStopPolicy / HeatTierPolicy / WarmupPolicy、HomeStatus、PtyProbe
DshMobile.Runtime (net11.0)           RuntimeState / FileRuntimeJournal、RuntimeStatusReader、Plugins（L0/L1 受控命令）、
        │                             Bundle/* · Layers/* · Guest/* · Hosting/*（DshHost、DshTokenRevisionTracker、ProbeHealthAsync）、
        │                             Supervision/*（NativeOrphanReaper、PhantomProcessMonitor）、Automation/*（Relay、ui_*）、
        │                             Remote/*（SSH.NET、主机密钥固定、IDshTokenSource 回环接入）
DshMobile.Native (net11.0)            NativeExecutablePolicy（W^X）、NativePty / Posix（libdshmobile_native P/Invoke）
DshMobile.Core (net11.0)              Contracts（ProbeResult、RemoteHost* 等）、StartupTrace、WorkspaceFileSystem / WorkspaceProvider V2
```

| 项目 | 测试 |
|---|---|
| `DshMobile.Core` | `tests/DshMobile.Core.Tests`：工作区文件系统与 Provider V2（回收站、冲突检测、归档安全、Git 状态） |
| `DshMobile.Runtime` / `DshMobile.Native` | `tests/DshMobile.Runtime.Tests`：共享向量、钉值测试（`UpstreamPinnedValuesTests` 等）、`DshTokenRevisionTracker`、状态机/journal、层包、孤儿回收/32 预算、Relay、远程 SSH |
| `DshMobile.Application` | `tests/DshMobile.Application.Tests`：协调器、在线更新与探针回滚、`WorkLeaseManager`/`IdleStopPolicy`/`HeatTierPolicy`、`HomeStatus`、PTY 自检 |
| `DshMobile.Agent` | `tests/DshMobile.Agent.Tests`：协议与传输、Gateway 契约、Transcript/Requests 引擎、`Stream6000` 性能门禁、CI 架构安全门禁（含 `NoBusinessWebViewGateTests`） |
| guest 脚本 | `tests/guest`（node）：`ui-facade.mjs`、`dsh-e2e.mjs`、`startup-fs-cache-shim.mjs` 回归 |

## 三类边界

1. **C# 控制面、原生客户端与系统集成**（`src/`）：Material 3 原生会话/文件/工作区/更多界面、系统治理、Relay（无障碍 / Direct / Shizuku 后端、审批）、远程 SSH；**全仓零业务 WebView**（外链统一安全确认后交由系统浏览器）。
2. **最小原生层**（`native/`，NDK 27）：`libdshmobile_native.so`（PTY、进程组）与自建 PRoot / loader / talloc / shmem（ADR-0014）；
   Shizuku 用户服务为最小 Java 类（ADR-0009）。
3. **Node Harness 执行面**（Debian/PRoot 内的官方 DSH）：原样运行，通过受控 `--patch` 与 `ui_*` 使用手机能力，原生侧通过 `/api` + `/api/remote.mux` 与之交互。

## 运行时

```text
MainActivity ─▶ BridgeHost ─▶ RuntimeCoordinator ─▶ DshHost ─▶ exec nativeLibraryDir/libproot.so
     │                                                   ▼
     └─ AndroidForegroundService (specialUse)   PRoot → Debian 13 → Node 24 → DSH (web, 127.0.0.1:3080)
```

- APK 携带 `assets/runtime/android-side.tar.zst`；base / node 由运行环境包导入（`LayerInstaller`），DSH 层不进 APK（ADR-0027：设备端导入 `runtime.import.dsh` 或在线更新）。
- APK 还携带原样复制自 DSHBox v1.5.1 的 guest 资产（链接垫片、corepack 缓存），由 `GuestAssetProvisioner` 在每次 guest 使用前确保到位。
- `extractNativeLibs=true`：PRoot 以可执行文件方式从 nativeLibraryDir 启动（W^X）。

## 原生传输与协议入口（[ADR-0033](ADR/0033-native-agent-client.md)）

- PR-09 起 `MainActivity` 默认入口为原生 **会话 | 工作区 | 更多** 三栏，`DshWebActivity` 与 `CSharpWebUiController` 等业务 WebView 栈已彻底删除（由 `NoBusinessWebViewGateTests` 门禁永久锁定）。
- 原生聊天经 `DshRemoteConnection`（`/connect` Cookie 换取 + `/api/remote.mux` WebSocket 多路复用）与 `IDshTokenSource`（`DshHost` / `RemoteSshSession` 内存持有 launch token、`DshTokenRevisionTracker` 跟踪代次，不落盘、不写日志）直接通信。
- 聊天中点击外部 `http(s)` 链接经安全确认框后交由受控系统浏览器（`Intent.ActionView` + `CategoryBrowsable`）打开。

## 线程与生命周期

- 协调器的运行时操作由一把 `SemaphoreSlim` 串行；后台结果回到 UI 线程更新界面，UI 线程不等待运行时操作。
- C# 宿主拥有进程以实际存活为准（含健康循环放弃、停止残留）；停止失败保留前台服务以免孤儿进程。

## 禁止的架构

见方案 3.1 节：C# 自建 Agent Loop、直读 DSH 私有存储、把 PRoot 视为安全隔离、双 AccessibilityService、默认授予 ADB/Root。
