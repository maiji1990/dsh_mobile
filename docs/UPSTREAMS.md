# 上游来源登记（UPSTREAMS）

所有上游均固定到具体 commit；**不得追随浮动分支**。升级必须单独 PR，并在下方“同步记录”追加一行。

## 固定版本

| 上游 | 引入方式 | 固定版本 | Commit SHA | 许可证 | 用途 |
|---|---|---|---|---|---|
| [WSK-build/DSHBox](https://github.com/WSK-build/DSHBox) | git submodule `third_party/dshbox`（shallow） | `v1.5.1` | `7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4` | GPL-3.0（品牌资产保留，见其 `TRADEMARK.md`）；子模块各自许可见其 `licenses/` | 运行时、终端、Relay 等原生组件来源 |
| [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) | 运行时层（npm 包，TASK-004/005 引入） | 待定（最新 tag 观察到 `dsh-v0.2.2-rc.1`，尚无稳定 tag） | — | 见上游 | 原样运行的 DSH / Cordis |
| [maiji1990/ui_mcp](https://github.com/maiji1990/ui_mcp) | 契约参考（TASK-010；不引入代码，guest 门面按其契约另行实现） | `main` @ `e098f91c7d34f27372ced17bea033861b63b681e`（工具名、错误码、ref 语义、`docs/p2-phone-facade-design.md`） | `e098f91c7d34f27372ced17bea033861b63b681e` | 见上游 | `ui_*` 统一契约 |

## DSHBox 模块使用情况

| 模块 | 当前状态 | 引入位置 | 是否生成 C# 绑定 |
|---|---|---|---|
| `common` | **已引入**（TASK-003） | `native/settings.gradle.kts` 以 `:common`（与 DSHBox 同名，其脚本按此引用）引用源码目录，构建输出重定向到 `native/build/upstream/` | 否（`Bind="false"`） |
| `app/src/main/jniLibs`（PRoot、talloc、android-shmem、zstd-jni 预编译 `.so`） | **已引入**（TASK-004） | `:native-bridge:exportRuntimeJars` 复制到 `artifacts/native/jniLibs/` | — |
| Release 运行层（DSH `0.2.1-rc.5.tar.zst`、`dshapp-runtime-debian-arm64-0.1.0.zip`） | **已引入**（TASK-004） | `runtime-bundle/runtime-assets.lock`（GitHub 发布摘要 SHA-256）+ `fetch-runtime.ps1` | — |
| `plugin-manager/dshbox-plugins/assets/dshbox/{link-shim.mjs, libdshbox-link.so}` | **已引入**（TASK-005） | 构建时原样复制进 Bridge AAR 资产（仅这两个文件；`assets/plugins/*` 中只另取 `browser-use`（TASK-011，`browser_*` 独立命名空间），mobile-pilot / mobile-adapt 与 `ui_*` 单一契约冲突，不取） | — |
| `dsh-renderer/api` | **已引入**（TASK-006） | `native/settings.gradle.kts` 以 `:dsh-renderer:api` 引用；使用 `DshOriginPolicy`、`DshTokenRevisionTracker` | 否（`Bind="false"`） |
| `sandbox-manager` | **已引入**（TASK-004） | `native/settings.gradle.kts` 以 `:sandbox-manager` 引用源码目录 | 否（`Bind="false"`） |
| `terminal-emulator` / `terminal-view` / `terminal-session` | **已引入**（TASK-008） | `native/settings.gradle.kts` 同名项目；`terminal-emulator` 的 `termux.c` 由 ndk-build 从源码构建（NDK 27.0.12077973，与 DSHBox 根构建同一锁定） | 否（`Bind="false"`；C# 只拿到 `android.view.View`） |
| `pilot/interlock-relay-core` | **已引入**（TASK-010，ADR-0004；未修改） | `native/settings.gradle.kts` 以 `:pilot:interlock-relay-core` 引用源码目录；宿主 SPI 在 `MobileAutomation.kt` / `RelayApproval.kt`；能力白名单只读 2 项 | 否（`Bind="false"`） |
| `browser/browser-api` / `browser-devtools` / `browser-runtime` / `browser-bridge` | **已引入**（TASK-011，ADR-0005；未修改） | `native/settings.gradle.kts` 以 `:browser:*`（与 DSHBox 同名）引用源码目录；`androidx.webkit` 由 NuGet `Xamarin.AndroidX.WebKit` 1.12.1.2 提供；唯一品牌字符串 `browser_collapse` 由 App 资源覆盖 | 否（`Bind="false"`） |
| `plugin-manager/dshbox-plugins/assets/plugins/browser-use`（Cordis 插件，GPL-3.0，24 个 `browser_*` 工具 + install/uninstall 脚本） | **已引入**（TASK-011，整目录原样复制进 Bridge AAR 资产，用户开启时才安装到 web profile） | `stageDshboxShimAssets` | — |
| `app`、`pilot/dshbox-adapter`、`plugin-manager` UI | **不引入**（重建为 C#） | — | — |

## 其它运行时依赖（非 DSHBox）

| 依赖 | 版本 | 许可证 | 用途 | 引入 |
|---|---|---|---|---|
| `com.github.mwiede:jsch` | 2.28.8（2026-10-10 发布） | Revised BSD / ISC | 远程主机 SSH 客户端（ADR-0006） | TASK-012；导出白名单 jar |
| `Xamarin.AndroidX.WebKit`（NuGet） | 1.12.1.2 | MIT（绑定）/ Apache-2.0（androidx.webkit 1.12.1） | DSHBox browser-runtime 依赖 | TASK-011 |

## 构建工具链来源

- Gradle Wrapper 脚本与 `gradle-wrapper.jar` 复制自 DSHBox `v1.5.1`（Gradle 项目原件，Apache-2.0）；分发版本 `gradle-9.5.0-bin`。本仓库副本额外加了 `distributionSha256Sum=553c78f5…6b746`（与 services.gradle.org 官方 `.sha256` 一致，2026-11-09 核对）。
- AGP / Kotlin / AndroidX 版本**直接读取** `third_party/dshbox/gradle/libs.versions.toml`（单一事实源，不复制第二份）。当前：AGP 9.3.1、Kotlin 2.4.20、compileSdk 36、minSdk 29。

## 本仓库对上游的修改（patch 登记）

| 日期 | 上游 | 文件 | 修改理由 |
|---|---|---|---|
| 2026-11-09 | DSHBox | （未修改子模块）`SandboxService.provisionBundledDsh` 逻辑移植进 `RuntimeController.kt` | 该函数位于 DSHBox `app` 模块（我们不引入 app）；同语义、去 UI 副作用，GPL-3.0 注明来源 |
| 2026-11-09 | DSHBox | （未修改子模块）`SandboxService.provisionDshLinkShim` 语义移植进 `RuntimeController.provisionGuestAssets` | 每次进程启动覆写垫片到 `files/dshbox`，与 DSHBox 一致 |
| 2026-11-09 | DSHBox | （未修改子模块）`app/.../webview/DshUrl.kt` 的 `dshUrlWithToken` 移植进 `WebUiController.urlWithToken` | 位于 DSHBox app 模块；同编码规则，加单测 |
| 2026-11-10 | DSHBox | （未修改子模块）`SandboxService.provisionCorepackCache`、`DSH_CLI_SHIM`、`PNPM_CLI_SHIM` 移植为 `GuestToolchain.kt`；随包 `toolchain-cache/corepack-pnpm-11.1.2.tar.zst` 原样打包 | 让 `dsh plugin` 有离线 pnpm；未移植 apt/dpkg 包装（需 fakeroot 配置）与 opencode 包装 |
| 2026-11-10 | DSHBox | （未修改子模块）`app/.../runtime/GuestUserProvisioner.kt` 移植为 `GuestUser.kt` | 用户名由 DSHBox 品牌名改为 `dshmobile`（AGENTS.md 规则 4）；其余逻辑一致，加单测 |
| 2026-11-10 | DSHBox | （未修改子模块）`DshTerminalViewClient` / `DshTerminalSessionClient` 语义并入 `TerminalController.kt` | 位于 DSHBox app 模块（Compose 屏幕的回调），不引入 app |
| 2026-11-09 | DSHBox | （未修改子模块）导入前创建 `runtime/runtime-current/` | 规避上游全新安装离线导入的 rename 失败 → `copyRecursively` 卡在悬空 `/var/run` 符号链接的问题；建议向上游报告 |
| 2026-11-10 | — | （本仓库）`RuntimeController.kt` 插件命令改为 argv + `shellJoin` 逐 token 单引号 | PR-00 安全修复：`PACKAGE_SPEC` 允许的 `>=` 曾被 shell 当重定向 |

## C# 移植登记（ADR-0007）

从 DSHBox（GPL-3.0-or-later）**翻译**为 C# 的代码。文件头注明上游路径与 commit；行为以 `tests/TestVectors` 共享向量 + 上游源码契约扫描（`LegacySourceContractTests`）锁定，上游升级时两者先失败。

| 日期 | 上游文件（DSHBox `v1.5.1` `7c9d33c`） | C# 位置 | 移植范围 | 验证 |
|---|---|---|---|---|
| 2026-11-10 | `common/.../Versions.kt` | `src/DshMobile.Runtime/Versions.cs` | 全部（含 Kotlin `toIntOrNull` 语义） | `versions.json` 新旧双跑 |
| 2026-11-10 | `common/.../Constants.kt`（仅 guest 路径 / 端点 / 标记等运行时事实） | `src/DshMobile.Runtime/RuntimeConstants.cs` | 子集；DSHBox 偏好键、品牌相关项不移植 | 源码扫描逐项比对 |
| 2026-11-10 | `sandbox-manager/.../SandboxConfig.kt`（目录布局） | `src/DshMobile.Runtime/RuntimeLayout.cs` | 目录布局；provider 回调不移植 | 源码扫描逐项比对 |
| 2026-11-10 | `sandbox-manager/.../SandboxState.kt` | `src/DshMobile.Runtime/Legacy/LegacyStateMapper.cs` | 枚举映射（非移植） | 源码扫描：每个上游值必须有映射 |
| 2026-11-10 | `sandbox-manager/.../RuntimeBundleLayout.kt` | `src/DshMobile.Runtime/Bundle/RuntimeBundleLayout.cs` | 全部 | `runtime-bundle-layout.json` 新旧双跑 |
| 2026-11-10 | `sandbox-manager/.../RuntimeProfile.kt` | `src/DshMobile.Runtime/Bundle/RuntimeProfile.cs` | 全部（`System.Text.Json` 源生成） | 单测 + 真实锁定包 |
| 2026-11-10 | `sandbox-manager/.../DefaultSandboxManager.kt` `importRuntimeBundle` / `extractStagedLayers` 的**校验段**；`BundleManager.verifySha256` | `src/DshMobile.Runtime/Bundle/RuntimeBundleInspector.cs` | 只读校验（顺序与 `BUNDLE_*` 错误码一致）；解包 / 换层未移植 | 单测 12 例 + 真机影子比对（待采集） |
| 2026-11-11 | `sandbox-manager/.../BundleManager.kt` `extractTarGz` / `applyEntryMode` / `isWithinRoot` / `is*File` 魔数识别 | `src/DshMobile.Runtime/Layers/LayerExtractor.cs` | 解包语义全部（受保护路径、规范路径越界、链接校验、硬链接延后、属主权限位、commons-compress 类型分派）；bzip2 / xz 容器未移植 | `layer-extract.json` 新旧双跑（夹具由 `tooling/gen-layer-fixtures.py` 生成）+ 真实锁定包干跑 + 真机影子（待采集） |
| 2026-11-11 | `sandbox-manager/.../DefaultSandboxManager.kt` `importRuntimeBundle`（zip 分支）/ `extractStagedLayers` / `swapRuntimeBody` / `writeFileAtomically` | `src/DshMobile.Runtime/Layers/LayerInstaller.cs` | zip 导入全流程（staging 改用独立目录 `dshmobile-bundle-staging`；解包期间重算压缩流 SHA-256；换层失败的复制回退保留符号链接）；tar 快照布局（非 zip）未移植 | `install-scenarios.json` 新旧双跑 + 真机（见 COMPATIBILITY） |

## C# 运行时依赖（NuGet）

| 包 | 版本 | 许可证 | 用途 | 引入 |
|---|---|---|---|---|
| `ZstdSharp.Port` | 0.8.8（2026-04-29 发布） | MIT | 运行时层 `.tar.zst` 解压（.NET 11 无内置 Zstandard）；纯托管 | PR-02 解包段 |
| `Microsoft.Extensions.DependencyInjection` | 10.0.14 | MIT | Android 组合根 | PR-01 |

## 同步记录

| 日期 | 上游 | 从 | 到 | 说明 / 兼容性结论 |
|---|---|---|---|---|
| 2026-11-09 | DSHBox | — | `v1.5.1` (`7c9d33c`) | 初次引入；仅使用 `common` 模块 |
| 2026-11-09 | DSHBox | `v1.5.1` | `v1.5.1` | 新增使用 `sandbox-manager`、预编译 `.so` 与 v1.5.1 Release 运行层；Maven 运行时依赖审计清单见 `artifacts/native/jars/MANIFEST.txt`（Kotlin/AndroidX 由 NuGet 提供） |
