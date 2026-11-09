# AGENTS.md — DSH Mobile 实施约束（AI / 人类开发者共用）

完整方案见 [docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md](docs/plan/DSH_Mobile_NET11_完整架构与实施方案.md)。本文件是其第 18 节的可执行版本。

## 开工前必读

1. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)、[docs/UPSTREAMS.md](docs/UPSTREAMS.md)、[docs/SECURITY.md](docs/SECURITY.md)、[docs/FEATURES.md](docs/FEATURES.md)。
2. 涉及架构取舍时先看 [docs/ADR/](docs/ADR/)，重大变更先写新 ADR。

## 硬性规则

1. **不重写 DSH 内核**：未经审核不得修改上游 DSH AgentLoop、Session Log、Cordis core。能力通过 Cordis 插件 / 官方 profile 扩展。
2. **不绑定 DSHBox Compose 页面**：C# 原生 UI 只通过 `native/native-bridge` 的 Java Facade 访问 DSHBox Kotlin 代码；不对整个 DSHBox 生成 C# 绑定。DSHBox 的 AAR 一律 `Bind="false"`。
3. **最小垂直切片**：先实现最小可运行纵切，再写单元 / 契约 / Android Instrumentation 测试，CI 通过再扩功能。
4. **品牌**：禁止复制 DSHBox 名称、Logo、图标、Splash 等品牌资产（见 `third_party/dshbox/TRADEMARK.md`）；保留源码许可证与第三方署名。
5. **不伪造成功**：系统限制必须返回稳定错误码（`DshMobile.Core.Bridge.BridgeErrorCodes`）并在 UI 可见；禁止为“看似支持”返回假成功。
6. **`ui_*` 单一契约**：手机自动化沿用 `maiji1990/ui_mcp` 契约与单写者规则，不并行造第二套工具。
7. **上游路径不硬编码**：生产代码不得硬编码 `third_party/` 内部目录；只依赖 Gradle 构建输出（`artifacts/native/*.aar`）与 Facade API。
8. **上游升级**：先做兼容性对照与变更日志，单独 PR；更新 `docs/UPSTREAMS.md`。
9. **真机证据**：Android 17 / 厂商 ROM 兼容性不能仅凭单元测试宣称通过，必须附真机日志或报告。
10. **版本锁定**：.NET SDK/workload 由 `global.json` 锁定；Gradle/AGP/Kotlin 跟随 DSHBox 锁定的 `libs.versions.toml`；禁止在发布环节使用 `latest`。

## 构建

```bash
git submodule update --init --depth 1
pwsh native/build-native.ps1      # 或 bash native/build-native.sh → 产出 artifacts/native/*.aar
dotnet build DshMobile.slnx -c Debug
dotnet test tests/DshMobile.Core.Tests
```

## 跨语言桥接约定

- 控制面：`DshNativeFacade.execute(requestJson, callback)`，协议见 [docs/PROVIDER_CONTRACTS.md](docs/PROVIDER_CONTRACTS.md)。
- 高吞吐数据（PTY、截图、大文件）**不得**走 `execute` JSON；使用 FD / stream / 原生 View。
- 回调在后台线程到达；C# 侧不得阻塞 UI 线程等待 Bridge。
