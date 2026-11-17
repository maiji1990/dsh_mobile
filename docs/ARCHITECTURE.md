# 架构概览

权威设计见 [plan/DSH_Mobile_NET11_完整架构与实施方案.md](plan/DSH_Mobile_NET11_完整架构与实施方案.md)。本文记录**当前代码实际落地**的部分。

> 长期路线为 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)（C#-first）。PR-10（[ADR-0018](ADR/0018-remove-dshbox.md)）删除了过渡期的
> DSHBox 子模块、Bridge AAR、`DshNativeFacade` / Kotlin 旧桥与全部影子比对；过渡期结构见本文件在 `128c2ea` 及之前的版本。

## 分层

```text
DshMobile.Android (net11.0-android)   Activities（Material 3）、BridgeHost（类名沿用，纯 C# 组合根）、AndroidForegroundService、
        │                             AndroidBundledAssets、C# Relay 组合根（CsRelay）、C# WebView / 文件 / 远程主机界面
        │ Microsoft.Extensions.DependencyInjection（AddDshMobileApplication）
DshMobile.Application (net11.0)       RuntimeCoordinator：状态机 + journal、DshHost 启停、导入（装后树核对）、准备、回滚、诊断、
        │                             探测、Relay 自检、PtyProbe
DshMobile.Runtime (net11.0)           RuntimeState / RuntimeStateMachine / FileRuntimeJournal、RuntimeStatusReader
        │                             Bundle/* · Layers/*（校验 / 解包 / 安装 / DSH 层 / 回滚）、Guest/*（PRoot argv + 逐 token 引号、
        │                             guest 资产）、Hosting/*（DshHost、token、健康循环）、Supervision/*、Automation/*（Relay、ui_*）、
        │                             Remote/*（SSH.NET、主机密钥固定）
DshMobile.Native (net11.0)            NativeExecutablePolicy（W^X）、NativePty / Posix（libdshmobile_native P/Invoke）
DshMobile.Core (net11.0)              Contracts（ProbeResult、RemoteHost* 等）、WebUi 策略、WorkspaceFileSystem
```

| 项目 | 测试 |
|---|---|
| `DshMobile.Core` | `tests/DshMobile.Core.Tests`：WebView 策略向量、工作区文件系统 |
| `DshMobile.Runtime` / `DshMobile.Native` | `tests/DshMobile.Runtime.Tests`：共享向量（期望值为 PR-10 前新旧双绿结果）、钉值测试（`UpstreamPinnedValuesTests` 等）、状态机 / journal、层包（含真实锁定包）、Relay、远程 SSH |
| `DshMobile.Application` | `tests/DshMobile.Application.Tests`：协调器（假宿主 / 真宿主前置条件）、PTY 自检 |
| guest 脚本 | `tests/guest`（node）：`ui-facade.mjs` 对假 Relay CLI 回放 |

## 三类边界

1. **C# 控制面与系统集成**（`src/`）：Activity、状态、权限 UI、Relay（无障碍 / Direct / Shizuku 后端、审批）、WebView、文件、远程 SSH。
2. **最小原生层**（`native/`，NDK 27）：`libdshmobile_native.so`（PTY、进程组）与自建 PRoot / loader / talloc / shmem（ADR-0014）；
   Shizuku 用户服务为最小 Java 类（ADR-0009）。
3. **Node Harness 执行面**（Debian/PRoot 内的官方 DSH）：原样运行，经 `ui_*`（Cordis patch 层 + guest 门面脚本）使用手机能力。

## 运行时

```text
MainActivity ─▶ BridgeHost ─▶ RuntimeCoordinator ─▶ DshHost ─▶ exec nativeLibraryDir/libproot.so
     │                                                   ▼
     └─ AndroidForegroundService (specialUse)   PRoot → Debian 13 → Node 24 → DSH (web, 127.0.0.1:3080)
```

- APK 携带 `assets/runtime/android-side.tar.zst`；base / node 由运行环境包导入（`LayerInstaller`），DSH 层不进 APK（ADR-0027：设备端导入 `runtime.import.dsh` 或在线更新）。
- APK 还携带原样复制自 DSHBox v1.5.1 的 guest 资产（链接垫片、corepack 缓存），由 `GuestAssetProvisioner` 在每次 guest 使用前确保到位。
- `extractNativeLibs=true`：PRoot 以可执行文件方式从 nativeLibraryDir 启动（W^X）。

## WebUI

```text
DshWebActivity (C#) ── CSharpWebUiController ── DshHost.LaunchToken
      · DshTokenRevisionTracker（token 变化即重载）
      · DshOriginPolicy（仅 127.0.0.1:3080，自写 java.net.URI 兼容解析）
      · loadUrl("/?token=…") → DSH 换签名 Cookie；401 每修订恢复一次；外链 → 系统浏览器
      · 状态条只显示状态与路径，不含 token
```

## 线程与生命周期

- 协调器的运行时操作由一把 `SemaphoreSlim` 串行；后台结果回到 UI 线程更新界面，UI 线程不等待运行时操作。
- C# 宿主拥有进程以实际存活为准（含健康循环放弃、停止残留）；停止失败保留前台服务以免孤儿进程。

## 禁止的架构

见方案 3.1 节：C# 自建 Agent Loop、直读 DSH 私有存储、把 PRoot 视为安全隔离、双 AccessibilityService、默认授予 ADB/Root。
