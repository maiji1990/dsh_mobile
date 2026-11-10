# DSH Mobile

基于 [DSHBox](https://github.com/WSK-build/DSHBox) 原生组件与 **.NET 11 for Android** 的实用型 DeepSeek Harness Android 客户端。原样运行上游 DSH，.NET 只承担 Android 宿主、控制面与系统集成。

> 状态：**M2（POC）**。C# App 经 Bridge 驱动 DSHBox `sandbox-manager`，已在 Android 17 真机上导入运行环境并启动官方 DSH 0.2.1-rc.5（WebUI 承载尚未做）。见 [docs/FEATURES.md](docs/FEATURES.md)。
> 本项目与 DSHBox、DeepSeek 无隶属关系，不是官方客户端。

## 目录

| 路径 | 内容 |
|---|---|
| `src/DshMobile.Core` | 协议 DTO、`BridgeClient`（纯 `net11.0`） |
| `src/DshMobile.NativeBinding` | Java Facade AAR 的 .NET 绑定 |
| `src/DshMobile.Android` | App 宿主（`net11.0-android`） |
| `native/` | Gradle 工程：`native-bridge` Java Facade + 引用 DSHBox 模块 |
| `third_party/dshbox` | DSHBox 子模块，固定 `v1.5.1` |
| `docs/` | 架构、ADR、上游登记、安全、兼容性、测试计划；原始方案在 `docs/plan/` |

## 构建

前置：.NET SDK `11.0.100-rc.2.26504.105` + `android` workload（见 `global.json`）、JDK 17+（已验证 21）、Android SDK（platform 36）。

```bash
git submodule update --init --depth 1
```

`native/local.properties` 写入 `sdk.dir=<Android SDK 路径>`（或设置 `ANDROID_HOME`），然后：

```bash
pwsh native/build-native.ps1
```

运行层（DSH 层 + android-side，约 300 MB 下载，按固定 SHA-256 校验；不入库）：

```bash
pwsh runtime-bundle/fetch-runtime.ps1
```

```bash
dotnet test tests/DshMobile.Core.Tests
```

```bash
dotnet build src/DshMobile.Android -c Debug
```

真机安装与 M1 验收步骤见 [docs/TEST_PLAN.md](docs/TEST_PLAN.md)。

## 构建排障

- **`SSLHandshakeException` / `Remote host terminated the handshake`（Gradle）**：若本机经本地代理（如 `~/.gradle/gradle.properties` 里的 `systemProp.https.proxyHost`）访问失败而直连可用，可在命令行临时绕过代理（不要写进仓库）：
  ```bash
  pwsh native/build-native.ps1 "-Dhttp.nonProxyHosts=*" "-Dhttps.nonProxyHosts=*"
  ```
- **`Failed to install ... build-tools;36.0.0`**：本仓库固定 build-tools `37.0.0`；如本机只有其它版本，用 `-Pdshmobile.buildToolsVersion=<版本>` 覆盖。
- **构建 DSHBox 基线（TASK-001）缺 SDK 组件**：在管理员 PowerShell 运行 `pwsh -File tooling/install-android-sdk-components.ps1`（安装 `platforms;android-37.2`、`build-tools;36.0.0`）。
- **`NDK path cannot contain spaces`**（DSHBox `terminal-emulator` 的 ndk-build）：在 DSHBox clone 的 `local.properties` 里把 `ndk.dir` 指向一个无空格的目录联接。
- **`NDK path cannot contain spaces`（本仓库 terminal-emulator）**：`pwsh native/build-native.ps1` 会自动在 `native/.generated/` 建目录联接并写入 `ndk.dir`。
- **`JAVA_HOME is set to an invalid directory`**：JDK 自动升级后用户级 `JAVA_HOME` 可能仍指向旧目录；把它改到当前 JDK（或仅在当前终端 `$env:JAVA_HOME=…`）。
- **`local.properties` 路径**：Windows 请用正斜杠，例如 `sdk.dir=C:/Program Files (x86)/Android/android-sdk`。

## 许可

GPL-3.0（见 [LICENSE](LICENSE) 与 [ADR-0003](docs/ADR/0003-license-route-a-gpl3.md)）。第三方组件保留各自许可。不包含 DSHBox 品牌资产。
