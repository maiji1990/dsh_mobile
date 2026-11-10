# 迁移状态（MIGRATION）——DSHBox AAR / Kotlin Facade → .NET 11 Android C#

决策见 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)。本文件是迁移状态的**唯一事实源**，每个切片 PR 必须更新。
状态取值：`未开始` / `接口已定义` / `影子比对中`（旧实现决策、C# 只读比对）/ `C# 默认（待删旧）` / `已完成（旧依赖已删）`。
“已验证”只写有证据的：`向量`（`tests/TestVectors` 新旧双跑）/ `单测` / `真机`（附日志位置）。

最后更新：2026-11-10（PR-00 + PR-01 + PR-02 首段，基线 `main@8817e8e`）。

## 1. 切片总表

| 切片 | 旧实现（Kotlin / DSHBox） | 旧代码行数¹ | C# 目标 | 状态 | 已验证 | 阻断 / 风险 |
|---|---|---|---|---|---|---|
| Versions / Constants / 路径布局 | DSHBox `common` (`Versions.kt`, `Constants.kt`)、`SandboxConfig.kt` | 838（common 全部） | `DshMobile.Runtime.Versions`、`RuntimeConstants`、`RuntimeLayout` | **影子比对中**（C# 已用于导入影子检查；旧桥仍做决策） | 向量（versions.json 29 例，新旧双绿）；源码契约测试（常量 / 目录 / 枚举逐项扫描上游） | — |
| RuntimeManifest / 层包校验 | `RuntimeBundleLayout.kt`、`RuntimeProfile.kt`、`DefaultSandboxManager.importRuntimeBundle/extractStagedLayers` 的校验段 | ≈350 | `DshMobile.Runtime.Bundle.{RuntimeBundleLayout, RuntimeProfile, RuntimeBundleInspector}` | **影子比对中**（`SliceMode.LegacyWithShadow`） | 向量（runtime-bundle-layout.json 24 例，新旧双绿）；单测 12 例（错误码与上游一致）；真实 200 MB 锁定包三层 SHA-256 全部核对通过（桌面） | **真机影子报告未采集**（无设备连接）；尚未做解包 / 原子换层 |
| LayerInstaller（解包 + 原子换层 + 回滚） | `DefaultSandboxManager.swapRuntimeBody`、`BundleManager.extractTarGz`、`BundledRuntimeInstaller` | ≈900 | `DshMobile.Runtime.Bundle.LayerInstaller`（未建） | 未开始 | — | zstd 解压需选定 C# 依赖（候选 ZstdSharp.Port，MIT）并验证 tar 硬链接 / 符号链接 / 权限位与上游一致；悬空 `/var/run` 链接问题（UPSTREAMS 已登记） |
| 进程启动 / PRoot / 前台服务 | `SandboxProcessRunner.kt`、`DefaultSandboxManager.startSandbox/startDsh/stop*`、`SandboxSupervisor.kt` | ≈2300 | `DshMobile.Runtime.{ProotRunner, DshProcessSupervisor}` + `DshMobile.Native` | **接口已定义**（`IRuntimeManager`、`IGuestProcess`、8 态状态机 + journal）；监督逻辑已在 C# `RuntimeCoordinator`（实现仍是旧桥） | 单测（状态机 13、协调器 14）；W^X 可执行策略单测 | 需 `libdshmobile_native.so`（进程组、fork/exec、waitpid）；DSHBox 预编译 `libproot.so` **非 16 KB 页对齐**（Release 构建 XA0141 警告）→ 须从源码以 NDK 重编（`native/proot/`） |
| DSH profile 启动 / 健康 / 事件 | `DshHealthPolicy.kt`、`HttpHealthChecker.kt`、`DshLayer.kt`、`OfficialPluginOverlay.kt` | ≈600 | `DshMobile.Runtime.{DshHost, ProfileRegistry, HealthProbe}` | 未开始 | — | — |
| WebView / Token / 401 | `WebUiController.kt` + `dsh-renderer/api` | 220 + 1229 | `DshMobile.Android.DshWebViewController` | 未开始 | — | token 不得进入 C# 日志；下载当前 `DOWNLOAD_UNSUPPORTED`（缺陷 4） |
| 文件 / SAF | （C# 已有 `WorkspaceFileSystem`） | — | `DshMobile.Files` | C# 已拥有（无 DSHBox 依赖） | 单测 + 真机（TASK-007） | — |
| 插件 / Node / pnpm | `RuntimeController` plugin.*、`GuestToolchain.kt` | ≈180 | `DshMobile.Plugins` | **argv 规则已移植**（`PluginCommand`、`GuestCommandLine`） | 向量（plugin-commands.json，新旧双绿） | 安装 / 卸载 / 回滚仍走旧桥 |
| 终端 PTY | `TerminalController.kt` + DSHBox `terminal-session/view/emulator` | 251 + 807 + 2837 + 5333 | `DshMobile.Terminal` + 本地 xterm.js + NDK PTY | 未开始 | — | 不得以 TextView 替换已验证的多会话 TerminalView |
| 手机操控 `ui_*` | `MobileAutomation.kt`、`RelayApproval.kt` + `interlock-relay-core` | 154 + 129 + 22021 | `DshMobile.Automation` | 未开始 | — | 写操作仍禁用；防自审批未验证前不得开放 |
| 浏览器 `browser_*` | `BrowserTools.kt` + `browser-*` + browser-use 插件 | 155 + 35821 | `DshMobile.Browser` | 未开始 | — | 24 个工具逐项可行性核对 |
| 远程 SSH | `RemoteHosts.kt`、`SshKeys.kt` + JSch | 455 + 173 | `DshMobile.Remote` | 未开始 | — | 必须先验证 C# SSH 库能用**不可导出** Keystore EC 私钥的自定义签名；不得导出私钥 / 关闭主机指纹校验 |
| 门面 / 绑定 | `DshNativeFacade.java` 等、`DshMobile.NativeBinding` | 506 + 71 | 删除 | 保留（过渡） | — | PR-10 |

¹ `wc -l`（含注释），`main@8817e8e`；DSHBox 模块为 `src/main` 下 `.kt/.java` 总行数。

## 2. 当前依赖链（2026-11-10）

```text
MainActivity ─▶ BridgeHost ─▶ RuntimeCoordinator (C#, 状态机 + journal + 监督)        ← 新
                                   │  IRuntimeManager
                                   ▼
                           LegacyBridgeRuntimeManager (C#)                              ← 新（过渡）
                                   │  BridgeClient JSON
                                   ▼
                    DshNativeFacade ─▶ RuntimeController.kt ─▶ DSHBox sandbox-manager ─▶ PRoot
                                   ▲
       导入时并行：RuntimeBundleInspector (C#, 只读) ── 比对 ──▶ files/dshmobile-runtime/shadow-reports.jsonl
```

## 3. PR 进度

| PR | 内容 | 状态 |
|---|---|---|
| PR-00 | CI `node --test` 目录解析（Windows Node 22）、插件参数 shell 引号、契约向量 | 代码完成，本地全绿；CI 待推送后确认；**真机 baseline 回归未做**（无设备） |
| PR-01 | ADR-0007、AGENTS.md、`DshMobile.Native/Runtime/Application` 接口 + DI + 单测 | 代码完成，Debug / Release APK 构建通过；真机未验证 |
| PR-02 | Runtime：Versions/Constants → RuntimeManifest/层校验（影子） | **首段完成**（校验 + 影子比对）；LayerInstaller（解包 / 换层 / 回滚）未开始 |
| PR-03 … PR-11 | 见迁移方案第 6 节 | 未开始 |

## 4. 已修复的现有缺陷（迁移方案第 7 节）

| # | 缺陷 | 修复 | 验证 |
|---|---|---|---|
| 1 | CI `node --test <dir>` 在 Windows Node 22 把目录当模块（`MODULE_NOT_FOUND`，`main@8817e8e` 的 CI 失败） | `tooling/run-node-tests.mjs` 显式枚举 `*.test.mjs`，0 文件 / 0 通过即失败；CI 固定 Node 22.x 与本地一致复现 | 本地 31/31；空目录 exit 3 |
| 2 | `PACKAGE_SPEC` 允许 `>`/`<`/`*`，原样拼入 `sh -c`：`pkg@>=1.0.0` 会把输出重定向到文件 `=1.0.0` | Kotlin `pluginCommand` 返回 argv，`shellJoin` 逐 token 单引号；C# `PluginCommand` + `GuestCommandLine` 同规则；共享向量 | Kotlin `PluginSpecTest` 6/6、C# 向量全绿；源码扫描禁止回退 |
| 3 | `RunStartAsync` 先起前台服务，沙箱失败不清理；`RunStopAsync` 失败仍停服务；且旧 `sandbox.start` 在沙箱 `ERROR` 时仍回 `ok=true` | `RuntimeCoordinator`：以返回的沙箱状态判定；失败 → 尽力 `sandbox.stop` → 成功才释放服务；停止失败保留服务并置 `Failed`；DSH 失败保留沙箱 | 协调器单测 14 例；**真机未验证** |
| 4 | WebUI 下载 `DOWNLOAD_UNSUPPORTED` | 未处理（WebView 切片） | — |
| 5 | CI 无可运行 APK 的真机门禁 | 未处理（需设备农场 / 自托管 runner） | — |
| 6 | SDK 固定 `11.0.100-rc.2` | 不在本 PR 改（独立 PR） | — |

## 5. 证据与采集方法

- 新旧实现双跑：`dotnet test tests/DshMobile.Runtime.Tests`（C#）+ `native/gradlew :native-bridge:testDebugUnitTest`（旧 Kotlin/DSHBox，`PluginSpecTest`、`LegacyRuntimeParityTest`）。
- 真机影子报告（每次导入一行 JSON）：
  ```bash
  adb shell run-as org.example.dshmobile cat files/dshmobile-runtime/shadow-reports.jsonl
  ```
  `verdict` 必须为 `Agree`；`Diverge` 为迁移阻断，`Inconclusive` 需说明（如 `SANDBOX_RUNNING`）。拉取的报告存 `migration/parity/`。
- 运行时状态 journal：`files/dshmobile-runtime/runtime-journal.jsonl`。

## 6. 下一步（PR-02 后半 → PR-03）

1. **真机**：Android 17 上做一次导入（采集 `shadow-reports.jsonl`，须 `Agree`）、启动 / 停止 / 沙箱失败注入，确认协调器行为与 journal；补 baseline 指标（冷启动、内存、APK 尺寸：本分支 Release 123,638,621 B，含重建的桥 AAR；新增 C# 程序集裁剪后每 ABI 约 150 KB）。
2. `LayerInstaller`：选定 zstd/tar 实现，按上游 staging → 校验 → 解包 → 写 `.dshbox/layer-<name>.sha256` 哨兵 → 原子换层；对同一 zip 比对 C# 与上游产出的目录树哈希（`DirHash` 口径）作为向量；仍以影子方式运行。
3. `native/proot` + `native/dshmobile-native`：从源码以 NDK 27 构建 16 KB 对齐的 `libproot.so`，以及 `dshm_abi_version` / spawn / waitpid / killpg 最小 ABI。
