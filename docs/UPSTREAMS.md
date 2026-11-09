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
| `common` | **已引入**（TASK-003） | `native/settings.gradle.kts` 以 `:dshbox-common` 引用源码目录，构建输出重定向到 `native/build/` | 否（`Bind="false"`） |
| `sandbox-manager` | 计划 TASK-004 | — | 否 |
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
| — | — | — | 目前无修改；子模块保持原样 |

## 同步记录

| 日期 | 上游 | 从 | 到 | 说明 / 兼容性结论 |
|---|---|---|---|---|
| 2026-11-09 | DSHBox | — | `v1.5.1` (`7c9d33c`) | 初次引入；仅使用 `common` 模块 |
