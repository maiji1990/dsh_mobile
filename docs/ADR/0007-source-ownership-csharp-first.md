# ADR-0007：C#-first 源码归属——逐切片以 .NET 11 Android 自主实现取代 DSHBox AAR / Kotlin Facade

- 状态：已接受（2026-11-10）
- 取代：[ADR-0002](0002-dshbox-reuse-via-submodule-and-java-facade.md)（子模块 + Java Facade 作为**长期**路线）
- 影响：ADR-0004（Interlock Relay）、ADR-0005（DSHBox 浏览器栈）、ADR-0006（JSch）中“复用 DSHBox/Kotlin 实现”的部分，均降级为过渡实现，按本 ADR 的切片逐一替换
- 来源：《DSH Mobile — 从 DSHBox 复用转为 .NET 11 Android 自主实现》（2026-11-10，审查基线 `main@8817e8e`、DSHBox `v1.5.1@7c9d33c`）

## 背景

ADR-0002 让 M1–M3 用最短路径跑通：DSHBox 子模块 → Gradle 构建 AAR → `DshNativeFacade` Java 门面 → C# 绑定。代价是产品的运行前提依赖外部项目的 Kotlin 业务层（`sandbox-manager`、`terminal-*`、`interlock-relay-core`、`browser-*`），C# 只是外壳：状态机、进程监管、错误恢复都在 Kotlin 侧，且每个功能都要穿越 JSON 门面。

## 决策

1. **长期维护权归 C#**：Android App、业务流程、权限与审批、文件、终端会话、WebView、MCP 宿主、SSH 由本仓库的 C# 代码拥有。目标依赖链：
   `DshMobile.Android → DshMobile.Application → DshMobile.Runtime / Terminal / Files / Automation / Browser / Remote / Diagnostics → DshMobile.Native (最小 NDK + P/Invoke) → PRoot → Debian → Node → 官方 DeepSeek Harness（不改）`。
2. **不改官方引擎**：继续原样运行上游 DeepSeek Harness（Node/Cordis）；**不移植、不重写 AgentLoop**。能力仍以 Cordis 插件 / MCP / 官方 profile 扩展。
3. **标准平台互操作不算“桥”**：C# 经 .NET for Android 调用 Android Java API 是平台互操作，不需要自建业务 Facade。要移除的是 DSHBox AAR、Kotlin 编译模块、`DshNativeFacade` 这类自建业务门面。
4. **原生能力保留，但源码自有**：`libproot`、PTY ioctl、进程组、从 `nativeLibraryDir` 启动 ELF（Android 10+ W^X）仍是原生能力；使用本仓库内可追溯源码 / 锁定版本，必要时自写尽可能小的 `libdshmobile_native.so`（P/Invoke）。**不为“纯 C#”牺牲兼容性**。
5. **宿主 ↔ guest 仍需最小内部 IPC**（Unix-domain socket 等，私有路径 + 能力分级 + TTL + 审计 + 取消）。同 UID 下该通道**不是安全边界**，guest 插件按同 UID 高风险代码对待，写操作逐动作审批。
6. **逐切片迁移，旧实现保留比对**：
   - 每个 PR 只替换**一个完整功能切片**；新实现先以 `SliceMode.LegacyWithShadow`（旧实现决策、C# 只读比对）运行，比对结果写入 `files/dshmobile-runtime/shadow-reports.jsonl`。
   - 切片的新旧实现必须跑**同一套黄金向量**（`tests/TestVectors/*.json`：C# 测试与旧 Kotlin/DSHBox 单测同时读取）。
   - 新实现成为默认实现的前提：同一向量全绿 + 真机验收（AGENTS.md 规则 9）。**只有此后**才能删除该切片的 DSHBox 依赖。
   - 迁移期间新增功能只写进新 C# 模块，不给待淘汰的 Kotlin 代码加需求（修安全缺陷除外）。
7. **UI 使用 Material 3**：迁移后 / 新写的原生界面采用 Material Components for Android（Material 3 主题与组件，经 `Xamarin.Google.Android.Material` 绑定）。主题切换作为独立 UI 切片做（需截图级真机验收），不与运行时切片混在一个 PR。
8. **许可证与来源**：从 DSHBox（GPL-3.0）翻译为 C# 的代码在文件头注明上游路径、版本、commit 与许可证，并在 `docs/UPSTREAMS.md` 登记；不复制 DSHBox 品牌资产。去掉子模块不代表来源消失。

## 迁移顺序

见 [docs/MIGRATION.md](../MIGRATION.md)。先 Runtime（最深的 DSHBox 依赖）：
`Versions/Constants → RuntimeManifest/LayerInstaller → Exec/PRoot → ProcessSupervisor → DSH profile launcher → Health/Events → WebView Token`。
第一项业务结论：C# 不经 `DshNativeFacade` / `RuntimeController.kt` / DSHBox `SandboxManager`，在 Android 17 真机完成 `导入 Debian → 启动 PRoot → dsh web READY → WebUI → 停止无孤儿`。

## 本 ADR 生效时已落地（PR-00 / PR-01 / PR-02 首段）

- 新项目 `DshMobile.Native`、`DshMobile.Runtime`、`DshMobile.Application`（`net11.0`，可在桌面单测），含方案第 5 节接口（`IRuntimeManager`、`IGuestProcess`）与 8 态 `RuntimeState` 状态机 + durable journal。
- `LegacyBridgeRuntimeManager`：把现有桥包成 `IRuntimeManager`，是当前生产实现。
- `RuntimeCoordinator`：C# 监督启动/停止（修复前台服务泄漏 / 失败仍停服务）；导入时 C# `RuntimeBundleInspector` 影子比对。
- 已移植并与旧实现跑同一向量：`Versions`、`RuntimeBundleLayout`、`RuntimeProfile`、导入校验规则、`Constants`/`SandboxConfig` 路径。

## 否决的备选

- **一次性重写全部模块后删子模块**：无法逐项比对，回归不可定位；违反“最小垂直切片”。
- **整仓绑定 DSHBox Kotlin 到 C#**：类型规模大、协程/Flow 语义无法映射，并且仍然依赖外部业务层（ADR-0002 已否决）。
- **把 PRoot / PTY 也改写为纯 C#**：Android 进程 / ioctl / W^X 约束下不可行或兼容性更差；保留最小 NDK 层。

## 后果

- 过渡期有两份实现并存，CI 同时跑 Gradle（旧）与 dotnet（新）测试；PR-10 才移除 Gradle、`native/native-bridge`、`DshMobile.NativeBinding`、`third_party/dshbox`。
- `docs/MIGRATION.md` 是迁移状态的唯一事实源，每个切片 PR 必须更新。
