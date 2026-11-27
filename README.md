# DSH Mobile

<img src="docs/brand/dsh-mobile-logo.png" width="160" align="right" alt="DSH Mobile">

在 Android 设备上以纯原生体验运行官方 [DeepSeek Harness（DSH）](https://www.npmjs.com/package/@deepseek-ai/dsh) 的开源客户端，基于 **.NET 11 for Android（C#）**、**Material 3** 与 **NDK 27 自建 PRoot 沙箱** 构建，同时支持**本机沙箱**与**远程 SSH 主机**双模式。

DSH 内核保持官方原样，不修改、不重写，通过 Cordis 插件、MCP 与官方 profile 扩展能力；全仓为纯 C# 原生实现，**零业务 WebView**（由 `NoBusinessWebViewGateTests` 永久门禁锁定），也不提供内置浏览器栈（`browser_*`）。

> **实验性项目**：与 DeepSeek、DSHBox 均无隶属关系，非官方客户端。基于 .NET 11 SDK 构建。
> 部分 C# 代码移植自 [DSHBox](https://github.com/WSK-build/DSHBox)（`v1.5.1`，GPL-3.0，逐项登记于 [docs/UPSTREAMS.md](docs/UPSTREAMS.md)），不包含其任何品牌资产。

---

## 核心特性

### 纯原生 Material 3 体验（V2 重塑）

- **纯净中性 Graphite 双色盘**：默认采用低噪中性浅 / 深色体系与 `1dp` 极细分隔线；可在应用内「外观」设置切换 **跟随系统 / 浅色 / 深色**，并按需开启 Android 12+ 壁纸动态取色（默认关闭，以保持中性对比度）。
- **自适应极简外壳**：手机端取消底部导航栏，直接以高密度「会话」首页作为主入口，顶栏可快速前往工作区与设置；平板与折叠屏（`≥600dp`）自动启用左侧 `NavigationRail` 与「会话列表 + 右栏详情」双栏布局，并在宽屏居中限宽。
- **现代化流式对话**：
  - 悬浮胶囊顶栏，单层承载标题、本机 / 远程连接状态与跨会话待办收件箱入口；
  - 一体化 `26dp` 无硬描边 Composer，无缝融合图片附件、中文化斜杠命令（`/`）快捷 Tray 与内嵌模型 / 推理档位（Reasoning Effort）选择器；
  - `16dp` 统一阅读基线、`1dp` 垂直时间线与同族工具聚合折叠（键值化参数展示，成功或失败均可整组折叠）；
  - 非模态固定审批 / 问答底槽（支持多待办切换与 3 秒 Hold-to-undo 撤回），不挤占正文滚动；
  - 原生轻量代码语法高亮、单元格限宽且可横向滚动的 Markdown 表格，以及 Cache-First 离线历史首屏回看。

### 工作区、协作与系统能力

| 能力 | 说明 |
|---|---|
| **工作区与独立全屏 Diff 审查** | 浏览 `/root/projects`、自动发现一级 `.git` 子仓库；支持 SAF 按次导入导出、冲突感知安全文本编辑、回收站撤销、压缩解压与折叠式递归搜索；独立全屏 Diff 审查页提供 Hunk 折叠、等宽行号与自动换行切换；`.dsh/` 目录双向受保护 |
| **跨会话待办收件箱与后台通知** | 顶部实时横幅、收件箱抽屉与独立后台审批 / 问答系统通知通道，支持直达对应会话并定位请求卡片 |
| **本地运行环境与系统治理** | 支持离线 SAF 导入与签名清单在线发现 / 事务更新 / 回滚；提供前台常驻服务、后台工作租约、空闲优雅停机、32 幻影进程预算预警与温控预热抑制 |
| **远程 SSH 主机双模** | 通过 SSH（SSH.NET）连接电脑或服务器上的 DSH，支持本地回环端口转发、远端 DSH 启停与同一套原生对话体验 |
| **插件目录** | 原生 L0 状态清单与 L1 受控装卸 / 启停，重启 DSH 自动同步 |
| **手机操控（实验，默认关闭）** | 沿用 `maiji1990/ui_mcp` 单一 `ui_*` 契约，经无障碍 / 悬浮窗 / Shizuku 协同操控界面；每次写操作均须人工显式批准 |

> 诊断、模式探测、PTY 自检与运行日志位于「开发者选项」（默认隐藏，在设置页「关于」上连点 7 次开启，见 [ADR-0021](docs/ADR/0021-product-ui-material3.md)）。启动分段计时与 P50/P95 采集分析方法见 [docs/STARTUP_PERF.md](docs/STARTUP_PERF.md)。

---

## 安全原则

- **手机操控逐次人工审批**：审批卡与确认页启用 `FLAG_SECURE` 防截屏并过滤被遮挡触摸，显示期间暂停 Agent 的点击、滑动与按键操作，确保 Agent 无法自行点击「允许」。无障碍服务、悬浮窗与 Shizuku 均由用户在系统界面自行授予，默认关闭且停止沙箱时自动停用。
- **SSH 不可导出私钥与主机指纹固定**：本机 EC P-256 私钥在 Android Keystore 内生成并完成签名，永不导出到文件或内存；首次连接须逐字核对主机指纹，固定后若指纹变化立即在握手阶段拒绝连接。远端 DSH 仅走本地端口转发，无法访问本机 `ui_*` 能力。
- **回环鉴权与零业务 WebView**：DSH 仅绑定本地回环地址，launch token 仅驻留原生进程内存、绝不落盘或写入日志；外部 `http(s)` 链接经明确确认后交由系统浏览器打开。
- **命令与文件系统边界**：所有 guest 命令一律由参数数组（`GuestCommandLine`）逐 token 单引号转义，禁止拼接 shell 字符串；工作区操作拒绝路径穿越与符号链接跟随，`.dsh/` 不可读写、移入或解压覆盖。
- **如实报错，不伪造成功**：所有系统限制与失败路径均返回稳定错误码并在界面可见。

完整威胁模型与已落实控制清单见 [docs/SECURITY.md](docs/SECURITY.md)。

---

## 架构概览

业务逻辑、状态机、权限策略与进程监管全部位于 C#（[ADR-0007](docs/ADR/0007-source-ownership-csharp-first.md)），原生层仅保留最小 NDK 边界，无 Gradle / Kotlin 业务桥接（[ADR-0018](docs/ADR/0018-remove-dshbox.md)）。

```text
DshMobile.Android (net11.0-android)   Material 3 原生界面（极简会话首页 / w600dp 双栏 / 聊天 / 工作区与 Diff 审查）、
        │                             前台服务、系统治理、C# Relay 组合根与平台适配
DshMobile.Agent (net11.0)             纯托管原生客户端层（零 Android 引用，ADR-0033）：协议 STJ 源生、
        │                             RemoteDshGateway、Transcript/Requests 引擎、Markdig 渲染缓存、ViewModels
DshMobile.Application (net11.0)       业务编排：RuntimeCoordinator（状态机 + journal、导入 / 更新探针 / 回滚）、
        │                             工作租约 / 空闲停机 / 温控与预热策略、HomeStatus、DI 组装
DshMobile.Runtime (net11.0)           层校验与事务安装、PRoot 启动、DSH 宿主与内存 token 追踪、
        │                             Relay（ui_*）、远程 SSH（SSH.NET）、插件 L0/L1 受控命令
DshMobile.Native (net11.0)            最小原生边界：Android 10+ W^X 可执行策略、libdshmobile_native（PTY / 进程组）P/Invoke
DshMobile.Core (net11.0)              共享契约、StartupTrace、工作区文件系统、Provider V2 与统一 Diff 规范化
```

运行时执行链路：

```text
MainActivity ─▶ BridgeHost ─▶ RuntimeCoordinator ─▶ DshHost ─▶ exec nativeLibraryDir/libproot.so
     │                                                  ▼
     └─ AndroidForegroundService (specialUse)   PRoot → Debian → Node → 官方 DSH (web, 127.0.0.1)
```

| 目录 | 说明 |
|---|---|
| `src/` | 上述 6 个核心项目，以及仅在 `-p:DshDiagnostics=true` 时编译的诊断库 `DshMobile.Runtime.Diagnostics` |
| `native/dshmobile-native`、`native/proot` | NDK 27 构建的最小原生库与自建 PRoot / loader / talloc / libandroid-shmem（[ADR-0014](docs/ADR/0014-in-repo-proot-build.md)） |
| `native/link-shim` | Android 硬链接兼容垫片源码与交叉构建脚本（[ADR-0019](docs/ADR/0019-rename-dshbox-paths-data-migration.md)） |
| `runtime-bundle/` | 运行环境与 DSH 层的版本锁定、拉取校验脚本与 Linux 自建脚本（[ADR-0020](docs/ADR/0020-self-built-runtime-layers.md)） |
| `tests/` | 4 个 xUnit 测试项目（`Core` / `Runtime` / `Application` / `Agent`）、`tests/guest` 与 `tests/tooling` Node 测试、共享向量 `tests/TestVectors` |
| `tooling/` | Android SDK 组件安装、净构建与 APK 门禁脚本、测试运行器与启动耗时分析脚本 |
| `docs/` | 架构说明、ADR（`0001–0033`）、功能矩阵、迁移事实源、安全基线、真机验收报告与原始设计方案（`docs/plan/`） |

详见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

---

## 快速上手

**设备要求**：Android 10（API 29）及以上的 **arm64** 设备。APK 同时包含 `x86_64` 原生库用于界面预览，但 Debian / Node / DSH 运行层仅面向 `arm64`，在 `x86_64` 模拟器上启动沙箱会立即退出（`DSH_EXITED`）。

1. 按下方步骤[从源码构建](#从源码构建)并安装 APK。
2. 首次进入「会话」首页时，可点击在线下载运行环境，或前往「设置 → 运行环境」通过系统文件选择器导入 `dshmobile-runtime-debian-arm64-*.zip` 与 DSH 层 `<版本>.tar.zst`（[ADR-0027](docs/ADR/0027-remove-bundled-dsh-import-instead.md) 起 DSH 层不内置于 APK）。
3. 启动本机 DSH（或在「设置 → 远程主机」连接你的电脑 / 服务器），前往「模型与凭据」配置 API Key，即可开始原生对话。

当前锁定运行层清单见 [runtime-bundle/runtime-assets.lock](runtime-bundle/runtime-assets.lock)，发布自动化流程见 [docs/RELEASE_AUTOMATION.md](docs/RELEASE_AUTOMATION.md)。

---

## 从源码构建

### 1. 工具链前置（均版本锁定，无需 JDK / Gradle）

- **.NET SDK `11.0.100`** 与 `android` workload（workload set `11.0.100.1`，由 [global.json](global.json) 锁定且禁止自动前滚）；
- **Android SDK**：`platforms;android-37.2`、`build-tools;36.0.0`（Windows 可用管理员 PowerShell 运行 `pwsh tooling/install-android-sdk-components.ps1` 安装）；
- **Android NDK `27.0.12077973`**；
- 配置路径：在 `native/local.properties` 写入 `sdk.dir=<Android SDK 路径>`（或 `ndk.dir=<NDK 路径>`，Windows 请使用正斜杠 `/`），或设置环境变量 `ANDROID_HOME` / `ANDROID_NDK_HOME`。

### 2. 构建原生层与拉取运行层资产

构建 `libdshmobile_native.so` 与自建 PRoot（源码按 `native/proot/sources.lock` 校验 SHA-256，产物输出至 `artifacts/native/`）：

```bash
# Windows (PowerShell 7)
pwsh native/build-native.ps1

# Linux / macOS
bash native/build-native.sh
```

拉取并校验锁定的运行层包，为 APK 暂存 `android-side` 层：

```bash
pwsh runtime-bundle/fetch-runtime.ps1
```

> 如需在 Linux / WSL 上自行构建 Debian / Node / DSH 运行层（需 `mmdebstrap`；x86_64 主机另需 arm64 binfmt / qemu-user，输入锁定于 `runtime-bundle/runtime-build.lock`）：
> ```bash
> bash runtime-bundle/build-runtime.sh --work <Linux 目录>
> ```
> 对应 CI 工作流为 `.github/workflows/runtime-layers.yml`。

### 3. 构建与打包 APK

构建整个解决方案（Debug）：

```bash
dotnet build DshMobile.slnx -c Debug
```

直接安装 Debug 版到已连接设备（多台设备时追加 `-p:AdbTarget=-s%20<序列号>`）：

```bash
dotnet build src/DshMobile.Android/DshMobile.Android.csproj -c Debug -t:Install
```

打包包含完整 .NET 程序集的独立真机 Release APK（用于 `adb install -r` 安装 `*-Signed.apk`）：

```bash
dotnet build src/DshMobile.Android/DshMobile.Android.csproj -c Release -p:EmbedAssembliesIntoApk=true
```

推荐使用发布前净构建与零业务 WebView 门禁脚本（自动清理 `obj/bin` 避免增量构建残留并校验最终 `*-Signed.apk`）：

```bash
pwsh tooling/build-clean-release-apk.ps1
```

### 4. 运行测试

```bash
dotnet test tests/DshMobile.Core.Tests
dotnet test tests/DshMobile.Runtime.Tests
dotnet test tests/DshMobile.Application.Tests
dotnet test tests/DshMobile.Agent.Tests
node tooling/run-node-tests.mjs tests/guest
```

核心协议与运行时行为由 `tests/TestVectors/*.json` 共享向量锁定，原样引入的上游资产由测试钉住 SHA-256。CI 工作流 [`.github/workflows/android-dotnet.yml`](.github/workflows/android-dotnet.yml) 会完整构建原生层与 App 并执行全部测试与质量门禁。

### 5. 常见构建排障

- **`NDK 27.0.12077973 not found`**：通过 SDK Manager 安装 `ndk;27.0.12077973`，或在 `native/local.properties` 显式配置 `ndk.dir`。
- **`NDK path cannot contain spaces`**：将 `ndk.dir` 指向无空格的目录或目录联接。
- **`PRoot not built in …` / `libdshmobile_native.so not built`**：先执行 `pwsh native/build-native.ps1`（或 `bash native/build-native.sh`）。
- **原生源码包下载失败**：`native/proot/sources.lock` 支持多镜像地址回退；也可传 `-SourceCache <目录>` 或 `-Offline` 复用本地缓存。

---

## 文档索引

| 文档 | 内容 |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | 架构分层、运行时边界、原生传输协议与禁止模式 |
| [docs/FEATURES.md](docs/FEATURES.md) | 引擎能力、宿主基础设施与可选模块状态矩阵（含证据时代标记） |
| [docs/SECURITY.md](docs/SECURITY.md) | 安全基线、已落实控制清单与每个 PR 的安全约束 |
| [docs/MIGRATION.md](docs/MIGRATION.md) | C#-first 与原生改造迁移事实源（唯一权威记录） |
| [docs/UPSTREAMS.md](docs/UPSTREAMS.md) | 上游来源登记、固定 commit SHA、patch 清单与开源许可 |
| [docs/RELEASE_AUTOMATION.md](docs/RELEASE_AUTOMATION.md) | 运行层构建、签名清单发布与上游自动跟踪流程 |
| [docs/VNEXT_STATUS.md](docs/VNEXT_STATUS.md) | 基线证据核对、待复测与阻断项清单 |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | 设备与 Android 版本兼容性实测记录 |
| [docs/STARTUP_PERF.md](docs/STARTUP_PERF.md) | 启动分段计时采集、CSV 导出与 P50/P95 统计指南 |
| [docs/native/ACCEPTANCE_MATRIX.md](docs/native/ACCEPTANCE_MATRIX.md) | 原生客户端各切片 Android 真机验收总表 |
| [docs/ADR/](docs/ADR/) | 架构决策记录（`0001–0033`） |
| [AGENTS.md](AGENTS.md) | 项目开发红线与工程约定（人类与 AI 协作共用） |

---

## 现状与已知限制

- **验证边界**：原生客户端主体闭环（PR-00 ～ PR-09 与 UI 布局重构 Slice 1 ～ Slice 5）已在 Android 17 真机完成验收（详见 [docs/native/ACCEPTANCE_MATRIX.md](docs/native/ACCEPTANCE_MATRIX.md)）；最新 V2 视觉与交互打磨（去底栏极简首页、w600dp 列表-详情双栏、统一 Composer、独立全屏 Diff 审查页等）已完成桌面编译与单元测试，**真机专项验收待执行**。
- **待测与受限项**：签名清单在线一键安装 / 回滚的真实端到端验收、手机操控写操作与通知栏审批面、真实局域网（非 `adb reverse`）远程 SSH 与 `HOST_KEY_CHANGED` 真机演练、以及 Android 10 / 16 KB 页大小设备兼容性仍待补充真机证据，详见 [docs/FEATURES.md](docs/FEATURES.md) 与 [docs/MIGRATION.md](docs/MIGRATION.md)。
- **回滚前提**：「回滚到上一版」仅在 `runtime-current/previous/` 槽位已存在时可用。
- **不包含的能力**：不提供内置浏览器栈（`browser_*`），也不在 C# 侧重写 DSH Agent Loop。

---

## 致谢与许可证

本项目采用 **GPL-3.0** 许可发布（见 [LICENSE](LICENSE) 与 [ADR-0003](docs/ADR/0003-license-route-a-gpl3.md)）。

感谢并尊重以下上游与开源项目（完整来源、固定版本与各自许可证均逐项登记于 [docs/UPSTREAMS.md](docs/UPSTREAMS.md)）：

- [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) — 官方运行时内核
- [DSHBox](https://github.com/WSK-build/DSHBox)（GPL-3.0）— 早期移植参考与已登记的 guest 垫片资产来源
- [maiji1990/ui_mcp](https://github.com/maiji1990/ui_mcp) — `ui_*` 手机操控单一契约参考
- [termux/proot](https://github.com/termux/proot)（GPL-2.0-or-later）、[Samba talloc](https://www.samba.org/ftp/talloc/)（LGPL-3.0-or-later）、[termux/libandroid-shmem](https://github.com/termux/libandroid-shmem)（BSD-3-Clause）
- [Shizuku](https://github.com/RikkaApps/Shizuku-API)（MIT）、Material Components for Android / Material Icons（Apache-2.0）等第三方依赖
