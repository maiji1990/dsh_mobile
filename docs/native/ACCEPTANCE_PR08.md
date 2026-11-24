# PR-08 系统治理、可靠性、在线更新探针与远程主机适配验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（`N-08-02` ～ `N-08-07`，`N-08-01` 已于 PR-04 先行落地；`N-08-04` / `N-08-05` 等未覆盖真机项见第 4–5 节）
> 分支：`feat/native-pr08-governance-remote-probe`（已合并 `origin/main` `e2295a3`）
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 无真机证据不勾 `[x]`、规则 11 不削弱安全边界）。
> 真机状态：**已真机验证（2026-11-24，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——`N-08-02`（后台空闲停机、租约保护）、`N-08-03`（31/32 预警与 API 34+ 开发者选项引导）、`N-08-06`（远程 SSH 原生对话 + 断线 + 重连 + `HOST_KEY_CHANGED` 拒绝）与 `N-08-07`（A/B/C 各 10 次）**通过**；`N-08-04` 的 MemoryLimiter 死因与温控阶梯、`N-08-05` 的在线更新探测提交/回滚/强杀恢复在本次环境**未能触发**（GitHub 清单不可达、无内存/发热压力源、无可导入更新层），按规则 5 / 规则 9 如实标注为**未验证**。详见第 5 节。

---

## 1. 本轮实现清单（`N-08-02` ～ `N-08-06`）

| 任务项 | 主要代码资产 | 说明 |
|---|---|---|
| `N-08-02`｜`WorkLeaseManager` 与 `IdleStopPolicy` | `src/DshMobile.Application/Runtime/WorkLeaseManager.cs`、`IdleStopPolicy.cs`、`src/DshMobile.Android/Governance/SystemGovernanceMonitor.cs`、`Bridge/AgentConnectionScope.cs`、`Bridge/BridgeHost.cs`、`DshMobileApplication.cs` | 覆盖 `WorkLeaseKind`（`Chat` / `Approval` / `Setup` / `Update` / `Reconcile`），默认单租约上限 10 分钟、连续活跃总上限 30 分钟；检测单调时钟倒退或负差值即清空全部活跃租约并归零计数；`IdleStopPolicy.Decide` 仅在「宿主运行中 + App 在后台 + 无活跃租约 + Agent 全 Idle + 后台空闲时长达阈值（默认 3 分钟）」时放行有序停机，且 `BridgeHost` 后台空闲停机循环（30 秒周期）不置位 `_userStopped`，与 `WarmupPolicy` 协同 |
| `N-08-03`｜Android 12+ 32 Phantom Process 预算监控与引导 | `src/DshMobile.Runtime/Supervision/PhantomProcessMonitor.cs`、`src/DshMobile.Runtime/RuntimeContracts.cs`、`Guest/ProotGuestProcess.cs`、`Hosting/DshHost.cs`、`src/DshMobile.Agent/Domain/Failures.cs`、`src/DshMobile.Android/Governance/SystemGovernanceMonitor.cs`、`RuntimeUi/RuntimeActivity.cs` | 基于只读 `ProcessTable` 统计同 UID 子进程数（排除 App 主进程），默认上限 32、≥28 触发预警（`NearLimit`）；在 `RuntimeActivity` 运行环境页展示计数与告警，并在 API 34+ 提供一键跳转开发者选项引导（只引导，绝不尝试篡改系统设置）；`ProotGuestProcess` 与 `DshHost` 将 `exit 137` 标记为 `RuntimeErrorCodes.PhantomProcessLimit`，`DshFailureMapper` 映射至 `DshFailureCode.PhantomProcessLimit` |
| `N-08-04`｜Android 17 Memory Limiter 死因分类与 `HeatTierPolicy` | `src/DshMobile.Application/Runtime/HistoricalExitClassifier.cs`、`HeatTierPolicy.cs`、`WarmupPolicy.cs`、`src/DshMobile.Agent/Domain/Failures.cs`、`src/DshMobile.Android/Governance/SystemGovernanceMonitor.cs`、`Bridge/BridgeHost.cs`、`DshMobileApplication.cs`、`RuntimeUi/RuntimeActivity.cs` | 启动在后台读取 `ActivityManager.GetHistoricalProcessExitReasons()` 匹配 `MemoryLimiter:AnonSwap` / `MemoryLimiter`，经 `HistoricalExitClassifier` 仅保留 `HistoricalExitKind.MemoryLimited` 与固定安全诊断键 `android.exit.memory_limiter_anon_swap`，绝不存储或透传原始 OS 描述，且 `DshFailureMapper.FromRuntimeErrorCode` 映射到 `DshFailureCode.MemoryLimited`；注册 `PowerManager.IOnThermalStatusChangedListener` 结合 `ThermalHeadroomSampler`（强制 `PowerManager.GetThermalHeadroom(30)` 最小间隔 ≥30 秒）与电量/省电开关由 `HeatTierPolicy.Evaluate` 划分 `Nominal / Moderate / Severe / Critical`，通过扩展后的 `WarmupPolicy.Decide` 在中度及以上发热或低电时抑制后台预热与并发，绝不强杀活跃前台会话 |
| `N-08-05`｜抽象在线更新健康探针（ADR-0024 / ADR-0029） | `src/DshMobile.Runtime/Hosting/DshHost.cs`、`src/DshMobile.Application/Runtime/RuntimeCoordinator.cs`、`Updates/UpdateCoordinator.cs`、`tests/DshMobile.Application.Tests/FakeDshHost.cs` | 新增 `DshHealthProbeResult` 与 `IDshHost.ProbeHealthAsync(CancellationToken)`；`DshHost.ProbeHealthAsync` 执行自包含探针（校验布局、按 `DshProfile.Web` 启动进程并等待 HTTP 就绪与读取版本后优雅停机/清理，跳过 Route B UI 握手）；`RuntimeCoordinator.CheckDshReplacementAsync` 与 `UpdateCoordinator.ProbeAsync` 统一切换至 `ProbeHealthAsync`（不再直接编排 `StartHostAsync` + `StopHostAsync`） |
| `N-08-06`｜远程主机 `RemoteSshSession` 原生会话接入 | `src/DshMobile.Runtime/Remote/RemoteSshSession.cs`、`src/DshMobile.Agent/Transport/RemoteConnectionOptions.cs`、`DshRemoteConnection.cs`、`src/DshMobile.Android/Bridge/AgentConnectionScope.cs`、`Remote/RemoteActivity.cs`、`Chat/ChatActivity.cs`、`Chat/ChatFragment.cs`、`Resources/layout/activity_remote.xml`、`Resources/values/strings.xml` | `RemoteSshSession` 实现 `IDshTokenSource` 并暴露 `LoopbackBaseUri`（`http://127.0.0.1:{port}/`，仅驻留内存，断线或停用立即清空并递增 `TokenRevision` 触发 `TokenChanged`）；`DshRemoteConnection` 支持动态 `baseUriProvider`，每次建连经 `RemoteConnectionOptions.ValidateLoopbackBaseUri` 校验只允许回环地址；`AgentConnectionScope` 维护独立 `RemoteTargetBundle`，`RemoteActivity` 新增「打开原生聊天」入口跳转 `ChatActivity(ExtraIsRemote=true)`，不污染本地会话缓存并完整保留 Keystore 不可导出私钥与 `HostKeyPin` 校验 |

---

## 2. 安全、架构与可靠性边界核对

| 检查项 | 核对结果 |
|---|---|
| 纯托管边界 | `DshMobile.Agent` 与 `DshMobile.Application` 保持纯 `net11.0`，零 `Android.*` / `Java.*` 依赖，通过 `ArchitectureBoundaryTests` |
| 零原始秘钥/OS 描述留存 | `RemoteSshSession.Token` 仅驻留内存；`HistoricalExitClassifier` 仅在内存中匹配关键字并丢弃 `ApplicationExitInfo.Description`；通过 `LoggingPrivacyTests` 与 `SavedStateSecurityTests` |
| SSH 主机密钥与转发约束 | `RemoteConnectionOptions.ValidateLoopbackBaseUri` 拒绝非回环主机与越界 scheme；未修改 `HostKeyPin` / `HOST_KEY_CHANGED` 校验与 Keystore 私钥不可导出约束 |
| 系统设置不篡改 | `PhantomProcessMonitor` 仅读取只读 `/proc` 快照；API 34+ 仅触发开发者选项跳转引导 |
| 后台治理不强杀前台 | `IdleStopPolicy` 严格要求 `inBackground && !hasActiveLease && agentIdle && backgroundIdleDuration >= threshold`；`HeatTierPolicy` 只限制后台预热与并发，不终止活跃对话 |

---

## 3. 桌面测试与 Release 构建基线（已实测通过）

> `.NET` 测试与 `Release` 构建于最后一次源码定稿后执行并通过；`tests/guest` 于合入 `origin/main` `e2295a3` 后复跑通过（注：`Runtime.Tests` 含既有 Windows 平台跳过的 `XdgUserDirShimTests` 2 项）：

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests --no-restore` | **109 通过 / 0 失败 / 0 跳过（总计 109）** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests --no-restore` | **879 通过 / 0 失败 / 2 跳过（总计 881）**（含 `GovernanceRuntimeTests`：32 预算监控、`exit 137` 归类、`ProbeHealthAsync` 前置校验、`RemoteSshSession` 令牌/回环契约） |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests --no-restore` | **151 通过 / 0 失败 / 0 跳过（总计 151）**（含 `GovernancePolicyTests` 与 `RuntimeCoordinatorTests`/`UpdateCoordinatorTests` 在线探测提交/回滚/强杀恢复覆盖） |
| Agent 单测（含 5 组 Quality 门禁） | `dotnet test tests/DshMobile.Agent.Tests --no-restore` | **96 通过 / 0 失败 / 0 跳过（总计 96）**（含 `RemoteGatewayAdapterTests` 动态 SSH 回环重连与 `PhantomProcessLimit` / `MemoryLimited` 错码映射） |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败（退出码 0）** |
| Android Release 裁剪构建 | `dotnet build src/DshMobile.Android -c Release --no-restore` | **0 错误 / 74 警告**（PR-08 新增与修改文件 0 新增警告） |

---

## 4. 真机验收矩阵

| 编号 | 任务对应 | 真机验收要点 | 当前状态 |
|---|---|---|---|
| `PR08-D01` | `N-08-01` / `N-08-02` | 后台静默空闲过阈值触发优雅停机、活跃生成/审批/更新租约保护不被误杀、杀进程与 T11 孤儿零残留验证 | **部分通过（2026-11-24）**：3 min 阈值 `idle-stop: graceful ok (Ready)`、Chat 租约背景 4 min 未误杀、`am force-stop` 后 guest 进程 0 残留；仅杀 App 进程的孤儿回收（reaper）因非 root 无法制造，仅单测覆盖 |
| `PR08-D02` | `N-08-03` | 高子进程压力下运行环境页 ≥28 预警、API 34+ 跳转开发者选项，以及 `exit 137` UI 错误提示展示 | **部分通过（2026-11-24）**：31/32「已接近上限…可能触发 SIGKILL(137)」预警与开发者选项跳转通过；32 上限时系统即时回收新子进程、未产生可见 `exit 137` UI 错误码（登记 F25） |
| `PR08-D03` | `N-08-04` | Android 17 `MemoryLimiter:AnonSwap` 死因恢复展示与中重度温控抑制后台预热验证 | **未验证（2026-11-24）**：正常态面板显示「未检测到异常内存终止记录」与 Nominal/允许/4；MemoryLimiter 未触发，低电模拟（`dumpsys battery set level 5` + unplug）不改变策略（BatteryManager 仍报 charging），中重度温控未制造 |
| `PR08-D04` | `N-08-05` | 真实 DSH / Runtime 在线更新经 `ProbeHealthAsync` 探测提交、人为损坏层回滚及安装中途强杀恢复 | **未验证（2026-11-24）**：`检查更新` 返回 `MANIFEST_FETCH_FAILED: github.com: timed out after 6 s and the api.github.com fallback did not answer`（本网络不可达），无更新层包可导入，提交/回滚/强杀均未覆盖 |
| `PR08-D05` | `N-08-06` | `RemoteActivity` 连接真实远程 SSH 主机 → 启动远程 DSH → 点击「打开原生聊天」完整对话、断线检测与重连恢复（及 `HOST_KEY_CHANGED` 拒绝回归） | **通过（2026-11-24）**：指纹核对固定（SHA256 一致）、`启动远端 DSH`（转发 41183→8791、HTTP 401、token 持有）、原生对话 `REMOTE_OK`；杀 SSH 会话后 `transport.timeout` + 「上条消息发送状态未确认」；重连后 `RECOVERED_OK`；更换主机密钥后按固定值拒绝。**注：远端用本机 WSL sshd + `adb reverse`，非真实 LAN 路径** |
| `PR08-D06` | `N-08-07` | 按 `docs/STARTUP_PERF.md` 完成 A/B/C 各 10 次冷启/热启/RSS/空闲 CPU 配对采集并生成 `docs/native/PERFORMANCE_BASELINE.md` | **部分通过（2026-11-24）**：A/B/C 启动计时各 10 次、570 条记录、0 跳过；`bridge.complete` B P50 16.83 s / C 17.98 s，`node_init` 占 95.5% / 94.8%，见 [PERFORMANCE_BASELINE.md](PERFORMANCE_BASELINE.md)；RSS 与空闲 CPU 未纳入本轮脚本采集 |

---

## 5. Android 17 真机验收（2026-11-24 09:16–10:45）

- 设备：Xiaomi M2007J1SC（LineageOS 24.0，Android 17 / API 37，arm64，userdebug，非 root，Wi‑Fi 192.168.137.61 可达外网）。
- APK：Release `io.github.maiji1990.dshmobile-Signed.apk`，SHA-256 `C0A35590B3EF5B9D266BEFE891C955C767AA8BE49996953C714EE07532B430E8`（覆盖安装，数据保留）；DSH `0.2.2-rc.5`，guest Node `26.13.1`。
- 证据目录：`artifacts/pr08/`（UI dump、截图、脚本），`artifacts/startup-perf/pr08/`（30 份 logcat 与 CSV）。

### 5.1 `N-08-02` 后台空闲停机与租约保护（PR08-D01，部分通过）

1. 首页「启动 DSH」→ `DSH 已就绪` → 按 Home 后台：09:18:35 起算，09:21:35 logcat 出现 `idle-stop: graceful ok (Ready)`；`/proc` 中 guest 进程数由 4 → 0（仅剩 App 主进程），回到 App 显示「已停止 / DSH 未启动」，与 3 min 阈值、30 s 巡检一致。
2. 租约保护：在原生会话发送长回答提示并立即后台（09:29:02），到 09:33:02（t+240 s，已过阈值 60 s）仍无 `idle-stop` 日志、guest 进程保持 3 个；运行环境页显示「活跃租约 1 项：Chat」。回合结束后回到前台仍为「已就绪」，证明 Chat 租约阻止了误杀。
3. T11 残留：运行中执行 `adb shell am force-stop io.github.maiji1990.dshmobile`，3 s 内 `libproot.so` / `node` 全部消失（0 残留），重新启动宿主后 `host.orphan_reap` 阶段正常。
4. 未覆盖：设备非 root，`am kill` 对前台服务无效、`kill -9 <app pid>` 返回 `Operation not permitted`，无法制造「仅 App 进程死亡、子进程被 init 收养」的状态，故 `NativeOrphanReaper` 的多条件回收仍只有单测证据（如实记录，不勾 T11 的孤儿分支）。

### 5.2 `N-08-03` 32 幻影进程预算（PR08-D02，部分通过）

1. 通过原生会话让 guest 启动 26 个 `setsid sleep 900`（app UID 子进程），运行环境页显示 `已接近上限（31 / 32）：可能触发系统 SIGKILL(137)，点此跳转开发者选项检查幻影进程限制`（截图 `artifacts/pr08/phantom-warning.png`）。
2. 点该行成功跳转 `com.android.settings/.Settings$DevelopmentSettingsActivity`（API 37 引导路径），返回后治理面板仍可用。
3. 继续追加子进程时计数**钉在 32 不再增长**（新 `sleep` 被系统即时回收），因此没有出现带 `exit 137` 的 UI 错误码；`kill -9 $$` 在 guest 内只得到工具级 `killed by signal: SIGKILL`，未经过 `ProotGuestProcess`/`DshHost` 的 `PHANTOM_PROCESS_LIMIT` 映射。登记 F25：32 上限下 `exit 137` 的 UI 归因缺少可复现构造方法。
4. 清理：`pkill -f 'sleep 900'`，计数回到 5–6。

### 5.3 `N-08-04` MemoryLimiter 与温控阶梯（PR08-D03，未验证）

- 治理面板正常态：「子进程预算 5 / 32」「正常（Nominal）· 后台预热：允许 · 后台并发：4」「当前无活跃租约」「未检测到异常内存终止记录」。
- 尝试 1（内存压力）：guest 内 `node` 大块分配脚本未真正起量（`MemAvailable` 3.8 GB 稳定），App 未被 MemoryLimiter 终止，重启后仍为「未检测到」。
- 尝试 2（低电）：`dumpsys battery unplug` + `set level 5`（AC/USB false）后运行环境页仍显示「允许 / 4」——`BatteryManager.IsCharging` 在 `status=FULL` 下仍为 true，`WarmupPower` 的 lowBattery 分支未被触发；`cmd power set-mode 1` 也未打开 Battery Saver。
- 中重度温控（ThermalStatus ≥2 / Headroom ≥0.85）无法在冷机短时内制造，未验证。
- 结论：`HistoricalExitClassifier` / `HeatTierPolicy` 的真机正向路径仍缺证据；UiPrefs 低电判定在模拟电量的设备上不可构造。

### 5.4 `N-08-05` 在线更新探针（PR08-D04，未验证）

- 运行环境页「检查更新」→ `检查更新未成功` / `MANIFEST_FETCH_FAILED: github.com: timed out after 6 s and the api.github.com fallback did not answer`（设备可 ping 223.5.5.5，但 GitHub 不可达）；首页同时出现「上次「检查更新」未成功」失败横幅，错误如实上报。
- 无可用的更新清单/层包，无法执行「经 `ProbeHealthAsync` 探测提交 → 人为损坏层回滚 → 安装中途强杀恢复」链路；本轮不宣称通过。

### 5.5 `N-08-06` 远程 SSH 原生会话（PR08-D05，通过；路径为测试隧道）

环境：本机 WSL Ubuntu 26.10 运行 OpenSSH sshd 与 `dsh 0.2.1-rc.5`，`adb reverse tcp:2222 tcp:22` 把设备的 `127.0.0.1:2222` 转到 PC 的 `127.0.0.1:22`（**非真实 LAN 路径**，ADR-0032 的局域网权限分支未覆盖）。

1. 主机页填写 `WSL-DSH / 127.0.0.1 / root / 2222 / 8791`，手机公钥（Keystore，ecdsa-sha2-nistp256…）加入 WSL `authorized_keys`；`保存主机` → `连接` 弹出「核对主机指纹」，设备显示 `SHA256:4MriWKkbmdHui8s24vNiJC24mKRA57paTPEd8Y6edz4`，与 `ssh-keygen -lf` 输出逐字一致后「一致，固定并连接」；状态为「连接实现：C#（SSH.NET）」。
2. `启动远端 DSH`：`dsh --profile web --port 8791` 输出 launch URL，App 显示「转发：127.0.0.1:41183 → remote 127.0.0.1:8791」「HTTP 401（鉴权生效）」「远端 launch token：已在本机原生侧持有」。
3. `打开远程原生对话` → 标题「远程 · WSL-DSH」，远端模型 `deepseek-official/deepseek-flash`、工作区 `/root/projects`；发送 `Reply with exactly REMOTE_OK` 收到 `REMOTE_OK`。
4. 断线：`kill -9` sshd 会话进程后，聊天页出现「仅显示部分历史（transport.timeout）」「上条消息发送状态未确认 / 连接中断，为避免重复执行工具，请先核对历史或手动确认重发」与「重试连接 / 核对并重连」；远程页状态为 `DISCONNECTED · UNREACHABLE · connection lost; connect again`。
5. 重连：重启 sshd → `连接` → `启动远端 DSH`（新转发 37279→8791）→ 再次对话收到 `RECOVERED_OK`。
6. 负向：替换 WSL 主机密钥（`SSH_HOST_ED25519` 重新生成）后 `连接`，App 拒绝并提示「主机密钥与已固定的不同，已拒绝连接。若确属主机重装，请删除该主机后重新添加并核对指纹。」（截图 `artifacts/pr08/remote-hostkey-rejected.png`）。
7. 副作用：该负向测试后设备上保留的主机指纹已与新密钥不符，后续如需复用须在远程页删除主机并重新核对指纹。

### 5.6 `N-08-07` A/B/C 性能配对（PR08-D06，部分通过：启动耗时已采、RSS/CPU 未采）

- 采集与解析命令：
  - `pwsh artifacts/pr08/collect-perf.ps1 -Serial 4bf97d61 -OutDir artifacts/startup-perf/pr08 -Runs 10`
  - `pwsh artifacts/pr08/analyze-perf.ps1 -Dir artifacts/startup-perf/pr08`（内部对 `{A,B,C}-input.log` 调用 `node tooling/startup-perf.mjs`）
- 见 [PERFORMANCE_BASELINE.md](PERFORMANCE_BASELINE.md)：A/B/C 各 10 次、`skipped lines = 0`；`bridge.complete` B P50 16.83 s（P95 20.37 s）、C P50 17.98 s（P95 19.05 s），`node_init` 占 95.5% / 94.8%，宿主 C# 阶段合计约 0.1 s，与 2026-11-17 基线（16.6 s）一致，无回归；RSS 与空闲 CPU 未在本轮脚本中采集。

### 5.7 本轮登记问题

| 编号 | 现象 | 影响 | 建议 |
|---|---|---|---|
| F24 | guest 内 DSH 的 `bash` 工具在默认 `workspace-write` 下报「No usable sandbox backend …」，必须 `/permission danger-full-access` 才能执行命令 | 真机上 Agent 的 shell 能力默认不可用，与「危险命令需审批」的预期不符 | 在运行层镜像内提供 bubblewrap/Landlock，或让会话默认预设与本机能力匹配并给出可操作提示 |
| F25 | 子进程达 32 上限时系统即时回收新进程，App 只显示预警，没有可复现的 `exit 137` 归因路径 | `N-08-03` 的 `PHANTOM_PROCESS_LIMIT` 映射缺乏真机证据 | 用系统 phantom killer 触发一次真实 137（如把开发者选项的幻影进程限制调低），或补一条注入式验收通道 |
| F26 | 模拟低电量（`dumpsys battery`）无法让 `WarmupPower` 进入 lowBattery 分支 | `N-08-04` 的低电抑制只能靠单测 | 验收时用真实低电或 `cmd power set-mode` 打开 Battery Saver 后复测 |

最后更新：2026-11-24

