# 架构概览

权威设计见 [plan/DSH_Mobile_NET11_完整架构与实施方案.md](plan/DSH_Mobile_NET11_完整架构与实施方案.md)。本文记录**当前代码实际落地**的部分，随里程碑更新。

> **2026-11-10 起长期路线为 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)（C#-first）**：下文 M1–M3 描述的 DSHBox 子模块 + `DshNativeFacade` 结构是过渡实现，按 [MIGRATION.md](MIGRATION.md) 逐切片替换。

## C#-first 分层（PR-01 起）

```text
DshMobile.Android (net11.0-android)   BridgeHost、Activities、AndroidForegroundService : IForegroundServiceController
        │ Microsoft.Extensions.DependencyInjection（AddDshMobileApplication）
DshMobile.Application (net11.0)       RuntimeCoordinator（监督启动/停止/导入）、MigrationOptions/SliceMode、BundleShadowCheck
        │
DshMobile.Runtime (net11.0)           IRuntimeManager / IGuestProcess、RuntimeState(8 态) + RuntimeStateMachine + FileRuntimeJournal
        │                             Versions、RuntimeConstants、RuntimeLayout、Bundle/*（从 DSHBox 移植）、Guest/*（argv + 引号）
        │                             Legacy/LegacyBridgeRuntimeManager ──▶ BridgeClient ──▶ 旧桥（过渡）
DshMobile.Native (net11.0)            NativeExecutablePolicy（W^X）、DshNativeLibrary（libdshmobile_native P/Invoke，尚未打包）
DshMobile.Core (net11.0)              协议 DTO / 错误码
```

| 项目 | 测试 |
|---|---|
| `DshMobile.Runtime` / `DshMobile.Native` | `tests/DshMobile.Runtime.Tests`：共享向量（与旧 Kotlin 同跑）、上游源码契约扫描、状态机 / journal、层包校验（含真实锁定包） |
| `DshMobile.Application` | `tests/DshMobile.Application.Tests`：协调器失败路径、影子比对判定 |

## 三类边界

1. **C# 控制面**（`src/`）：Activity、状态、设置、权限 UI、日志。
2. **Android Native 服务面**（`native/` + `third_party/dshbox` 的可复用模块）：以 AAR 提供窄 Java API。
3. **Node Harness 执行面**（Debian/PRoot 内的官方 DSH）：TASK-004 起接入。

## 当前已落地（M1 / TASK-000 ~ 003）

```text
DshMobile.Android (net11.0-android, C#)
  MainActivity ── NativeBridgeClient ──JNI──▶ DshMobile.NativeBinding (binding of dsh-native-bridge.aar)
        │                ▲                          │
        │                │ NativeCallback (后台线程)  ▼
        │                └──────────────── dshmobile.nativebridge.DshNativeFacade (Java)
        │                                           │ 单写者 Executor
        ▼                                           ▼
  DshMobile.Core (net11.0)                com.dshbox.app.common.Versions (DSHBox Kotlin, 源码原样)
  BridgeRequest/Response/ErrorCodes
```

| 项目 | TFM | 职责 |
|---|---|---|
| `src/DshMobile.Core` | `net11.0` | 协议 DTO、错误码、JSON 源生成；无 Android 依赖，可在桌面单测 |
| `src/DshMobile.NativeBinding` | `net11.0-android` | `android-bindinglib`，只绑定 `dsh-native-bridge.aar`；DSHBox AAR `Bind="false"` 随包 |
| `src/DshMobile.Android` | `net11.0-android` | App 宿主：首页、诊断日志、Bridge 压测 |
| `native/native-bridge` | Gradle Android Library | Java Facade，内部调用 DSHBox Kotlin |
| `tests/DshMobile.Core.Tests` | `net11.0` | 协议契约测试 |

## M2 / TASK-004 新增

```text
MainActivity ─▶ BridgeHost ─▶ BridgeClient ─JNI─▶ DshNativeFacade ─▶ RuntimeController (Kotlin, IO 协程, Mutex 串行)
     │                                                                   │
     └─ HarnessForegroundService (specialUse)                            ▼
                                                     DSHBox DefaultSandboxManager / BundledRuntimeInstaller
                                                                         │ exec nativeLibraryDir/libproot.so
                                                                         ▼
                                                     PRoot → Debian 13 → Node 24 → DSH (web, 127.0.0.1:3080)
```

- APK 携带 `assets/dsh/<ver>.tar.zst` 与 `assets/runtime/android-side.tar.zst`（与 DSHBox ≥ 1.4 相同）；base/node 由运行环境包导入。
- `extractNativeLibs=true`：PRoot 以可执行文件方式从 nativeLibraryDir 启动。

## M3 / TASK-006 WebUI

```text
DshWebActivity (C#) ── new WebView ──▶ DshNativeFacade.attachWebUi(webView, listener)   [主线程]
                                                │
                                                ▼
                        WebUiController (Kotlin)：订阅 SandboxManager.dshLaunchToken
                          · DshTokenRevisionTracker（token 变化即重载）
                          · DshOriginPolicy（仅 127.0.0.1:3080）
                          · loadUrl("/?token=…") → DSH 换签名 Cookie
                          · 401 每修订恢复一次；外链 → 系统浏览器
                                                │ onWebUiState(state, 路径)   ← 不含 token
                                                ▼
                                       DshWebActivity 状态条
```

## 线程与生命周期

- Facade 所有请求在单线程 Executor 上串行执行；回调永远不在调用线程。
- C# `NativeBridgeClient` 以 `requestId → TaskCompletionSource` 关联响应，支持 `CancellationToken` 与本地超时；在途表可观测（压测要求最终为 0）。
- Facade 生命周期绑定 `Application`（不是 Activity）；旋转 / 切后台不重建 Facade。

## 禁止的架构

见方案 3.1 节：C# 自建 Agent Loop、直读 DSH 私有存储、把 PRoot 视为安全隔离、双 AccessibilityService、默认授予 ADB/Root。
