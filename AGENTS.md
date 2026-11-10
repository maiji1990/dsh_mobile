# AGENTS.md — DSH Mobile 实施约束（AI / 人类开发者共用）

完整方案见 [docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md](docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md)。
**当前长期路线：[ADR-0007 C#-first 源码归属](docs/ADR/0007-source-ownership-csharp-first.md)**（取代 ADR-0002）；迁移状态唯一事实源：[docs/MIGRATION.md](docs/MIGRATION.md)。

## 开工前必读

1. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)、[docs/MIGRATION.md](docs/MIGRATION.md)、[docs/UPSTREAMS.md](docs/UPSTREAMS.md)、[docs/SECURITY.md](docs/SECURITY.md)、[docs/FEATURES.md](docs/FEATURES.md)。
2. 涉及架构取舍时先看 [docs/ADR/](docs/ADR/)，重大变更先写新 ADR。

## 硬性规则

1. **不重写 DSH 内核**：不得修改或移植上游 DeepSeek Harness 的 AgentLoop、Session Log、Cordis core。能力通过 Cordis 插件 / MCP / 官方 profile 扩展。
2. **C#-first（ADR-0007）**：业务逻辑、状态机、权限策略、进程监管写在 C#（`src/DshMobile.*`）。**禁止新增**对 DSHBox AAR、`DshNativeFacade` / Java Facade、Kotlin 业务层的长期依赖；现有桥只做过渡实现与比对基线，除修安全缺陷外不再给它加需求。调用 Android Java API 用 .NET for Android 标准互操作；必须原生的能力（PRoot、PTY、进程组、ELF 启动）放进本仓库可追溯的最小 NDK 层（`DshMobile.Native` + P/Invoke）。
3. **最小垂直切片 + 影子比对**：一次只替换一个完整功能切片。新实现先以 `SliceMode.LegacyWithShadow` 运行（旧实现决策，C# 只读比对），新旧实现必须跑同一套 `tests/TestVectors/*.json`。新实现通过同一向量 + 真机验收后才能成为默认；**之后**才删除该切片的 DSHBox 依赖。禁止一次性删除 `third_party/dshbox`（PR-10 才移除）。
4. **品牌**：禁止复制 DSHBox 名称、Logo、图标、Splash 等品牌资产（见 `third_party/dshbox/TRADEMARK.md`）；从 DSHBox 翻译的代码保留上游路径 / commit / GPL-3.0 声明并登记到 `docs/UPSTREAMS.md`。
5. **不伪造成功**：系统限制返回稳定错误码（`BridgeErrorCodes`、`RuntimeErrorCodes`、`NativeErrorCodes`、上游 `BUNDLE_*` 等）并在 UI 可见；禁止为“看似支持”返回假成功；测试 0 用例不算通过。
6. **`ui_*` 单一契约**：手机自动化沿用 `maiji1990/ui_mcp` 契约与单写者规则，不并行造第二套工具。`ui_*` / `browser_*` 是唯一对 Agent 暴露的设备工具命名空间。
7. **上游路径不硬编码**：生产代码不得读取 `third_party/` 内部文件；只有过渡期的 Gradle 构建与**测试**（向量 / 源码契约扫描）可以引用它。
8. **上游升级**：先做兼容性对照与变更日志，单独 PR；更新 `docs/UPSTREAMS.md`。
9. **真机证据**：Android 17 / 厂商 ROM 兼容性不能仅凭单元测试宣称通过，必须附真机日志或报告；影子比对结果（`shadow-reports.jsonl`）是切片验收证据的一部分。
10. **版本锁定**：.NET SDK/workload 由 `global.json` 锁定；Gradle/AGP/Kotlin 跟随 DSHBox 锁定的 `libs.versions.toml`（直至 PR-10）；NuGet 固定精确版本；禁止在发布环节使用 `latest`。
11. **安全不得削弱**：不得为迁移而削弱设备权限审批、Accessibility 写操作门禁、SSH 主机密钥固定或不可导出私钥（不得导出私钥到文件、不得关闭主机密钥校验）。guest 命令一律由 argv 构建并逐 token 引号（`GuestCommandLine` / Kotlin `shellJoin`），禁止把调用方输入拼进 shell 字符串。
12. **UI 用 Material 3**：新写或迁移的原生界面使用 Material Components（Material 3 主题与组件，`Xamarin.Google.Android.Material`）；主题替换作为独立 UI 切片并附截图级真机验收。

## 构建

```bash
git submodule update --init --depth 1
pwsh native/build-native.ps1      # 过渡期：或 bash native/build-native.sh → 产出 artifacts/native/*.aar（含旧 Kotlin 单测）
dotnet build DshMobile.slnx -c Debug
dotnet test tests/DshMobile.Core.Tests
dotnet test tests/DshMobile.Runtime.Tests
dotnet test tests/DshMobile.Application.Tests
node tooling/run-node-tests.mjs native/native-bridge/src/test/guest
```

## 分层（ADR-0007）

```text
DshMobile.Android      Activity / Service / 平台适配（IForegroundServiceController 等）
DshMobile.Application  编排与策略：RuntimeCoordinator、SliceMode、影子比对、DI 组装
DshMobile.Runtime      IRuntimeManager / IGuestProcess、RuntimeState 状态机 + journal、层校验、Legacy 适配器
DshMobile.Native       最小原生边界：W^X 可执行策略、libdshmobile_native P/Invoke
DshMobile.Core         协议 DTO、错误码（含旧桥协议，PR-10 后只保留通用部分）
```

## 过渡期跨语言桥接约定（旧实现，PR-10 移除）

- 控制面：`DshNativeFacade.execute(requestJson, callback)`，协议见 [docs/PROVIDER_CONTRACTS.md](docs/PROVIDER_CONTRACTS.md)；C# 侧经 `LegacyBridgeRuntimeManager` 等适配器使用，新代码不直接调用桥方法。
- 高吞吐数据（PTY、截图、大文件）**不得**走 `execute` JSON；使用 FD / stream / 原生 View。
- 回调在后台线程到达；C# 侧不得阻塞 UI 线程等待 Bridge。
