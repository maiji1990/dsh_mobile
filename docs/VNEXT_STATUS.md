# vNext 状态（VNEXT_STATUS）

vNext 方案「PR-00：基线与 CI 完整性」的产出：锁定基线、核对证据、列出阻断项与下一批 PR 的切片。**本文只记录事实，不宣称未经真机验证的功能可用**
（AGENTS.md 规则 5 / 9）。功能逐项状态仍以 [FEATURES.md](FEATURES.md) 为准（PR-00 起带证据时代标记），平台矩阵见
[COMPATIBILITY.md](COMPATIBILITY.md)「vNext 待测矩阵」。

最后更新：2026-11-16。

## 1. 基线

| 项 | 值 |
|---|---|
| 基线 commit | `main` @ `7e87118e2daf7f7c53e0a1a16f408f04a7e45806`（Merge maiji1990/dsh_mobile#41） |
| CI | `android-dotnet` run [43793423389](https://github.com/maiji1990/dsh_mobile/actions/runs/43793423389)：success（windows-latest，2026-11-16 02:38–02:46 UTC） |
| SDK / workload（CI） | .NET SDK `11.0.100-rc.2.26504.105`，workload set `11.0.100-rc.2.26513.2`（`global.json`，`rollForward: disable`） |
| NDK | 27.0.12077973 |

CI 中各套件（net11.0，同一 run）：

| 套件 | 结果 |
|---|---|
| `DshMobile.Core.Tests` | 35 通过 / 0 失败 / 0 跳过 |
| `DshMobile.Runtime.Tests` | 798 通过 / 0 失败 / 1 跳过（`RemoteSshOpenSshTests.DshWebStartStop_AgainstWindowsOpenSsh`：需真实 Windows OpenSSH） |
| `DshMobile.Application.Tests` | 112 通过 / 0 失败 / 0 跳过 |
| guest（`node tooling/run-node-tests.mjs tests/guest`） | 37 通过 / 0 失败（运行器对 0 个文件 / 0 个通过视为失败） |
| `dotnet build src/DshMobile.Android -c Release` | Build succeeded，75 warnings / 0 errors；其中 `Runtime layers not staged … the APK will not contain android-side/DSH`（见 §4 风险 6） |

### 1.1 `PtyProbeTests` 重复验证（CI 偶发失败的修复是否真正生效）

背景：run [43784510013](https://github.com/maiji1990/dsh_mobile/actions/runs/43784510013)（`c55f848`）中
`IgnoredCtrlC_FailsThatStep_AndTheSessionIsHungUp` 失败；maiji1990/dsh_mobile#41 改为 `TimeProvider` + `FakeTimeProvider`，
测试只在 FakeShell 静默（输出全部读走、读线程回到等待）时推进假时钟。此后 `main` 的 CI 均通过。

本次重复运行（见 [证据](../migration/parity/2026-11-16-vnext-pr00-baseline.md)）：

| 运行方式 | 结果 |
|---|---|
| 只跑 `PtyProbeTests`（7 例）× 30 | 30 / 30 通过 |
| 整个 Application 套件（112 例，类间并行）× 10 | 10 / 10 通过 |
| 线程池饥饿复现（#41 之前必失败）× 3 | 3 / 3 通过（约 1 m 3 s / 次：时钟只在空闲时推进，饥饿下变慢而不误判） |

（本地 .NET 10 重定向 net10.0。CI 自 #41 起的 2 次运行均通过：runs 43793404535（PR）、43793423389（main）。）

**结论**：在能复现原失败的条件下（线程池饥饿）已不再失败；但这是桌面 xUnit 证据，**#41 之后的构建还没有在真机上跑过「终端 PTY 自检」**，
列为待复测（§3）。没有删除或跳过任何测试。

### 1.2 本环境的限制（如实记录，未虚报）

本次 PR-00 在云端容器中执行：网络策略拦截 `builds.dotnet.microsoft.com` / `dotnetcli.azureedge.net` 等 .NET 下载源，只能从 Ubuntu 源装到
.NET SDK 10.0.115。因此：

- 本地测试是在**临时副本**里把 `net11.0` 重定向为 `net10.0` 跑的（仓库未改）；以 CI 的 net11.0 结果为准。
- **Android Release 构建未在本地执行**：缺 .NET 11 SDK、`android` workload、NDK 27 与 Android SDK。在已按 `global.json` 装好工具链的开发机上执行：

```powershell
pwsh native/build-native.ps1
pwsh runtime-bundle/fetch-runtime.ps1
dotnet build src/DshMobile.Android -c Release
```

## 2. 证据核对：旧桥证据 vs 当前 C# 实现

PR-10（ADR-0018，2026-11-15）删除了全部旧桥 / DSHBox 代码。此前的真机证据（2026-11-09 … 11-14）验证的是旧实现，或当时**开发者可选**的
C# 路径；PR-10 之后的 C# 唯一实现只有两次真机验收：

- [2026-11-16 产品界面](../migration/parity/2026-11-16-PTP-AN10-A17-product-ui.md)
- [2026-11-16 续测](../migration/parity/2026-11-16-PTP-AN10-A17-runtime-relay-dark-landscape.md)

两次都是 Honor PTP-AN10 / Android 17 / arm64 / 4 KB 页，**Debug 构建**，分支 `ui/product-material3`，**早于 #41**
（#41 的 PtyProbe 计时、DSH 层换层事务、ADR-0022 均无真机证据）。

| 分类 | 条目 |
|---|---|
| **已通过（真机 · 当前 C#）** | 启动 → READY → WebUI → 停止无残留；web 与 sdk / sdk-minimal / acp **握手**、plugin list；终端多会话 / ^C=130 / 重放 / resize / 横屏、PTY 自检 PASS；文件页浏览与受保护 `.dsh`；运行层导入 + 装后核对、回滚（需手工建 `previous/`）；手机操控只读 + 页内 / 悬浮审批卡（`FLAG_SECURE`）；深色模式；ADR-0019 数据迁移；覆盖安装杀进程后 journal `RUNTIME_INTERRUPTED` 恢复 |
| **待复测（只有 PR-10 前真机证据）** | Release full-trim 冷启动（TASK-002）；SAF 导入 / 导出；插件 add / remove 与安装守卫；手机操控写操作；前台服务后台 90 s 存活；远程主机（SSH → Windows OpenSSH，远端 `dsh web` 启停） |
| **待测（从未真机验证）** | #41 之后的 PTY 自检；DSH 层换层中途强杀 / 低磁盘；中文 IME / vim / tmux / 大输出；WebUI 401 恢复 / 文件选择 / 下载；通知栏审批面；Android 10 / 14；16 KB 页；Linux sshd；Tailscale |
| **阻断** | 模型凭据相关：headless 真实任务、Session / Resume / Fork、工具调用与审批、MCP 工具被模型调用、Subagent / Agent Team（PR-07）；DSH 默认工作区无法创建（guest 无 Documents，PR-10 计划）；在线更新：`ReleaseTrust.BuiltIn` 为空 → `UPDATE_NO_TRUSTED_KEYS`（PR-02），无下载器 / 安装事务 / UI（PR-04 … 06） |

FEATURES.md 各行的状态列已按上表改写（证据列保留原始记录）；COMPATIBILITY.md 删除了已不存在的 Gradle / AGP / Kotlin 工具链行。

## 3. 下一次真机回归的最小清单（PR-01 合入后执行）

在 .NET 11 GA 的 **Release** 构建上、Android 17 arm64 真机：

1. 安装（同签名覆盖）→ 冷启动 → 启动 DSH → WebUI → 停止，`ps -A` 无 `libproot.so` / `node` / `bash`。
2. 开发者选项「终端 PTY 自检」PASS（覆盖 #41 的计时改动）。
3. 「安装内置 DSH」在已装同版本时保持不变；用较新的内置版本触发一次真实换层，过程中 `am force-stop`，重开后 `Recover()` 恢复为旧版或新版、DSH 可启动（#41）。
4. SAF 导入 / 导出各一次；插件 add / remove 各一次。
5. Android 10 或 14 至少一台：步骤 1、2。

## 4. 发布 / 更新链路核对与 PR-01、PR-02 切片

### 现状

- `.github/workflows/runtime-layers.yml`：仅手动；`proot`（x86_64）→ `layers`（arm64）构建 base / node / android-side bundle 与 DSH 层；
  填 `release_tag` 时用 `GITHUB_TOKEN` `gh release create` 到**本私有源码仓库**——设备无法匿名下载，**不构成在线更新源**。没有签名清单、
  没有 SBOM / 来源证明、Runtime 与 DSH 不能分开发布。
- `.github/workflows/android-dotnet.yml`：push main / PR；测试 + `dotnet build -c Release`；APK 未内置运行层（只有 Warning）；APK 用默认调试签名。
- `runtime-bundle/runtime-assets.lock` 仍指向 DSHBox v1.5.1；`runtime-build.lock` 锁 Node 24.19.0、DSH 0.2.1-rc.5（上游最新预发布 `dsh-v0.2.2-rc.3`，未验证，不得直接进 stable）。
- `ReleaseTrust.BuiltIn` 为空；`ReleaseDiscovery` 没有接入 Android 组合根；没有“每通道上次接受的 generatedAt”持久化。
- `DshMobile.Android.csproj`：`ApplicationId=org.example.dshmobile`、`ApplicationVersion=1`、`ApplicationDisplayVersion=0.1.0`。

### 本 PR 已做的最小加固（不改行为）

- `runtime-layers.yml` 两个 job 的 `actions/checkout` 设 `persist-credentials: false`：`layers` job 持有 `contents: write`，同一 job 里
  `npm ci` 会执行第三方依赖的安装脚本；此前 checkout 把可写 token 留在 `.git/config`，依赖代码可读取并推送到源码仓库。发布步骤只经 `GH_TOKEN` 取 token，不依赖 git 凭据。
  **该工作流是手动触发，本 PR 无法在 CI 中验证**，下次手动运行时确认。
- `android-dotnet.yml` 显式 `permissions: contents: read`，checkout 不保留凭据（此前未声明权限，取决于仓库默认设置）。

### 风险清单

1. **供应链**：构建与发布在同一 job（`contents: write` 与 `npm ci` 共存）；Actions 按 tag（`@v4`）而非 commit SHA 引用；Node 20 运行时弃用告警（actions/*@v4）。
2. **分发**：私有仓库 Release 不能匿名下载；跨仓库发布需要 GitHub App token 或专用凭据（`GITHUB_TOKEN` 不能跨仓库）。
3. **信任根**：发布私钥尚不存在；生成、托管（受保护 environment + 审批人）、公钥钉入都需负责人操作。私钥目录不得在仓库内。
4. **包标识**：`org.example.dshmobile` 改名后不能覆盖升级；Keystore 中的 SSH 私钥不可导出，换包名后需在新包重新生成并重新登记到主机。
5. **SDK**：RC2 → GA 同时影响 CoreCLR、trim、JNI 绑定与 NDK 载入；workload 版本须以 GA SDK 实测为准。
6. **APK 内容**：CI 的 Release APK 不含 android-side / DSH（Warning 而非 Error）；发行配置须决定“内置”还是“首启在线安装”，后者依赖 PR-02 … 06。
7. **16 KB 页**：链接参数正确，但没有 `readelf` LOAD 段对齐的 CI 检查，也没有 16 KB 设备运行记录。

### PR-01（.NET 11 GA 与正式包标识）切片

1. `global.json` → SDK `11.0.100` + GA workload set（装好后 `dotnet workload --version` 实测再填）；CI 同步；`docs/COMPATIBILITY.md` 工具链表。
2. 全部套件 + Android Release 构建；检查 trim / AOT 告警数量变化（基线 75 warnings）。
3. 真机：§3 清单（Release 构建）。
4. 包标识决策记录（新 ADR）：永久 `ApplicationId`、versionCode 规则、release keystore 托管；若改包名，写清迁移步骤（工作区可导出，Keystore 私钥不可迁移）。

验收门禁：CI 全绿且 Release APK 构建成功；真机步骤 1–2 通过并登记证据；包标识 ADR 获负责人批准。

### PR-02（发布基础设施与可信密钥，基于 ADR-0022）切片

1. 负责人：离线 `node tooling/release-manifest-sign.mjs keygen <仓库外私密目录>`；私钥放入受保护 GitHub environment（必需审批人）；确认公开发布位置与 stable / preview 清单固定 URL。
2. 代码：公钥加入 `ReleaseTrust.BuiltIn`（单独 commit，ADR-0022 登记 keyId）；`ReleaseDiscovery` 接入 Android 组合根（仍只读）；每通道 `generatedAt` 下限持久化，写失败如实报错。
3. CI：`release-publish.yml`——构建 job 只读、发布 job 单独授权；制品 SHA / 长度、签名清单、跨仓库上传、匿名回读验证；禁止未经验收自动晋升 stable。
4. 测试：过期、回放、换签、通道串流、TLS 降级、错误 URL、无公钥。

验收门禁：匿名拉取两文件并验签通过；篡改 / 过期 / 回放 / preview 冒充 stable 全部拒绝；私钥不出现在 APK、仓库、日志与 Actions artifact。

## 5. PR-01 结果（maiji1990/dsh_mobile#44，已合并）

| 项 | 结果 |
|---|---|
| SDK / workload | CI 实装 `11.0.100`（`dotnet-install: Installed version is 11.0.100`）、workload set `11.0.100.1`；Android 运行时包 `11.0.0`（GA） |
| CI run [43797605544](https://github.com/maiji1990/dsh_mobile/actions/runs/43797605544)（SDK 提交） | Core 35 / Runtime 811（+1 跳过）/ Application 115 / guest 37；Android Release 构建 75 warnings / 0 errors（与 RC2 基线 75 相同） |
| CI run [43798009073](https://github.com/maiji1990/dsh_mobile/actions/runs/43798009073)（包标识提交） | Core 35 / Runtime 814（+1）/ Application 115 / guest 37；构建日志 `DSH Mobile package io.github.maiji1990.dshmobile 0.1.0 versionCode 10000 (ADR-0023)`；75 warnings / 0 errors |
| 包标识 | `io.github.maiji1990.dshmobile`，versionCode 由版本号推导（ADR-0023）；发布签名 keystore 待负责人生成 |
| 真机 | **未做**：§3 清单（Release 构建）仍待执行；新包名是新应用，迁移步骤见 ADR-0023 |

## 6. PR-02 状态（发布基础设施与可信密钥）

| 切片 | 状态 |
|---|---|
| 公开发布位置 | 已定：本仓库已公开，通道清单在 release `channel-stable` / `channel-preview`（ADR-0022 修订） |
| 防重放下限持久化 + 只读检查接入 App | 已实现（`ReleaseAcceptanceStore`、`UpdateChecker`、开发者选项「检查更新（只读）」）；单测；**无真机证据** |
| `release-publish.yml` | 已实现；本地演练通过；**未运行**（需 environment `release-signing` + secret + 公钥 + 至少一个运行层 Release） |
| 发布公钥 | **待负责人**：`release-trusted-keys.txt` 为空 → App 如实报 `UPDATE_NO_TRUSTED_KEYS`、不联网；工作流门禁失败 |
| 首个通道清单 | 依赖 PR-03（运行层 / DSH 层在本仓库发布版本化 Release）与公钥 |

PR-02 验收门禁（方案）：匿名拉取两文件并正确验签（工作流 verify job）；伪造 / 篡改 / 过期 / 回放 / preview 冒充 stable 全部拒绝（单测已覆盖，
真机待公钥就绪后用开发者选项复核）；私钥不进 APK 或 Actions artifact（签名只在一个步骤以 secret 注入）。

## 7. PR-03 状态（运行层 / DSH 层分开构建与发布）

| 切片 | 状态 |
|---|---|
| 两个独立作业（runtime：base / node / android-side；dsh：DSH 层） | 已实现（`runtime-layers.yml`）；锁定值与 SHA 校验沿用 `runtime-build.lock` |
| 可复现性 | 每个产品构建两次比对 `SHA256SUMS`，结果写入验收报告（不一致只告警，不宣称可复现） |
| 验收 | arm64：`SelfBuiltRuntimeBundleTests`（App 导入器 + DSH 安装器，缺产物即失败）；chroot guest 布局 + App 的 `dsh-probe.mjs`：web / sdk / sdk-minimal / acp 须 pass，headless pass 或 limited；plugin 留给真机 |
| 来源 | DSH 层 `provenance.json`：npm integrity（来自锁文件）+ 上游 tag `dsh-v<版本>` 的 commit（`git ls-remote`，CI 中解析）；没有 commit 时拒绝发布 |
| 发布 | 手动：`publish_runtime` / `publish_dsh` 分别创建不可覆盖的 `runtime-<v>` / `dsh-<v>` Release；发布作业不跑 `npm ci` |
| `runtime-assets.lock` | **未改**：仍指 DSHBox v1.5.1；自有 Release 发布并真机验收后单独改 |
| DSH `0.2.2-rc.3`（preview） | **未做**：上游升级，按规则 8 另起 PR（新 `package.json` / lockfile、Node `>=24.9.0` 已满足、兼容性对照） |
| 真机 | **未做**：chroot 不是 Android / PRoot |

**CI 结果**（maiji1990/dsh_mobile#46，`runtime-layers` run [43807148968](https://github.com/maiji1990/dsh_mobile/actions/runs/43807148968)，head `df7a4b6`）：

| 项 | 结果 |
|---|---|
| 可复现性 | runtime 与 dsh 两次构建均逐字节相同；与前一次运行（43806511282）的哈希也相同（共 4 次独立构建一致）：`dshmobile-runtime-debian-arm64-0.2.0.zip` = `204d8489…3daba`，`0.2.1-rc.5.tar.zst` = `b8d45456…37352`（DSH 层 90,615,319 字节） |
| App 验收 | `SelfBuiltRuntimeBundleTests` 通过（App 的 `LayerInstaller` 导入、`DshLayerInstaller` 装 DSH 层，`DSHM_REQUIRE_SELF_BUILT=1`） |
| guest（chroot，arm64 Linux） | Debian 13.7 / aarch64 / node v24.19.0；`dsh --version` = 0.2.1-rc.5；DSH web 按 App 方式启动，`GET /` → **401**；探测 web **pass**、headless **limited**（需凭据）、sdk **pass**、sdk-minimal **pass**、acp **pass** |
| 来源 | `provenance.json`：npm integrity `sha512-WkyJZv4z…`，上游 tag `dsh-v0.2.1-rc.5` → commit `b830ec950faba0a46dda564be3e2d854afed94f1` |

**发现（待处理）**：DSH 层 `npm ci` 输出 `npm warn allow-scripts 5 packages have install scripts not yet covered by allowScripts`——
`@deepseek-ai/dsh-subprocess-local`（postinstall `ensure-spawn-helper.mjs`）、`koffi`、`node-pty`（prebuild / node-gyp）、`protobufjs`、
`@google/genai`（no-op）。若该 npm 版本跳过未批准的脚本，层内可能缺少这些包安装时应完成的步骤（如 spawn-helper、node-pty 原生模块就位）。
chroot 探测未覆盖终端 / 子进程工具，**真机上自建 DSH 层从未运行过**（11-16 真机仍用 DSHBox 构建的 DSH 层）。发布 `dsh-0.2.1-rc.5` 前需：
核对层内 `node-pty` 的 `linux-arm64` 原生模块与 spawn-helper 是否存在且可执行，必要时在 `package.json` 中显式允许这几个包的脚本（单独 PR，规则 8）；
并在真机上用自建 DSH 层跑一次带 Bash 工具 / 终端的任务。
