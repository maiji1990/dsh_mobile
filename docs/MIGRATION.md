# 迁移状态（MIGRATION）——DSHBox AAR / Kotlin Facade → .NET 11 Android C#

决策见 [ADR-0007](ADR/0007-source-ownership-csharp-first.md)。本文件是迁移状态的**唯一事实源**，每个切片 PR 必须更新。
状态取值：`未开始` / `接口已定义` / `影子比对中`（旧实现决策、C# 只读比对）/ `C# 默认（待删旧）` / `已完成（旧依赖已删）`。
“已验证”只写有证据的：`向量`（`tests/TestVectors` 新旧双跑）/ `单测` / `真机`（附日志位置）。

最后更新：2026-11-11。**里程碑**：方案第 12 节「导入 → 启动 PRoot → READY → WebUI → 停止无孤儿」的每一步都已有不经 DSHBox 代码的 C# 实现并通过 Android 17 真机（仍为开发者可选路径，默认未切换）。

## 1. 切片总表

| 切片 | 旧实现（Kotlin / DSHBox） | 旧代码行数¹ | C# 目标 | 状态 | 已验证 | 阻断 / 风险 |
|---|---|---|---|---|---|---|
| Versions / Constants / 路径布局 | DSHBox `common` (`Versions.kt`, `Constants.kt`)、`SandboxConfig.kt` | 838（common 全部） | `DshMobile.Runtime.Versions`、`RuntimeConstants`、`RuntimeLayout` | **影子比对中**（C# 已用于导入影子检查；旧桥仍做决策） | 向量（versions.json 29 例，新旧双绿）；源码契约测试（常量 / 目录 / 枚举逐项扫描上游） | — |
| RuntimeManifest / 层包校验 | `RuntimeBundleLayout.kt`、`RuntimeProfile.kt`、`DefaultSandboxManager.importRuntimeBundle/extractStagedLayers` 的校验段 | ≈350 | `DshMobile.Runtime.Bundle.{RuntimeBundleLayout, RuntimeProfile, RuntimeBundleInspector}` | **影子比对中**（`SliceMode.LegacyWithShadow`） | 向量（runtime-bundle-layout.json 24 例，新旧双绿）；单测 12 例（错误码与上游一致）；真实 200 MB 锁定包三层 SHA-256 全部核对通过（桌面） | 真机影子 **Agree**（2026-11-11 Honor PTP-AN10 / Android 17，[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)）；解包段见下一行 |
| LayerInstaller · 解包 | `BundleManager.extractTarGz` / `applyEntryMode` / `isWithinRoot`、魔数识别 | ≈330 | `DshMobile.Runtime.Layers.{LayerExtractor, DryRunExtractionTarget, DiskExtractionTarget, LayerTreeScanner, LayerManifest}` | **影子比对中**（导入成功后，C# 直接从同一 zip 干跑解包，与旧实现装好的 `runtime-current/<layer>` 逐项比对：路径 / 类型 / 大小 / SHA-256 / 链接 / 属主权限位；不写盘） | 向量（layer-extract.json 19 例：旧 Kotlin 在桌面 JVM 跑 15 例全绿，4 例依赖真实符号链接或 Linux 目录权限只由 C# 跑；C# 19/19；磁盘解包 = 干跑清单）；真实锁定包干跑：base 10,324 项 / 447 MiB、node 5,781 项 / 185 MiB、android-side 11 项，0 告警，桌面 5.5 s | 真机影子 **Agree**：base 10,325 / node 5,782 / android-side 12 项逐项一致，5.6 s（[真机证据](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)）；bzip2 / xz 容器 C# 未支持（返回 `BUNDLE_FORMAT_UNSUPPORTED`，官方包均为 zstd；旧实现支持）；结果依赖宿主路径是否存在（字面绝对链接，见 §7），设备上用真实文件系统探测 |
| LayerInstaller · 安装 / 换层 | `DefaultSandboxManager.importRuntimeBundle`（zip 分支）、`extractStagedLayers`、`swapRuntimeBody`、`writeFileAtomically` | ≈250 | `DshMobile.Runtime.Layers.LayerInstaller` + `RuntimeCoordinator` 的 `SliceMode.CSharp` | **C# 可选，真机已通过**（开发者长按“导入”逐次选择）；默认仍为旧实现 + 影子，切换默认需单独决定。C# 模式：校验 → 解包到私有 staging + 哨兵 → 旧 body 移入 `previous/` → 新层就位 → profile 原子写 → 装后树校验（`runtime.layer-install`，与同一 zip 干跑逐项比对）→ 旧桥 `runtime.prepare` 供给内置 DSH 层 | 向量（install-scenarios.json 4 例：全新 / 无 previous / 有 previous / 坏层，整个 files/ 树逐项比对，旧 Kotlin 与 C# 均全绿）；协调器单测 3 例 | 真机（2026-11-11 PTP-AN10 / Android 17，[证据](../migration/parity/2026-11-11-PTP-AN10-A17-layer-install.md)）：C# 导入 5.7 s，装后树 `Agree`，旧运行时接受（`runtime=True`），在其上 DSH web `Ready`、健康全通过、停止无孤儿；未测首次安装 / 低磁盘 / 中途强杀。完整 rootfs 无法在 Windows 落盘（`:` 文件名、大小写冲突），桌面只跑夹具；沿用旧实现：`previous/` 不存在时旧层被直接删除（§7） |
| LayerInstaller · 回滚 / 内置层安装 | `BundleManager.promote/rollback`、`BundledRuntimeInstaller`、`installAssembledRuntime` | ≈450 | `LayerInstaller.RollbackAsync` 等（未建） | 未开始 | — | 旧导入路径本身没有用户可触发的回滚；先确认是否需要 |
| Exec / PRoot · 一次性 guest 命令 | `DefaultSandboxManager.runGuestCommand`（argv）、`buildProotEnv` / `parseEnvExports` / `substitutionExports`、`SandboxProcessRunner.runGuestCommand`（进程、超时） | ≈200 | `DshMobile.Runtime.Guest.{ProotCommandBuilder, ProotGuestProcess}`（实现 `IGuestProcess`）；`.NET Process` 直接起 `libproot.so` | **影子比对中**（`MigrationOptions.GuestExec`）：旧诊断之后，C# 跑同样 4 条固定命令并比对输出（`runtime.guest-exec`） | 向量（proot-env.json 4 例：旧实现经反射调用私有 `buildProotEnv`，与 C# 双绿）；argv 结构测试；诊断命令表源码契约；进程运行器单测（退出码、合流、stdin EOF、超时杀树、截尾）；真机影子两次 **Agree**（[证据](../migration/parity/2026-11-11-PTP-AN10-A17-guest-exec.md)） | argv 与旧实现的 `/proc` 逐字节对照未完成；PTY 见「终端 PTY · 后端」行（终端界面仍用旧 TerminalView）；未移植 LogRedactor（输出只用于固定诊断） |
| 进程监管 · 观察 + 健康策略 | `SandboxProcessRunner` 的 `readProcTable` / `findDescendantByCmdline` / `descendantPids` / `killAll`、`DshHealthPolicy.decideDshHealth`、`HttpHealthChecker` | ≈350 | `DshMobile.Runtime.Supervision.{ProcessTable, RuntimeProcessSnapshot, DshHealthPolicy, HttpHealthProbe}` | **影子比对中**（只读）：诊断时紧接旧状态 / 健康读数，C# 自己读 `/proc` 找沙箱 / DSH 树、自己探测 WebUI，比对（`runtime.supervisor-observe`）；不启动、不杀进程 | 向量（dsh-health-policy.json 15 例：旧实现经反射调用 `internal` 的 `decideDshHealth`，与 C# 双绿）；伪 `/proc` 单测（BFS 顺序、标记查找、`killAll` 顺序、stat 解析）；HTTP 探测单测（401/200/302/503 视为就绪、端口关、非 HTTP） | 真机影子停止 / 运行 / 停止后均 **Agree**（[证据](../migration/parity/2026-11-11-PTP-AN10-A17-supervisor-observe.md)）；`/proc` 部分无法在 JVM 上跑旧实现，以设备为准；HTTP 探测改为原生 socket（避免 `HttpClient` 带入 ~2.7 MB） |
| 进程启动 · 沙箱 / DSH 启动 argv | `SandboxProcessRunner.buildProotSandboxCommand` / `buildProotDshCommand` / `layeredProotPrefix`、`DefaultSandboxManager.dshOverlayGuestPaths` / `linkShimHostDir` 及两处调用点 | ≈200 | `ProotCommandBuilder.{SandboxCommand, DshCommand(inPlaceRestart), DshOverlayGuestPaths}` | **影子比对中**（只读）：诊断时把 C# 会用的 argv 与旧实现进程的 `/proc/<pid>/cmdline` 逐 token 比对（`runtime.launcher-argv`） | 真机抓取的黄金 argv（device-proot-cmdlines.json）桌面测试；源码契约（两处 DSH 调用点差异）；真机影子 **Agree**（沙箱 14 / DSH 22 个参数，[证据](../migration/parity/2026-11-11-PTP-AN10-A17-launcher-argv.md)） | 进程仍由旧实现启动；C# 真正接管启动需同时接管 launch token（WebView 仍读旧实现的 token） |
| 进程启动 · C# 宿主（沙箱 + DSH + token + 健康循环） | `DefaultSandboxManager.startSandbox/startDsh/stopDsh/stopSandbox`、`ingestDshWebLaunchToken`、`sweepDeadWriterLocks`、健康循环；`LogRedactor` | ≈700 | `DshMobile.Runtime.Hosting.{DshHost, DshLaunchToken, DshWriterLockSweeper, LogRedactor}` + 协调器 `SliceMode.CSharp` 启停 | **C# 可选，真机里程碑通过**（长按「启动 DSH」逐次选择）；默认仍为旧实现 | 向量（log-redactor.json，旧实现双绿）；单测（token 解析、锁清理、前置条件拒绝、协调器失败路径）；真机：10.1 s READY、WebUI 加载、停止 / 强杀无孤儿、token 不泄漏（[证据](../migration/parity/2026-11-11-PTP-AN10-A17-csharp-host-milestone.md)） | C# 宿主运行期间终端 / 手机操控 / 浏览器不可用（它们跟随旧运行时状态）；健康循环重启分支未在真机注入故障；就地重启有意**不**复制旧实现缺 guest-cache 绑定的问题 |
| 进程启动 / PRoot / 前台服务 | `SandboxProcessRunner.kt`、`DefaultSandboxManager.startSandbox/startDsh/stop*`、`SandboxSupervisor.kt` | ≈2300 | `DshMobile.Runtime.{ProotRunner, DshProcessSupervisor}` + `DshMobile.Native` | **接口已定义**（`IRuntimeManager`、`IGuestProcess`、8 态状态机 + journal）；监督逻辑已在 C# `RuntimeCoordinator`（实现仍是旧桥） | 单测（状态机 13、协调器 14）；W^X 可执行策略单测 | 需 `libdshmobile_native.so`（进程组、fork/exec、waitpid）；DSHBox 预编译 `libproot.so` **非 16 KB 页对齐**（Release 构建 XA0141 警告）→ 须从源码以 NDK 重编（`native/proot/`） |
| DSH profile 启动 / 健康 / 事件 | `DshHealthPolicy.kt`、`HttpHealthChecker.kt`、`DshLayer.kt`、`OfficialPluginOverlay.kt` | ≈600 | `DshMobile.Runtime.{DshHost, ProfileRegistry, HealthProbe}` | 未开始 | — | — |
| WebView · 策略（源、token 修订、URL、导航、401） | `dsh-renderer/api` `DshOriginPolicy` / `DshTokenRevisionTracker`、`WebUiController` 的 `urlWithToken` / `shouldOverrideUrlLoading` / 401 规则 | ≈260 | `DshMobile.Core.WebUi.{DshOriginPolicy, DshTokenRevisionTracker, WebUiPolicy}` | **已移植（纯逻辑，尚未接入 WebView）** | 向量（webview-policy.json：源策略 90+ 例，含 `java.net.URI` 与 `System.Uri` 行为不同的对抗输入；token 修订序列；URL 编码）——旧实现（真实 `java.net.URI`）与 C# 双绿 | 源策略不用 `System.Uri`（它会把 `127.1`、展开的 IPv6 规范化成可信回环）；接入需与 C# 启动 DSH、接管 launch token 同一切片 |
| WebView · 宿主 / Token / 文件选择 / 下载 | `WebUiController.kt`（attach、token 订阅、文件选择、下载） | ≈150 | `DshMobile.App.WebUi.CSharpWebUiController` | **C# 可选，真机通过**（C# 宿主运行时自动使用） | 真机：官方 WebUI 已登录首页加载，无 401；token 不入日志 | 文件上传 / 外链 / 401 恢复未在真机操作；下载仍 `DOWNLOAD_UNSUPPORTED`（缺陷 4） |
| 文件 / SAF | （C# 已有 `WorkspaceFileSystem`） | — | `DshMobile.Files` | C# 已拥有（无 DSHBox 依赖） | 单测 + 真机（TASK-007） | — |
| 插件 / Node / pnpm · 列表 | `RuntimeController` `plugin.list`（`runPluginCommand` / `runGuestScript`）、`PluginsActivity` 的 TASK-009 解析 | ≈120 | `DshMobile.Runtime.Guest.GuestPlugins` + `PluginListShadowCheck` | **影子比对中**（只读，`MigrationOptions.PluginList`）：插件页用旧桥结果渲染后，C# 经自己的 PRoot 启动器跑同一 `dsh plugin list --json --depth 0` 并比对解析结果（`plugins.list`）；Agree 要求两边都是可解析的 JSON 列表 | 向量（plugin-commands.json，新旧双绿）；单测（解析、噪声前缀、版本差异、两边都不可解析不判 Agree）；真机 **Agree**（[证据](../migration/parity/2026-11-11-PTP-AN10-A17-plugins-list.md)） | 本机无第三方插件，真机只覆盖空列表；列表解析已从界面移到 `GuestPlugins`（界面与影子共用） |
| 插件 / Node / pnpm · 安装 / 卸载 | `RuntimeController` `plugin.add/remove`（只看退出码）、`GuestToolchain.kt`；DSHBox `plugin-manager` `InstallGuard` / `ProfileReader`（本 App 旧实现**未使用**） | ≈60 + 移植 ≈450 | `DshMobile.Runtime.Plugins.{PluginInstallGuard, PluginProfileReader, PluginPackageRules}` + 协调器 `ChangePluginWithCSharpAsync` | **C# 可选**（插件页长按「安装」/ 长按列表项逐次选择）；默认仍为旧桥。C# 安装：装前 / 装后依赖差集 → 新增包可加载性校验（`dsh` 字段、入口产物、载体挂载）→ 不可加载则只回收本次新增的包（`INSTALL_VERIFY_FAILED`）；C# 卸载：命令成功后核对依赖已移除（`REMOVE_VERIFY_FAILED`） | 单测：上游 `InstallGuardTest` 21 例逐例转写 + 回收失败 / 卸载核对 4 例；临时目录文件系统测试 9 例（普通 npm 包无 `dsh`、声明入口缺产物、载体、纯客户端、作用域名、名称越界）；协调器参数拒绝不启动进程；真机：C# 卸载核对通过、C# 安装 `is-number`（非插件，DSH 只警告、退出码 0）→ `INSTALL_VERIFY_FAILED` 并回收，列表回到装前（[证据](../migration/parity/2026-11-11-PTP-AN10-A17-plugin-install-guard.md)） | 会改动 guest 文件系统，不能影子双跑；行为与旧实现**有意不同**（旧实现对 `is-number` 这类非插件包报成功，见 COMPATIBILITY TASK-009），真机未装真正的 DSH 插件（成功路径 / 载体只有单测）；切默认需单独决定 |
| 终端 PTY · 后端（PR-06 第一段） | `TerminalController.kt` → DSHBox `TerminalCommandFactory.sandboxLoginShell` / `TerminalEnvFactory.sandboxEnv` / `TerminalProcFake` / `TerminalPathsResolver`；`terminal-emulator` 的 `termux.c`（PTY） | ≈350 | `DshMobile.Runtime.Guest.{TerminalCommandBuilder, ProotPtySession}` + `ProotGuestProcess.OpenPtyAsync` + `DshMobile.Native.NativePty`（`libdshmobile_native.so` ABI v1：`dshm_pty_spawn` / read / write / resize / wait / kill，本仓库 C 源码，NDK 27 构建，16 KB 对齐） | **影子比对中**（`terminal.launcher-argv`：诊断时把旧 TerminalView 会话的 `/proc/<pid>/cmdline` 与 C# argv 逐 token 比对）；C# PTY 只用于开发者自检（长按「诊断」→ `terminal.pty-probe`：prompt / tty / stty / resize / ^C=130 / exit 0 / 无残留）；终端界面仍为旧实现 | 向量（terminal-command.json 6 例，旧 Kotlin `LegacyTerminalCommandParityTest` 与 C# 双绿）；原生 ABI 源码契约（C 导出 ↔ LibraryImport 参数个数、ABI 版本、两份构建脚本导出检查、fork→exec 之间只用 async-signal-safe 调用）；脚本化假 shell 单测（与真机 bash 一致的 bracketed paste；探针通过 / ^C 失败 / 无提示符 / 打开失败错误码 / 挂断→强杀→关闭）；真机（2026-11-12 PTP-AN10 / Android 17，[证据](../migration/parity/2026-11-12-PTP-AN10-A17-terminal-pty.md)）：PTY 自检连续 3 次 PASS（/dev/pts、24×80→30×100、^C=130、exit 0、无残留），终端 argv 影子 **Agree**（22 个参数逐 token 一致） | 仅 1 个旧会话参与影子（多会话未测）；环境变量只有向量证据；App 被杀时的 PTY 子进程回收未测；xterm.js 界面、多会话、会话恢复未开始；不得以 TextView 替换已验证的多会话 TerminalView |
| 终端界面 · C# 多会话（PR-06 第二段） | `TerminalController.kt` + `TerminalActivity`（DSHBox `terminal-view` / `terminal-session`：`DshTerminalManager` 会话列表、Termux 渲染与 `KeyHandler`）；`GuestUser.kt` | ≈900 | `DshMobile.Application.Terminal.{TerminalSessions, TerminalKeys}`（会话列表 / 256 KiB 回放缓冲 / 附着 / 运行时停止时关闭）、`DshMobile.Runtime.Guest.GuestUser`、Android `CSharpTerminalActivity`（Material 3 + 本地 xterm.js 6.0.0，WebMessagePort 传字节，资源拦截只放行 `/terminal/**`）+ `TerminalInputView`（原生 `TYPE_NULL` 键盘） | **C# 可选**（长按「终端」→「C# 终端」）；默认仍为旧 TerminalView | 单测（会话编号 / 激活 / 自然退出 / 回放 / 截尾 / resize / 打开失败 / 协调器停止两条路径；按键序列与 CTRL 锁存；GuestUser 6 例；前端资源哈希锁定 + CSP + 页面无网络 API + Activity 源码契约）；真机（[证据](../migration/parity/2026-11-12-PTP-AN10-A17-csharp-terminal-ui.md)）：双会话 `/dev/pts/0`、`/dev/pts/1`，resize、^C=130、标签切换重放、离开后会话保留、10 万行输出 0.085 s、关闭与强杀无残留、旧终端 argv 影子不误比 C# 会话；首版两处真机缺陷已修（xterm.js IME 错字 → 原生输入视图；沙箱未启动时「停止」漏关 C# 会话） | 中文 / 组合输入、vim/less 应用光标模式、横屏未真机验证；与旧终端无会话级影子（两套会话互不可见，只比 argv）；切为默认前需人工输入验收 |
| 手机操控 `ui_*` · 工具注册与状态（PR-07 第一段） | `MobileAutomation.kt` 的 `UiToolsRegistration`（DSH `--patch` 层写入 / 删除、标记行、冲突不覆盖）与 `isAccessibilityEnabled`；`RuntimeController.relayStatus` | ≈90 | `DshMobile.Runtime.Automation.{UiToolsRegistration, AccessibilitySetting}` + `UiToolsShadowCheck`（`MigrationOptions.UiTools`） | **影子比对中**（只读）：每次旧 `relay.start / stop / status` 之后，C# 读同一 patch 层与无障碍设置，比对 registered / conflict / 层字节 / 无障碍 / 能力白名单 / guest CLI / server 名；写入仍由旧实现完成 | 向量（ui-tools-registration.json 11 例：旧 Kotlin `LegacyUiToolsRegistrationParityTest` 与 C# 双绿；accessibility-enabled.json 12 例仅 C#——JVM 单测里 `ComponentName` 是桩）；影子单测 5 例；真机（[证据](../migration/parity/2026-11-12-PTP-AN10-A17-ui-tools.md)）：状态 / 开启 / 关闭三次 **Agree**，开启后层文件与 C# 渲染逐字节一致 | 无障碍「已开启」只有向量证据（真机未开启本 App 服务）；冲突分支只有向量（Release 不能往 App 目录写外来文件）；C# 写入路径（`SliceMode.CSharp`）未做——旧 `relay.start` 把 Relay 启动与注册放在同一桥调用里 |
| 手机操控 `ui_*` · Relay 信箱信封解码（PR-07 第二段） | `interlock-relay-core` `transport/EnvelopeCodec.kt` 的 `decodeRequest` / `decodeControl`（大小、嵌套深度、id / 文件名、版本、封闭能力集、args、TTL 夹取、摘要形状、ask 限制）；`protocol/RelayWire.kt` `RelayError` / `RelayExitCode`；`Capability.kt` `CapabilityId` 线名 | ≈330 | `DshMobile.Runtime.Automation.Relay.{RelayEnvelope, RelayProtocol, OrgJson}` + `RelayEnvelopeProbe`（`MigrationOptions.RelayEnvelopeCodec`） | **影子比对中**（只读）：开发者在「手机操控」页点「协议信封比对」，向真实信箱写 16 条解码阶段必被拒的请求，比对旧 Relay 的 v1 `error.code` / v2 `cause` 与 C# 判定；解码仍由旧实现完成 | 向量（relay-envelope.json：request 37 + control 39 例，旧 Kotlin `LegacyRelayEnvelopeParityTest` 直接跑上游 `EnvelopeCodec` 与 C# 双绿；能力表 39 项 / 错误表 36 项对表；csharpOnly 7 例按 Android libcore org.json 语义）；探针单测 7 例；真机（[证据](../migration/parity/2026-11-12-PTP-AN10-A17-relay-envelope.md)）：两次 **16/16 Agree**，中继未开时 Inconclusive | 合法请求只有向量（真机发合法请求会进审批，探针不发）；C# 只收严格 JSON（org.json 的单引号 / 注释 / 尾随文本被拒，见 UPSTREAMS）；响应编码、认领 / 超时 / 满队、控制通道状态机未移植 |
| 手机操控 `ui_*` · 执行层（其余） | `RelayApproval.kt` + `interlock-relay-core`（无障碍服务、审批队列、信箱服务循环、响应编码、`relay` CLI） | 129 + ≈21700 | `DshMobile.Automation` | 未开始 | — | 写操作仍禁用；防自审批未验证前不得开放 |
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
| PR-06 | 终端：C# PTY 后端（`libdshmobile_native` + `ProotPtySession`）+ 终端 argv 影子 | 第一段 maiji1990/dsh_mobile#13（含 CI 修复，CI 通过）：真机 PTY 自检 PASS ×3、终端 argv 影子 Agree。第二段 maiji1990/dsh_mobile#14（C# 多会话终端界面，开发者可选）：真机主要路径通过 |
| PR-07 | 手机操控：`ui_*` 工具注册与状态（第一段，只读影子）→ Relay 信封解码（第二段，只读影子）→ 执行层其余 | 第一段已合并（maiji1990/dsh_mobile#16，CI 绿）；第二段完成待提交：向量新旧双绿、真机 2× 16/16 Agree；执行层其余未开始 |
| PR-03 … PR-11（其余） | 见迁移方案第 6 节与切片总表 | 见各切片行 |

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
| `killAll` 注释称“最深的子进程先死”，实际对后序列表 `asReversed()`，**先杀父进程**（根 → 子） | `SandboxProcessRunner.killAll` + C# `KillAllOrder` 测试 | C# 计算结果与旧实现一致（尚未用于真正杀进程）；PRoot 的被跟踪进程随其退出，实际影响小 |
| DSH 的就地重启调用点**不传** `guestCacheHostDir`（及 extraBinds），首次启动调用点传：自动重启后的 DSH 没有 `/opt/dshbox-cache`，pnpm 静默回落到工作区、内置缓存失效 | `DefaultSandboxManager` 两处 `buildProotDshCommand` + 源码契约测试 | C# 两种形态都建模并在影子中识别；是否修正需单独决定 |
| `/proc/<pid>/stat` 在**第一个** `") "` 之后取 ppid：进程名含 `") "` 时被误解析并跳过 | `readProcTable` | C# 保持一致（测试锁定） |
| `swapRuntimeBody` 不创建 `runtime-current/previous/`：它不存在时重命名失败，旧 base/node/android-side **被直接删除**，旧 profile 被覆盖，不留回滚副本 | `swapRuntimeBody` + 向量 `replace without previous/` | C# 一致（向量锁定）；是否改为先建 `previous/` 需单独决定（会改变磁盘占用） |
| 导入成功后旧桥再调 `provisionBundledDsh`（APK 内 DSH 层版本仲裁） | `RuntimeController` `runtime.importBundle` | C# 导入后调用旧桥 `runtime.prepare` 完成同一步 |
