# 迁移状态（MIGRATION）——DSHBox AAR / Kotlin Facade → .NET 11 Android C#

决策见 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)。本文件是迁移状态的**唯一事实源**，每个切片 PR 必须更新。
状态取值：`未开始` / `接口已定义` / `影子比对中`（旧实现决策、C# 只读比对）/ `C# 默认（待删旧）` / `已完成（旧依赖已删）`。
“已验证”只写有证据的：`向量`（`tests/TestVectors` 新旧双跑）/ `单测` / `真机`（附日志位置）。

最后更新：2026-11-11（PR-00 + PR-01 + PR-02 校验段 + 解包段；两段影子已在 Android 17 真机 `Agree`）。

## 1. 切片总表

| 切片 | 旧实现（Kotlin / DSHBox） | 旧代码行数¹ | C# 目标 | 状态 | 已验证 | 阻断 / 风险 |
|---|---|---|---|---|---|---|
| Versions / Constants / 路径布局 | DSHBox `common` (`Versions.kt`, `Constants.kt`)、`SandboxConfig.kt` | 838（common 全部） | `DshMobile.Runtime.Versions`、`RuntimeConstants`、`RuntimeLayout` | **影子比对中**（C# 已用于导入影子检查；旧桥仍做决策） | 向量（versions.json 29 例，新旧双绿）；源码契约测试（常量 / 目录 / 枚举逐项扫描上游） | — |
| RuntimeManifest / 层包校验 | `RuntimeBundleLayout.kt`、`RuntimeProfile.kt`、`DefaultSandboxManager.importRuntimeBundle/extractStagedLayers` 的校验段 | ≈350 | `DshMobile.Runtime.Bundle.{RuntimeBundleLayout, RuntimeProfile, RuntimeBundleInspector}` | **影子比对中**（`SliceMode.LegacyWithShadow`） | 向量（runtime-bundle-layout.json 24 例，新旧双绿）；单测 12 例（错误码与上游一致）；真实 200 MB 锁定包三层 SHA-256 全部核对通过（桌面） | 真机影子 **Agree**（2026-11-11 Honor PTP-AN10 / Android 17，[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)）；解包段见下一行 |
| LayerInstaller · 解包 | `BundleManager.extractTarGz` / `applyEntryMode` / `isWithinRoot`、魔数识别 | ≈330 | `DshMobile.Runtime.Layers.{LayerExtractor, DryRunExtractionTarget, DiskExtractionTarget, LayerTreeScanner, LayerManifest}` | **影子比对中**（导入成功后，C# 直接从同一 zip 干跑解包，与旧实现装好的 `runtime-current/<layer>` 逐项比对：路径 / 类型 / 大小 / SHA-256 / 链接 / 属主权限位；不写盘） | 向量（layer-extract.json 19 例：旧 Kotlin 在桌面 JVM 跑 15 例全绿，4 例依赖真实符号链接或 Linux 目录权限只由 C# 跑；C# 19/19；磁盘解包 = 干跑清单）；真实锁定包干跑：base 10,324 项 / 447 MiB、node 5,781 项 / 185 MiB、android-side 11 项，0 告警，桌面 5.5 s | 真机影子 **Agree**：base 10,325 / node 5,782 / android-side 12 项逐项一致，5.6 s（[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)）；bzip2 / xz 容器 C# 未支持（返回 `BUNDLE_FORMAT_UNSUPPORTED`，官方包均为 zstd；旧实现支持）；结果依赖宿主路径是否存在（字面绝对链接，见 §7），设备上用真实文件系统探测 |
| LayerInstaller · 安装 / 换层 | `DefaultSandboxManager.importRuntimeBundle`（zip 分支）、`extractStagedLayers`、`swapRuntimeBody`、`writeFileAtomically` | ≈250 | `DshMobile.Runtime.Layers.LayerInstaller` + `RuntimeCoordinator` 的 `SliceMode.CSharp` | **C# 可选，真机已通过**（开发者长按“导入”逐次选择）；默认仍为旧实现 + 影子，切换默认需单独决定。C# 模式：校验 → 解包到私有 staging + 哨兵 → 旧 body 移入 `previous/` → 新层就位 → profile 原子写 → 装后树校验（`runtime.layer-install`，与同一 zip 干跑逐项比对）→ 旧桥 `runtime.prepare` 供给内置 DSH 层 | 向量（install-scenarios.json 4 例：全新 / 无 previous / 有 previous / 坏层，整个 files/ 树逐项比对，旧 Kotlin 与 C# 均全绿）；协调器单测 3 例 | 真机（2026-11-11 PTP-AN10 / Android 17，[证据](../migration/parity/2026-11-11-PTP-AN10-A17-layer-install.md)）：C# 导入 5.7 s，装后树 `Agree`，旧运行时接受（`runtime=True`），在其上 DSH web `Ready`、健康全通过、停止无孤儿；未测首次安装 / 低磁盘 / 中途强杀。完整 rootfs 无法在 Windows 落盘（`:` 文件名、大小写冲突），桌面只跑夹具；沿用旧实现：`previous/` 不存在时旧层被直接删除（§7） |
| LayerInstaller · 回滚 / 内置层安装 | `BundleManager.promote/rollback`、`BundledRuntimeInstaller`、`installAssembledRuntime` | ≈450 | `LayerInstaller.RollbackAsync` 等（未建） | 未开始 | — | 旧导入路径本身没有用户可触发的回滚；先确认是否需要 |
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
| PR-00 | CI `node --test` 目录解析（Windows Node 22）、插件参数 shell 引号、契约向量 | 已合并（#1）；CI 通过；真机回归：冷启动 228–293 ms（基线 220–290 ms）、PSS 128 MB、导入 / 启停正常（[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)） |
| PR-01 | ADR-0007、AGENTS.md、`DshMobile.Native/Runtime/Application` 接口 + DI + 单测 | 已合并（#1）；Release（full trim）真机运行通过 |
| PR-02 | Runtime：Versions/Constants → RuntimeManifest/层校验 → 层解包（均为影子） | 校验段已合并（#1），解包段已合并（#3）；两段真机影子均 `Agree`；换层 / 回滚未开始 |
| PR-03 … PR-11 | 见迁移方案第 6 节 | 未开始 |

## 4. 已修复的现有缺陷（迁移方案第 7 节）

| # | 缺陷 | 修复 | 验证 |
|---|---|---|---|
| 1 | CI `node --test <dir>` 在 Windows Node 22 把目录当模块（`MODULE_NOT_FOUND`，`main@8817e8e` 的 CI 失败） | `tooling/run-node-tests.mjs` 显式枚举 `*.test.mjs`，0 文件 / 0 通过即失败；CI 固定 Node 22.x 与本地一致复现 | 本地 31/31；空目录 exit 3 |
| 2 | `PACKAGE_SPEC` 允许 `>`/`<`/`*`，原样拼入 `sh -c`：`pkg@>=1.0.0` 会把输出重定向到文件 `=1.0.0` | Kotlin `pluginCommand` 返回 argv，`shellJoin` 逐 token 单引号；C# `PluginCommand` + `GuestCommandLine` 同规则；共享向量 | Kotlin `PluginSpecTest` 6/6、C# 向量全绿；源码扫描禁止回退 |
| 3 | `RunStartAsync` 先起前台服务，沙箱失败不清理；`RunStopAsync` 失败仍停服务；且旧 `sandbox.start` 在沙箱 `ERROR` 时仍回 `ok=true` | `RuntimeCoordinator`：以返回的沙箱状态判定；失败 → 尽力 `sandbox.stop` → 成功才释放服务；停止失败保留服务并置 `Failed`；DSH 失败保留沙箱 | 协调器单测 14 例；真机：启动 / 重复启动 / 停止无孤儿 / 强杀后 journal 恢复 `RUNTIME_INTERRUPTED` 通过；失败清理分支仅单测（无法安全注入） |
| 4 | WebUI 下载 `DOWNLOAD_UNSUPPORTED` | 未处理（WebView 切片） | — |
| 5 | CI 无可运行 APK 的真机门禁 | 未处理（需设备农场 / 自托管 runner） | — |
| 6 | SDK 固定 `11.0.100-rc.2` | 不在本 PR 改（独立 PR） | — |

## 5. 证据与采集方法

- 新旧实现双跑：`dotnet test tests/DshMobile.Runtime.Tests`（C#）+ `native/gradlew :native-bridge:testDebugUnitTest`（旧 Kotlin/DSHBox，`PluginSpecTest`、`LegacyRuntimeParityTest`）。
- 真机影子报告（每次导入一行 JSON；Release 构建不可 `run-as`，此时以 `adb logcat -s DshMobile:I` 中的 `shadow …` 行为准）：
  ```bash
  adb shell run-as org.example.dshmobile cat files/dshmobile-runtime/shadow-reports.jsonl
  ```
  `verdict` 必须为 `Agree`；`Diverge` 为迁移阻断，`Inconclusive` 需说明（如 `SANDBOX_RUNNING`）。拉取的报告存 `migration/parity/`。
- 运行时状态 journal：`files/dshmobile-runtime/runtime-journal.jsonl`。

## 6. 下一步（PR-02 收尾 → PR-03）

1. ~~真机：导入影子、启停、journal~~ — 2026-11-11 完成（[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)）。仍缺：沙箱启动失败 / 停止失败的真机注入、Android 10、16 KB 页设备。
2. `LayerInstaller` 换层 / 回滚：staging → 校验（已有）→ 解包（已有）→ 写 `.dshbox/layer-<name>.sha256` 哨兵 → 原子换层 → 失败回滚；对旧实现的 `swapRuntimeBody` 行为写向量；仍以影子方式运行，真机两段影子报告均为 `Agree` 后才把导入切到 C#。
3. `native/proot` + `native/dshmobile-native`：从源码以 NDK 27 构建 16 KB 对齐的 `libproot.so`，以及 `dshm_abi_version` / spawn / waitpid / killpg 最小 ABI。

## 7. 迁移中发现的旧实现事实（移植时必须保持，或单独决定修正）

| 事实 | 来源 | 处理 |
|---|---|---|
| commons-compress `isFile()` 对除目录外的**所有**类型为真：设备节点 / FIFO 被解成**空的普通文件**，源码里“skip device nodes”分支实际不可达 | `javap` 反汇编 commons-compress 1.27.1 `TarArchiveEntry.isFile` | C# 保持一致（向量 `devices.tar`） |
| 符号链接按**字面**创建：绝对链接在宿主上指向 Android 的路径。规范路径检查因此依赖宿主：`x -> /etc` 在 Android 上可解析，写入 `x/…` 被判越界；`x.pem -> /usr/share/…` 在 Android 上悬空，不被跟随 | `extractTarGz` + JDK `canonicalize_md.c` | C# 以宿主探测复现（设备上为真实文件系统）；Debian `/etc/ssl/certs` 依赖后者（真实 base 层验证） |
| 规范路径 = 能解析的最长前缀的 realpath + 未解析尾部按字面折叠；悬空链接不被跟随 | JDK `canonicalize_md.c` | C# `VirtualTree.Canonicalize` 复现 |
| 只设属主权限位；x = 任意执行位；目录权限立即生效（`0555` 目录会挡住其后的子项，仅 Linux） | `applyEntryMode` | C# 一致（向量 `err-readonly-dir.tar`，仅 C#） |
| 硬链接变成独立副本；目标在后的延后处理；始终解析不到只记日志不报错 | `extractTarGz` | C# 一致，并把告警带回结果 |
| `swapRuntimeBody` 不创建 `runtime-current/previous/`：它不存在时重命名失败，旧 base/node/android-side **被直接删除**，旧 profile 被覆盖，不留回滚副本 | `swapRuntimeBody` + 向量 `replace without previous/` | C# 一致（向量锁定）；是否改为先建 `previous/` 需单独决定（会改变磁盘占用） |
| 导入成功后旧桥再调 `provisionBundledDsh`（APK 内 DSH 层版本仲裁） | `RuntimeController` `runtime.importBundle` | C# 导入后调用旧桥 `runtime.prepare` 完成同一步 |
