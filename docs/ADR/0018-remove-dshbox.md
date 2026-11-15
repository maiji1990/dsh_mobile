# ADR-0018：移除 DSHBox 依赖（PR-10）、浏览器不迁移、跳过真机验收

- 状态：已接受（2026-11-15；项目负责人决定：「所有 PR 都完成再移除对 DSHBox 的依赖」→「可以移出了么」→
  「1. 不需要浏览器 2. 跳过真机，写 ADR 记录」）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first，PR-10 = 删除 DSHBox）、[ADR-0014](0014-in-repo-proot-build.md)（自建 PRoot）、
  [ADR-0015](0015-csharp-host-default.md) / [ADR-0016](0016-csharp-default-all-shadow-slices.md) / [ADR-0017](0017-remaining-slices-csharp-default-lazy-legacy-facade.md)（逐步翻转为 C# 默认）、
  取代 [ADR-0002](0002-dshbox-reuse-via-submodule-and-java-facade.md) 与 [ADR-0005](0005-browser-automation-dshbox-browser-stack.md) 的剩余部分；
  `docs/MIGRATION.md`、[模拟器证据](../../migration/parity/2026-11-15-emulator-A17-x86_64-pr10-remove-dshbox.md)

## 背景

ADR-0017 之后，App 的默认路径已不调用旧桥：所有切片都由 C# 决策，旧实现只剩「长按回退」与回退时的影子比对。仍随仓库 / APK 的 DSHBox
部分只服务于这些回退和浏览器：`third_party/dshbox` 子模块、`native/` 下的 Gradle 构建与 Bridge AAR（`DshNativeFacade`、`RuntimeController.kt`、
DSHBox sandbox-manager、旧终端、旧 Relay 与其清单组件、浏览器栈）、`DshMobile.NativeBinding`、zstd-jni、`DshProotSource=dshbox` 开关。
测试侧还有向 `third_party/` 与 `native/native-bridge` 源码做契约扫描的测试，以及跑旧 Kotlin 的 `:native-bridge:testDebugUnitTest`。

AGENTS.md 规则 3 要求「新实现通过同一向量 + 真机验收后才能成为默认；之后才删除该切片的 DSHBox 依赖」，规则 9 要求兼容性声明附真机证据。
ADR-0016 / 0017 已先于真机验收翻转默认；删除旧实现意味着回退入口一并消失。

## 决策

1. **一次性移除 DSHBox 依赖**（所有迁移 PR 已完成，符合负责人「所有 PR 都完成再移除」）：
   - 删除子模块 `third_party/dshbox` 与 `.gitmodules`；删除 `native/native-bridge`、`native/build.gradle.kts` / `settings.gradle.kts` /
     `gradle.properties` / Gradle wrapper；`native/build-native.*` 只构建 `dshmobile-native` 与 `proot`。
   - 删除 `src/DshMobile.NativeBinding`、`src/DshMobile.Core/Bridge/*`（`BridgeClient`、`BridgeErrorCodes`、`BridgeMethods`、协议 DTO）、
     `IRuntimeManager` / `LegacyBridgeRuntimeManager`、`SliceMode` / `MigrationOptions`、全部影子检查（只保留导入的装后树核对
     `runtime.layer-install`，它比对 C# 自己的安装结果，不依赖旧实现）、旧终端 `TerminalActivity`、`NativeFacadeTransport`。
   - `RuntimeCoordinator` 只剩 C# 路径（构造函数要求 `RuntimeLayout`）；`BridgeHost` 类名沿用但不再含旧桥；所有「长按 → 旧实现（回退）」入口删除，
     「导入」长按 = 回滚、「诊断」长按 = PTY 自检保留。
   - 仍被 C# 使用的通用协议类型移到 `DshMobile.Core.Contracts`（`ProbeResult`、`RemoteHost*`、`DshProfiles` 等）；`RuntimeErrorCodes` 增加 `BAD_REQUEST`。
2. **guest 资产与清单组件并入 App**：
   - APK 资产 `dshbox/link-shim.mjs`、`dshbox/libdshbox-link.so`、`toolchain-cache/corepack-pnpm-11.1.2.tar.zst(.sha256)` 从 DSHBox v1.5.1
     逐字节复制到 `src/DshMobile.Android/Assets/`（来源与 SHA-256 见该目录 README，测试钉住哈希与文件清单）；`libdshbox-link.so` 的源码与构建脚本
     原样复制到 `native/link-shim/` 以便追溯（不在本仓库构建，需 aarch64 glibc 工具链）。
   - 本仓库自有 guest 脚本移出旧桥：`ui-facade.mjs` / `ui-facade-check.mjs` → `src/DshMobile.Runtime/Automation/Assets/`，`dsh-probe.mjs` →
     `src/DshMobile.Runtime/Guest/Assets/`，guest 端 node 测试 → `tests/guest/`。
   - 旧 AAR 合并进清单、而 C# 仍需要的条目改由 App 清单声明：Relay 各后端用到的运行时权限、`<queries>`、`rikka.shizuku.ShizukuProvider`。
     合并后清单与 PR-10 之前逐项比对：只少了浏览器、旧终端、旧 Relay 的组件与 `BROWSER_INTERNAL` 权限及多出的 `MonoRuntimeProvider_1`（`:browser` 进程）。
   - 自建 PRoot 成为唯一来源：`DshProotSource` 开关删除，App 项目直接打包 `artifacts/native/proot/<abi>/*.so`，缺失即构建失败。
3. **浏览器 `browser_*` 不迁移，删除**（负责人：「不需要浏览器」）。旧 `BrowserTools.kt`、DSHBox browser-api / devtools / runtime / bridge、
   browser-use 插件、`browser-check.mjs`、浏览器页面与 `:browser` 进程随旧桥删除，`Xamarin.AndroidX.WebKit` 不再需要。`ui_*` 仍是唯一的设备工具
   命名空间；ADR-0005 作废。
4. **跳过真机验收**（负责人：「跳过真机」）。本次删除及其前置的 ADR-0016 / 0017 翻转只有单测 + Android 17 x86_64 模拟器冒烟证据；
   模拟器无法运行 arm64 guest（PRoot 内 signal 4），所以 guest 内的成功路径（DSH READY、插件安装、`ui_*` 自检、探测结果）在本次没有设备证据。
   这是对 AGENTS.md 规则 3 / 9 的**有意偏离**，记录于此；`docs/MIGRATION.md` 每个切片的状态都标注「真机验收由负责人决定跳过」。
5. **测试改为钉值**（规则 7：上游路径只可在过渡期的构建与测试中引用；子模块删除后无从引用）：
   - 删除跑旧实现或扫描旧源码的测试：`LegacySourceContractTests*`、`LegacyBridgeRuntimeManagerTests`、Core 的 `BridgeClient` / `BridgeCodec` /
     `CrossLanguageContractTests`、全部影子检查测试、`tests/DshMobile.TestTarget`（只为旧 Relay 宿主影子服务）。
   - 原先从上游源码扫出的值改为字面量，并注明来源 commit（DSHBox `7c9d33c`、旧桥 `128c2ea`）：`UpstreamPinnedValuesTests`（常量、目录布局、
     插件正则、guest 路径、远程主机文案、SSH 别名 / 算法、APK 资产哈希）、诊断命令表、终端 `/proc` 伪文件文本、`relay-client.cjs` 与
     `USAGE_TEMPLATE`（经 Kotlin `trimIndent` 语义）的 SHA-256、`dsh` / `pnpm` 包装文本、`relay.selftest` 命令。
   - `tests/TestVectors/*.json` 保留：其期望值是 PR-10 之前新旧双绿的结果，继续约束 C#。
6. **CI 只用 .NET + NDK**：去掉子模块检出与 Java / Gradle，`sdkmanager` 安装 `ndk;27.0.12077973` 后运行 `native/build-native.ps1`，
   再跑三套单测、`tests/guest` 的 node 测试与 App 构建。

## 不变 / 不做

- 不改上游 AgentLoop / Session Log / Cordis core。
- 权限审批（`ui_*` 写操作逐次审批、确认页一次性令牌）、无障碍写门禁、SSH 主机密钥固定、Keystore 不可导出私钥均不变；guest 命令一律 argv + 逐 token 引号。
- 运行层仍取自 DSHBox **Release 发布物**（`runtime-bundle/runtime-assets.lock` 钉 SHA-256，`fetch-runtime.ps1` 下载）：这是二进制输入而非源码依赖；
  更换来源需另立 ADR。
- 已登记的 GPL-3.0 移植（`docs/UPSTREAMS.md`「C# 移植登记」）保留来源注释；文件头中「Ported from … native-bridge …」指向的路径可在 `128c2ea` 及之前的历史中查到。

## 后果

- 仓库不再需要 JDK / Gradle / Kotlin；构建只需 .NET SDK（`global.json`）与 NDK 27。APK 不再含 DSHBox 代码、zstd-jni、`:browser` 进程。
- 失去回退：C# 实现若在真机上出问题，只能修 C#（或回到 `128c2ea` 之前的版本）；影子报告文件只剩 `runtime.layer-install`。
- 测试数量下降（删除的是旧实现 / 影子测试）：Core 35、Runtime 728、Application 82、guest node 37，均通过。
- 冒烟中发现并修正：测试替身 `IBundledAssets` 未按接口约定在缺失时抛 `IOException`（接口文档已写明）；首页 / 远程页残留的「旧实现回退」
  「浏览器」文案；只服务于浏览器品牌覆盖的 5 个语言目录；48 个不再使用的字符串。
- 风险：见决策 4。发布前应在 arm64 真机至少跑一遍：导入 → 准备 → 启动 → WebUI → 停止无孤儿、终端、插件安装、`ui_*` 审批、远程 SSH。
