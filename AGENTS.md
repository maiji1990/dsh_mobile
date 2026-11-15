# AGENTS.md — DSH Mobile 实施约束（AI / 人类开发者共用）

完整方案见 [docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md](docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md)。
**当前长期路线：[ADR-0007 C#-first 源码归属](docs/ADR/0007-source-ownership-csharp-first.md)**（取代 ADR-0002）；迁移状态唯一事实源：[docs/MIGRATION.md](docs/MIGRATION.md)。
**PR-10（[ADR-0018](docs/ADR/0018-remove-dshbox.md)）起 DSHBox 子模块、Bridge AAR / Gradle 构建、Java Facade 与 Kotlin 旧桥均已删除，App 只有 C# 实现；浏览器 `browser_*` 不迁移、已删除。**

## 开工前必读

1. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)、[docs/MIGRATION.md](docs/MIGRATION.md)、[docs/UPSTREAMS.md](docs/UPSTREAMS.md)、[docs/SECURITY.md](docs/SECURITY.md)、[docs/FEATURES.md](docs/FEATURES.md)。
2. 涉及架构取舍时先看 [docs/ADR/](docs/ADR/)，重大变更先写新 ADR。

## 硬性规则

1. **不重写 DSH 内核**：不得修改或移植上游 DeepSeek Harness 的 AgentLoop、Session Log、Cordis core。能力通过 Cordis 插件 / MCP / 官方 profile 扩展。
2. **C#-first（ADR-0007）**：业务逻辑、状态机、权限策略、进程监管写在 C#（`src/DshMobile.*`）。**禁止重新引入** DSHBox AAR、Java Facade、Kotlin 业务层或 Gradle 构建（ADR-0018 已删除）。调用 Android Java API 用 .NET for Android 标准互操作；必须原生的能力（PRoot、PTY、进程组、ELF 启动）放进本仓库可追溯的最小 NDK 层（`DshMobile.Native` + P/Invoke）；只能在非 .NET 进程里运行的部分（如 Shizuku 用户服务，ADR-0009）保持为最小、可追溯的 Java 类。
3. **最小垂直切片 + 共享向量**：一次只改一个完整功能切片。行为由 `tests/TestVectors/*.json` 锁定（其期望值是 PR-10 前新旧实现双绿的结果）；改变已锁定的行为必须同时改向量并在 PR 中说明理由。新实现成为默认前需通过同一向量 + 真机验收（ADR-0016 / 0017 / 0018 的翻转与删除由负责人决定先于真机验收，见 ADR-0018 决策 4）。
4. **品牌**：禁止复制 DSHBox 名称、Logo、图标、Splash 等品牌资产（见上游 DSHBox `v1.5.1` 的 `TRADEMARK.md`）；从 DSHBox 翻译或原样复制的代码 / 文件保留上游路径 / commit / GPL-3.0 声明并登记到 `docs/UPSTREAMS.md`。
5. **不伪造成功**：系统限制返回稳定错误码（`RuntimeErrorCodes`、`NativeErrorCodes`、上游 `BUNDLE_*` 等）并在 UI 可见；禁止为“看似支持”返回假成功；测试 0 用例不算通过。
6. **`ui_*` 单一契约**：手机自动化沿用 `maiji1990/ui_mcp` 契约与单写者规则，不并行造第二套工具。`ui_*` 是唯一对 Agent 暴露的设备工具命名空间（`browser_*` 已随 ADR-0018 删除）。
7. **上游文件**：仓库不再包含上游源码树；生产代码与测试都不得引用上游仓库路径。原样复制进本仓库的上游文件（`src/DshMobile.Android/Assets/dshbox`、`toolchain-cache`、`native/link-shim`、Relay guest 资产等）只放在已登记位置，并由测试钉住 SHA-256；原先向上游源码扫描的契约改为钉值测试（`UpstreamPinnedValuesTests` 等），注明来源 commit。
8. **上游升级**：先做兼容性对照与变更日志，单独 PR；更新 `docs/UPSTREAMS.md` 与相应钉值。
9. **真机证据**：Android 17 / 厂商 ROM 兼容性不能仅凭单元测试或模拟器宣称通过，必须附真机日志或报告；没有真机证据的切片在 `docs/MIGRATION.md` 中必须如实标注（ADR-0018 记录了负责人决定跳过的部分）。
10. **版本锁定**：.NET SDK/workload 由 `global.json` 锁定；NDK 固定 `27.0.12077973`；NuGet / Maven（`AndroidMavenLibrary`）固定精确版本；原生上游源码由 `native/proot/sources.lock` 钉 SHA-256；禁止在发布环节使用 `latest`。
11. **安全不得削弱**：不得削弱设备权限审批、Accessibility 写操作门禁、SSH 主机密钥固定或不可导出私钥（不得导出私钥到文件、不得关闭主机密钥校验）。guest 命令一律由 argv 构建并逐 token 引号（`GuestCommandLine`），禁止把调用方输入拼进 shell 字符串。
12. **UI 用 Material 3**：新写或迁移的原生界面使用 Material Components（Material 3 主题与组件，`Xamarin.Google.Android.Material`）；主题替换作为独立 UI 切片并附截图级真机验收。

## 构建

```bash
pwsh native/build-native.ps1      # 或 bash native/build-native.sh：NDK 27 构建 libdshmobile_native + 自建 PRoot → artifacts/native/
dotnet build DshMobile.slnx -c Debug
dotnet test tests/DshMobile.Core.Tests
dotnet test tests/DshMobile.Runtime.Tests
dotnet test tests/DshMobile.Application.Tests
node tooling/run-node-tests.mjs tests/guest
```

运行层（DSH / Debian rootfs）：`pwsh runtime-bundle/fetch-runtime.ps1`（按 `runtime-bundle/runtime-assets.lock` 校验 SHA-256）。

## 分层（ADR-0007）

```text
DshMobile.Android      Activity / Service / 平台适配（IForegroundServiceController、AndroidBundledAssets、C# Relay 组合根等）
DshMobile.Application  编排与策略：RuntimeCoordinator（状态机 + journal + 宿主 / 导入 / 准备 / 回滚）、终端会话、DI 组装
DshMobile.Runtime      IGuestProcess / DshHost、RuntimeState 状态机 + journal、层校验 / 安装、Relay、远程 SSH
DshMobile.Native       最小原生边界：W^X 可执行策略、libdshmobile_native P/Invoke
DshMobile.Core         通用协议类型（`DshMobile.Core.Contracts`）、WebView 策略、工作区文件系统
```

## 跨进程 / 数据约定

- 高吞吐数据（PTY、截图、大文件）使用 FD / stream / 原生 View，不经 JSON 控制面。
- 后台线程的结果不得阻塞 UI 线程等待。
