# DSH Mobile

**.NET 11 for Android** 的实用型 DeepSeek Harness Android 客户端。原样运行上游 DSH（PRoot + Debian guest），Android 宿主、控制面、
运行时管理、终端、手机操控（`ui_*`）与远程 SSH 全部用 C# 实现。

> 状态：迁移完成（PR-10，[ADR-0018](docs/ADR/0018-remove-dshbox.md)）：不再包含 [DSHBox](https://github.com/WSK-build/DSHBox) 源码、AAR 或 Kotlin 层；
> 部分 C# 代码移植自 DSHBox（GPL-3.0，见 [docs/UPSTREAMS.md](docs/UPSTREAMS.md)），运行层取自其 Release。PR-10 前后的翻转与删除未经真机验收，
> 见 [docs/MIGRATION.md](docs/MIGRATION.md)。
> 本项目与 DSHBox、DeepSeek 无隶属关系，不是官方客户端。

## 目录

| 路径 | 内容 |
|---|---|
| `src/DshMobile.Core` | 通用协议类型、WebView 策略、工作区文件系统（纯 `net11.0`） |
| `src/DshMobile.Native` | 最小原生边界（W^X 策略、`libdshmobile_native` P/Invoke） |
| `src/DshMobile.Runtime` | 运行时：层校验 / 安装、PRoot 启动、DSH 宿主、Relay、远程 SSH |
| `src/DshMobile.Application` | 编排：`RuntimeCoordinator`、终端会话、DI |
| `src/DshMobile.Android` | App 宿主（`net11.0-android`） |
| `native/dshmobile-native`、`native/proot` | NDK 27 构建的本仓库原生库与自建 PRoot（ADR-0014） |
| `native/link-shim` | guest 链接垫片的上游源码（仅追溯，不在此构建） |
| `runtime-bundle/` | 运行层锁定与下载脚本 |
| `docs/` | 架构、ADR、迁移状态、上游登记、安全、兼容性、测试计划；原始方案在 `docs/plan/` |

## 构建

前置：.NET SDK `11.0.100-rc.2.26504.105` + `android` workload（见 `global.json`）、Android SDK（platform 36）、NDK `27.0.12077973`。
不需要 JDK / Gradle。

`native/local.properties` 写入 `sdk.dir=<Android SDK 路径>`（或 `ndk.dir=…`，或设置 `ANDROID_NDK_HOME` / `ANDROID_HOME`），然后：

```bash
pwsh native/build-native.ps1
```

运行层（DSH 层 + android-side，约 300 MB 下载，按固定 SHA-256 校验；不入库）：

```bash
pwsh runtime-bundle/fetch-runtime.ps1
```

```bash
dotnet build DshMobile.slnx -c Debug
```

```bash
dotnet test tests/DshMobile.Runtime.Tests
```

```bash
node tooling/run-node-tests.mjs tests/guest
```

真机安装与验收步骤见 [docs/TEST_PLAN.md](docs/TEST_PLAN.md)。

## 构建排障

- **`NDK 27.0.12077973 not found`**：用 SDK Manager 安装 `ndk;27.0.12077973`，或在 `native/local.properties` 写 `ndk.dir`。
- **`NDK path cannot contain spaces`**：把 `ndk.dir` 指向一个无空格的目录联接。
- **`local.properties` 路径**：Windows 请用正斜杠，例如 `sdk.dir=C:/Program Files (x86)/Android/android-sdk`。
- **`PRoot not built in …` / `libdshmobile_native.so not built`**：先运行 `pwsh native/build-native.ps1`（App 构建会检查这两组原生产物）。

## 许可

GPL-3.0（见 [LICENSE](LICENSE) 与 [ADR-0003](docs/ADR/0003-license-route-a-gpl3.md)）。第三方组件保留各自许可。不包含 DSHBox 品牌资产。
