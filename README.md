# DSH Mobile

<img src="docs/brand/dsh-mobile-logo.png" width="160" align="right" alt="DSH Mobile">

在 Android 手机上本地运行 [DeepSeek Harness（DSH）](https://www.npmjs.com/package/@deepseek-ai/dsh) 的客户端，用 **.NET 11 for Android（C#）** 编写。
DSH 原样运行在 App 私有目录里的 Debian 沙箱（PRoot）中。App 负责安装与启停运行环境、承载官方网页界面，并提供文件、手机操控与远程主机等能力。

> **实验性项目**，与 DeepSeek、DSHBox 均无隶属关系，不是官方客户端。基于预览版 .NET 11 SDK 构建。
> 部分 C# 代码移植自 [DSHBox](https://github.com/WSK-build/DSHBox)（GPL-3.0，逐项登记于 [docs/UPSTREAMS.md](docs/UPSTREAMS.md)），不含其品牌资产。

## 功能

| | 说明 |
|---|---|
| **本地运行 DSH** | 一键启动 / 停止沙箱内的官方 DSH（`web` profile），在 App 内全屏打开其网页界面；后台运行时通知栏常驻（前台服务） |
| **文件** | 浏览工作区 `/root/projects`，从系统文件选择器导入、导出与预览文件；`.dsh` 等受保护目录只读 |
| **插件** | 在 DSH 内管理 Cordis 插件 |
| **手机操控（实验）** | 让 DSH 通过 `ui_*` 工具读取和操作手机界面；每次写操作都需要你本人批准 |
| **远程主机** | 通过 SSH 连接你自己电脑或服务器上的 DSH：端口转发、远端 DSH 启停 |
| **运行环境维护** | 导入运行环境包（逐层校验）、导入 DSH 层、回滚到上一版 |

界面为 Material 3，导航包含 **首页 / 文件 / 更多**（横屏或宽屏时使用左侧导航栏）。主题跟随系统深浅色，Android 12+ 按壁纸取色。诊断、模式探测、PTY 自检与日志在
「更多 → 开发者选项」里，默认隐藏，在「关于」上连点 7 次开启（[ADR-0021](docs/ADR/0021-product-ui-material3.md)）。启动分段计时以 `startup-perf` 行写入 Logcat；采集日志、导出 CSV 与计算各阶段 P50/P95 见 [docs/STARTUP_PERF.md](docs/STARTUP_PERF.md)（真实耗时待真机验证）。

## 安全设计

- **手机操控需逐次批准**：审批卡显示期间窗口禁止截屏，Agent 的点击 / 滑动 / 按键全部暂停，它按不到「允许」。无障碍服务、悬浮窗与
  Shizuku 授权都由你本人在系统里开启，App 不会代为开启。默认关闭，停止 DSH 时自动关闭。
- **SSH 私钥不可导出**：本机密钥在 Android Keystore 内生成；首次连接必须逐字核对主机指纹，固定后指纹变化即拒绝连接。远端 DSH 无法使用本机的手机操控。
- **DSH 只在本机访问**：launch token 只留在 App 原生侧，网页界面里拿不到。
- **沙箱命令不拼字符串**：guest 命令一律由参数数组逐个加引号构建。
- **不伪造成功**：系统限制以稳定错误码报告并显示在界面上。

详见 [docs/SECURITY.md](docs/SECURITY.md)。

## 使用

**要求**：Android 10（API 29）及以上的 **arm64** 设备。APK 也带 x86_64 原生库，但运行层是 arm64，x86_64 模拟器上
沙箱会立即退出（`DSH_EXITED`），只能看界面。

1. 安装 APK（目前需[从源码构建](#从源码构建)）。
2. 首页点「导入运行环境包」，选择 Runtime Release 中的 `dshmobile-runtime-debian-arm64-*.zip`。导入会逐层校验 SHA-256。
3. 首页点「导入 DSH 层」，选择本项目 DSH Release 中的 `<版本>.tar.zst`。保留下载时的文件名。
4. 点「启动 DSH」，就绪后点「打开 DSH」。

运行环境和 DSH 层由本仓库构建并分别发布。当前锁定版本的下载地址见 [runtime-assets.lock](runtime-bundle/runtime-assets.lock)，发布流程见 [ADR-0020](docs/ADR/0020-self-built-runtime-layers.md) 和 [自动升级与发布流程](docs/RELEASE_AUTOMATION.md)。

## 从源码构建

**前置**：

- .NET SDK `11.0.100`（.NET 11 GA）与 `android` workload，workload set `11.0.100.1`（`global.json` 锁定，不自动前滚）；
- Android SDK：`platforms;android-37.2`、`build-tools;36.0.0`（可用 `tooling/install-android-sdk-components.ps1` 安装）；
- NDK `27.0.12077973`。

不需要 JDK / Gradle。在 `native/local.properties` 写入 `sdk.dir=<Android SDK 路径>`（或 `ndk.dir=…`，或设置 `ANDROID_NDK_HOME` / `ANDROID_HOME`）。

构建原生层（`libdshmobile_native` 与自建 PRoot，[ADR-0014](docs/ADR/0014-in-repo-proot-build.md)；源码包按 `native/proot/sources.lock` 钉住的 SHA-256 校验）：

```bash
pwsh native/build-native.ps1
```

下载并校验锁定的运行环境包，为 APK 暂存其中的 `android-side` 层：

```bash
pwsh runtime-bundle/fetch-runtime.ps1
```

构建 App：

```bash
dotnet build DshMobile.slnx -c Debug
```

安装到已连接的设备（多台设备时追加 `-p:AdbTarget=-s%20<序列号>`）：

```bash
dotnet build src/DshMobile.Android/DshMobile.Android.csproj -c Debug -t:Install
```

如果要用 `adb install -r` 直接覆盖安装，先构建包含 .NET 程序集的 Release APK，再安装生成的 `*-Signed.apk`。

```bash
dotnet build src/DshMobile.Android/DshMobile.Android.csproj -c Release -p:EmbedAssembliesIntoApk=true
```

### 自建运行层

在 Linux / WSL 上（需要 `mmdebstrap`；x86_64 主机另需 arm64 binfmt / qemu-user），输入全部钉在 `runtime-bundle/runtime-build.lock`：

```bash
bash runtime-bundle/build-runtime.sh --work <Linux 目录>
```

CI 中对应手动触发的 `.github/workflows/runtime-layers.yml`。

### 测试

```bash
dotnet test tests/DshMobile.Core.Tests
```

```bash
dotnet test tests/DshMobile.Runtime.Tests
```

```bash
dotnet test tests/DshMobile.Application.Tests
```

```bash
node tooling/run-node-tests.mjs tests/guest
```

行为由 `tests/TestVectors/*.json` 共享向量锁定；原样复制进仓库的上游文件由测试钉住 SHA-256。CI（`.github/workflows/android-dotnet.yml`）
构建原生层与 App 并运行以上全部测试。

### 构建排障

- **`NDK 27.0.12077973 not found`**：用 SDK Manager 安装 `ndk;27.0.12077973`，或在 `native/local.properties` 写 `ndk.dir`。
- **`NDK path cannot contain spaces`**：把 `ndk.dir` 指向一个无空格的目录联接。
- **`local.properties` 路径**：Windows 请用正斜杠，例如 `sdk.dir=C:/Program Files (x86)/Android/android-sdk`。
- **`PRoot not built in …` / `libdshmobile_native.so not built`**：先运行 `pwsh native/build-native.ps1`（App 构建会检查这两组原生产物）。
- **源码包下载失败**：`native/proot/sources.lock` 中每个源可列多个地址依次尝试；也可用 `-SourceCache <目录>` 指向已下载的包，或 `-Offline` 只用本地缓存。

## 架构

```text
DshMobile.Android      Activity / Fragment（Material 3）、前台服务、平台适配、Relay 组合根
DshMobile.Application  编排：RuntimeCoordinator（状态机 + journal、导入 / 回滚）、PTY 自检、首页状态模型、DI
DshMobile.Runtime      层校验与安装、PRoot 启动、DSH 宿主、Relay（ui_*）、远程 SSH（SSH.NET）
DshMobile.Native       最小原生边界：W^X 可执行策略、libdshmobile_native（PTY、进程组）P/Invoke
DshMobile.Core         通用协议类型、WebView 策略、工作区文件系统
```

业务逻辑、状态机、权限策略与进程监管都在 C#（[ADR-0007](docs/ADR/0007-source-ownership-csharp-first.md)）；必须原生的部分放在本仓库可追溯的
最小 NDK 层；DSH 本身不修改，能力通过 Cordis 插件、MCP 与官方 profile 扩展。详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

| 路径 | 内容 |
|---|---|
| `src/` | 上述五个项目 |
| `native/dshmobile-native`、`native/proot` | NDK 27 构建的原生库与自建 PRoot / loader / talloc / libandroid-shmem |
| `native/link-shim` | guest 链接垫片的上游源码（仅追溯，不在此构建） |
| `runtime-bundle/` | 运行层锁定、下载与自建脚本 |
| `tests/` | 三个 xUnit 测试项目、guest 脚本测试（node）与共享向量 |
| `tooling/` | SDK 组件安装、测试运行、向量生成等辅助脚本 |
| `docs/` | 架构、ADR、迁移状态、功能矩阵、上游登记、安全、兼容性；原始方案在 `docs/plan/` |

## 文档

- [docs/FEATURES.md](docs/FEATURES.md)：功能矩阵与真机证据
- [docs/MIGRATION.md](docs/MIGRATION.md)：迁移状态（唯一事实源）
- [docs/VNEXT_STATUS.md](docs/VNEXT_STATUS.md)：vNext 基线、证据核对（已通过 / 待复测 / 待测 / 阻断）与下一批 PR 门禁
- [docs/ADR/](docs/ADR/)：架构决策记录（0001–0026）
- [docs/UPSTREAMS.md](docs/UPSTREAMS.md)：上游来源、固定版本与许可
- [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md)：设备兼容性
- [AGENTS.md](AGENTS.md)：开发约束（人类与 AI 共用）

## 现状与限制

- App 只有 C# 实现（[ADR-0018](docs/ADR/0018-remove-dshbox.md) 删除了 DSHBox 子模块、AAR 与 Kotlin 层）。新界面（ADR-0021）已在 Android 17 真机上
  逐页验收（启动 / WebUI / 终端 / 文件 / 停止、诊断与 PTY 自检，导入 / 回滚，手机操控中继与审批卡，深色模式与横屏）；远程主机连接尚未在新实现上真机复验。
  各项状态与证据以 [docs/MIGRATION.md](docs/MIGRATION.md) 为准。
- DSH 首次打开会提示「无法创建默认工作区」（guest 内没有 Documents 目录配置），需在 DSH 里手动选择工作区。
- 「回滚到上一版」只有在 `runtime-current/previous/` 已存在时才有上一版可回（继承旧实现，向量锁定），目前实际使用中通常会如实提示没有上一版。
- 模型凭据相关能力（会话、工具调用、Subagent 等）尚未验证。
- 浏览器自动化（`browser_*`）不提供。

## 许可

GPL-3.0（见 [LICENSE](LICENSE) 与 [ADR-0003](docs/ADR/0003-license-route-a-gpl3.md)）。第三方组件保留各自许可（PRoot GPL-2.0-or-later、
talloc LGPL-3.0-or-later、Material Components / Material Icons Apache-2.0 等，完整清单见 [docs/UPSTREAMS.md](docs/UPSTREAMS.md)）。
