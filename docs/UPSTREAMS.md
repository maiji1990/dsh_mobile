# 上游来源登记（UPSTREAMS）

所有上游均固定到具体 commit；**不得追随浮动分支**。升级必须单独 PR，并在下方“同步记录”追加一行。

## 固定版本

| 上游 | 引入方式 | 固定版本 | Commit SHA | 许可证 | 用途 |
|---|---|---|---|---|---|
| [WSK-build/DSHBox](https://github.com/WSK-build/DSHBox) | git submodule `third_party/dshbox`（shallow） | `v1.5.1` | `7c9d33c1f29dd2c42dde6c7e2fcdf38814f491c4` | GPL-3.0（品牌资产保留，见其 `TRADEMARK.md`）；子模块各自许可见其 `licenses/` | 运行时、终端、Relay 等原生组件来源 |
| [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness) | 运行时层（npm 包，TASK-004/005 引入） | 待定（最新 tag 观察到 `dsh-v0.2.2-rc.1`，尚无稳定 tag） | — | 见上游 | 原样运行的 DSH / Cordis |
| [maiji1990/ui_mcp](https://github.com/maiji1990/ui_mcp) | 契约参考（TASK-010 引入） | 待定（`main` 当前 `e098f91c7d34f27372ced17bea033861b63b681e`，仅记录未引入） | — | 见上游 | `ui_*` 统一契约 |

## DSHBox 模块使用情况

| 模块 | 当前状态 | 引入位置 | 是否生成 C# 绑定 |
|---|---|---|---|
| `common` | **已引入**（TASK-003） | `native/settings.gradle.kts` 以 `:common`（与 DSHBox 同名，其脚本按此引用）引用源码目录，构建输出重定向到 `native/build/upstream/` | 否（`Bind="false"`） |
| `app/src/main/jniLibs`（PRoot、talloc、android-shmem、zstd-jni 预编译 `.so`） | **已引入**（TASK-004） | `:native-bridge:exportRuntimeJars` 复制到 `artifacts/native/jniLibs/` | — |
| Release 运行层（DSH `0.2.1-rc.5.tar.zst`、`dshapp-runtime-debian-arm64-0.1.0.zip`） | **已引入**（TASK-004） | `runtime-bundle/runtime-assets.lock`（GitHub 发布摘要 SHA-256）+ `fetch-runtime.ps1` | — |
| `plugin-manager/dshbox-plugins/assets/dshbox/{link-shim.mjs, libdshbox-link.so}` | **已引入**（TASK-005） | 构建时原样复制进 Bridge AAR 资产（仅这两个文件，**不含** DSHBox `assets/plugins/*`：mobile-pilot 等与 `ui_*` 单一契约冲突） | — |
| `sandbox-manager` | **已引入**（TASK-004） | `native/settings.gradle.kts` 以 `:sandbox-manager` 引用源码目录 | 否（`Bind="false"`） |
| `terminal-*` | 计划 TASK-008 | — | 否（仅 View 适配） |
| `pilot/interlock-relay-core` | 计划 TASK-010（优先使用 Apache-2.0 独立仓库） | — | 否 |
| `browser/*` | 计划 TASK-011 | — | 否 |
| `app`、`pilot/dshbox-adapter`、`plugin-manager` UI | **不引入**（重建为 C#） | — | — |

## 构建工具链来源

- Gradle Wrapper 脚本与 `gradle-wrapper.jar` 复制自 DSHBox `v1.5.1`（Gradle 项目原件，Apache-2.0）；分发版本 `gradle-9.5.0-bin`。本仓库副本额外加了 `distributionSha256Sum=553c78f5…6b746`（与 services.gradle.org 官方 `.sha256` 一致，2026-11-09 核对）。
- AGP / Kotlin / AndroidX 版本**直接读取** `third_party/dshbox/gradle/libs.versions.toml`（单一事实源，不复制第二份）。当前：AGP 9.3.1、Kotlin 2.4.20、compileSdk 36、minSdk 29。

## 本仓库对上游的修改（patch 登记）

| 日期 | 上游 | 文件 | 修改理由 |
|---|---|---|---|
| 2026-11-09 | DSHBox | （未修改子模块）`SandboxService.provisionBundledDsh` 逻辑移植进 `RuntimeController.kt` | 该函数位于 DSHBox `app` 模块（我们不引入 app）；同语义、去 UI 副作用，GPL-3.0 注明来源 |
| 2026-11-09 | DSHBox | （未修改子模块）`SandboxService.provisionDshLinkShim` 语义移植进 `RuntimeController.provisionGuestAssets` | 每次进程启动覆写垫片到 `files/dshbox`，与 DSHBox 一致 |
| 2026-11-09 | DSHBox | （未修改子模块）导入前创建 `runtime/runtime-current/` | 规避上游全新安装离线导入的 rename 失败 → `copyRecursively` 卡在悬空 `/var/run` 符号链接的问题；建议向上游报告 |

## 同步记录

| 日期 | 上游 | 从 | 到 | 说明 / 兼容性结论 |
|---|---|---|---|---|
| 2026-11-09 | DSHBox | — | `v1.5.1` (`7c9d33c`) | 初次引入；仅使用 `common` 模块 |
| 2026-11-09 | DSHBox | `v1.5.1` | `v1.5.1` | 新增使用 `sandbox-manager`、预编译 `.so` 与 v1.5.1 Release 运行层；Maven 运行时依赖审计清单见 `artifacts/native/jars/MANIFEST.txt`（Kotlin/AndroidX 由 NuGet 提供） |
