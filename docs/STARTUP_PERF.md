# 启动性能观测（STARTUP_PERF）

> 状态：解析工具、测试与本文档已落地；**2026-11-17 已在 PTP-AN10 / Android 17 上完成 A/B/C 各 10 次真机采集**，结果与 CSV 见[真机记录](../migration/parity/2026-11-17-PTP-AN10-A17-startup-perf.md)（B 场景 `bridge.complete` P50 16.6 s，其中 97.6% 是 DSH 初始化）。D–F 场景、可交互测点与跟踪开销仍**待真机验证**（[AGENTS.md](../AGENTS.md) 规则 9）。本工具只读取 logcat 文本，不参与启动流程，也不改变任何启动语义。

## 目的与边界

目的是把 App 启动路径上的分段计时（Tag `DshMobile`，标记 `startup-perf`）变成可复现的 CSV 与 P50/P95 摘要，用于回答“时间花在哪个阶段”，并为优化候选排序。

边界：

- **只观测**：解析日志、生成 CSV、打印统计；不修改 C#、不改启动顺序、不改变权限审批、Accessibility 门禁、SSH 主机密钥校验或 guest 命令构造（规则 5、规则 11）。
- **不替代真机**：CI、模拟器或 chroot 的数据不能替代真机结论；没有真机日志的格子只能写「待真机验证」。
- **不记录敏感内容**：只导出固定 CSV 列；`details` 里除 `cacheHit` 外一律不进入 CSV、摘要或 stdout。详见[隐私禁记清单](#隐私禁记清单)。
- **派生分组是估算**：分组由同一 `runId` 的多个阶段相加/相减得到，不是新的插桩点；用于筛查，不作为精确归因。
- **样本量优先**：单次运行不足以下结论；常规重复启动至少 10 次（见[真机矩阵](#真机矩阵-af)）。
- **默认关闭**：`startup-perf` 行只在「开发者选项」开启时产生（`UiPrefs.StartupTraceEnabled`：默认跟随开发者选项，可用 `startup_trace` 覆盖；目前没有单独的 UI 行）。关闭时不格式化、不写任何行。开启方式：「更多 → 关于」连点 7 次。

## 日志格式与字段

Android `logcat -v threadtime` 行通常形如（App 自己还会再加一个 `HH:mm:ss.fff` 前缀）：

   11-17 16:31:42.123  1234  1250 I DshMobile: 16:31:42.123 startup-perf {"schema":1,"runId":"a1b2c3d4e5f6","stage":"guest_assets.total","elapsedMs":23.456,"sinceStartMs":102.789,"result":"ok","details":{"cacheHit":true}}

解析规则（[startup-perf.mjs](../tooling/startup-perf.mjs) 的 `parseLog`）：

1. 在一行里找子串 `startup-perf `；找不到则整行忽略。
2. 从它之后的**第一个** `{` 起，到**该行最后一个** `}` 止，作为 JSON 解析。
3. 解析失败、缺少必需字段、或 `schema` 不等于 1 的行被**跳过并计数**（不抛异常）；摘要里的 `skipped lines` 就是该计数。
4. 一行只有标记没有 JSON 同样按跳过处理。

字段（全部必需）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `schema` | int | 固定为 `1`；其它值跳过并计数 |
| `runId` | string | 单次启动的随机标识；分组、一致性检查都以它为单位 |
| `stage` | string | 阶段 id，见[阶段清单](#阶段清单) |
| `elapsedMs` | number | 该阶段计时值；不同阶段的**起点不同**，见下表 |
| `sinceStartMs` | number | 相对统一启动起点的时刻，仅用于把记录排到同一条时间线；**不要**拿它和 `elapsedMs` 混用 |
| `result` | string | `ok` \| `failed` \| `canceled` \| `busy` \| `incomplete` \| `refused` |
| `details` | object | 可能为空对象；当前键见下，工具只读取 `cacheHit` |

`details` 目前会出现的键：`cacheHit`(bool)、`extracted`(bool)、`written`(int)、`scanned`(int)、`removed`(int)、`probes`(int)、`status`(int)、`pid`(int)、`attempt`(int)、`revision`(int，launch token 代次计数，不是 token 本身)、`stream`("stdout"/"stderr")、`code`(string，错误码常量)。

字符串值有硬性限制：只接受大写错误码（如 `RUNTIME_NOT_INSTALLED`）或 `stdout` / `stderr` / `none` / `unknown`，其它任何字符串都写成 `[redacted]`（token、URL、prompt 都匹配不上，见 [StartupTrace.cs](../src/DshMobile.Core/Diagnostics/StartupTrace.cs) 的 `IsDetailText`）。

### elapsedMs 的三种基准

- **阶段自身耗时**：大多数 `coordinator.*`、`guest_assets.*`、`host.preconditions`、`host.proc_scan`、`sandbox.*`、`host.cleanup_processes`、`host.lock_sweep`、`dsh.command_env`、`dsh.spawn`、`web.activity_create`。
- **从 DSH spawn 起算（累计）**：`dsh.first_output`、`dsh.token_available`、`http.first_port_open`、`http.first_response`、`host.ready`。
- **从 LoadUrl 起算（累计）**：`web.load_url`、`web.page_start`、`web.commit_visible`、`web.page_finish`；`web.activity_create` 例外，是 activity 创建耗时。
- `bridge.complete` 是整条启动链路的总耗时（下文 `host_total` 的来源）；`ui.click_to_bridge` 是点击到 bridge 调用的等待。

## 阶段清单

锚点只指向以下真实文件（缩写即链接）：

- [StartupTrace.cs](../src/DshMobile.Core/Diagnostics/StartupTrace.cs)
- [RuntimeCoordinator.cs](../src/DshMobile.Application/Runtime/RuntimeCoordinator.cs)
- [DshHost.cs](../src/DshMobile.Runtime/Hosting/DshHost.cs)
- [GuestAssetProvisioner.cs](../src/DshMobile.Runtime/Guest/GuestAssetProvisioner.cs)
- [GuestToolchain.cs](../src/DshMobile.Runtime/Guest/GuestToolchain.cs)
- [BridgeHost.cs](../src/DshMobile.Android/Bridge/BridgeHost.cs)
- [DshWebActivity.cs](../src/DshMobile.Android/DshWebActivity.cs)
- [CSharpWebUiController.cs](../src/DshMobile.Android/WebUi/CSharpWebUiController.cs)

| 阶段 id | 含义 | `elapsedMs` 起点 | 代码锚点 |
|---|---|---|---|
| `ui.click_to_bridge` | 点击「启动 DSH」到 start 操作真正开始（排队等待） | 阶段自身 | BridgeHost.cs（`RunStartAsync` / `RunExclusive`） |
| `coordinator.lock_wait` | 获取 RuntimeCoordinator 单写者锁的等待 | 阶段自身 | RuntimeCoordinator.cs |
| `coordinator.sync` | 状态机与磁盘 journal 同步 | 阶段自身 | RuntimeCoordinator.cs |
| `coordinator.journal` | journal 写入/回放 | 阶段自身 | RuntimeCoordinator.cs |
| `guest_assets.total` | guest 资产准备总耗时（包含下列子阶段） | 阶段自身 | RuntimeCoordinator.cs |
| `guest_assets.shim` | `link-shim` 资源校验/写入 | 阶段自身 | GuestAssetProvisioner.cs |
| `guest_assets.scripts` | `scripts` 资源校验/写入 | 阶段自身 | GuestAssetProvisioner.cs |
| `guest_assets.corepack` | `corepack` 缓存校验/解包 | 阶段自身 | GuestToolchain.cs |
| `guest_assets.wrappers` | `wrappers` 资源校验/写入 | 阶段自身 | GuestAssetProvisioner.cs |
| `host.preconditions` | 宿主前置条件检查（目录、磁盘、ABI、权限） | 阶段自身 | DshHost.cs |
| `host.proc_scan` | 扫描残留进程 | 阶段自身 | DshHost.cs |
| `sandbox.spawn` | PTY/沙箱进程 spawn | 阶段自身 | DshHost.cs |
| `sandbox.fixed_delay` | spawn 后的固定等待 | 阶段自身 | DshHost.cs |
| `host.cleanup_processes` | 清理残留进程 | 阶段自身 | DshHost.cs |
| `host.lock_sweep` | 清理锁/陈旧标记 | 阶段自身 | DshHost.cs |
| `dsh.command_env` | 构建 DSH 命令与环境 | 阶段自身 | DshHost.cs |
| `dsh.spawn` | 启动 DSH 进程 | 阶段自身 | DshHost.cs |
| `dsh.first_output` | 从 DSH spawn 到首行输出 | 从 DSH spawn 起算 | DshHost.cs |
| `dsh.token_available` | 从 DSH spawn 到 launch token 可解析 | 从 DSH spawn 起算 | DshHost.cs |
| `http.first_port_open` | 从 DSH spawn 到本地端口可连接 | 从 DSH spawn 起算 | DshHost.cs |
| `http.first_response` | 从 DSH spawn 到首个 HTTP 响应 | 从 DSH spawn 起算 | DshHost.cs |
| `host.ready` | 从 DSH spawn 到宿主判定 READY | 从 DSH spawn 起算 | DshHost.cs |
| `bridge.complete` | 从点击到 start 操作结束（`host_total`）；busy / 异常也记一条 | 点击时刻 | BridgeHost.cs（`RunStartAsync` / `RunExclusive`） |
| `web.activity_create` | WebView Activity 创建耗时 | 阶段自身 | DshWebActivity.cs |
| `web.load_url` | 发出 `LoadUrl` 的时刻（`elapsedMs` 恒为 0，仅作时间线标记） | 从 LoadUrl 起算 | CSharpWebUiController.cs |
| `web.page_start` | 从 LoadUrl 到 `OnPageStarted` | 从 LoadUrl 起算 | CSharpWebUiController.cs |
| `web.commit_visible` | 从 LoadUrl 到 `OnPageCommitVisible` | 从 LoadUrl 起算 | CSharpWebUiController.cs |
| `web.page_finish` | 从 LoadUrl 到 `OnPageFinished` | 从 LoadUrl 起算 | CSharpWebUiController.cs |

不要混用 `sinceStartMs` 与 `elapsedMs`：前者是统一的绝对时刻（用于排序/关联），后者按上表各自的起点计时；把两者相减或相加会得到错误的阶段耗时。

## 真机采集

每次启动前清空 logcat 并重新开始抓取，一次只采集一次启动（或一个明确的场景）：

    adb logcat -c
    adb logcat -v threadtime -s DshMobile:I '*:S' > startup-A-01.log
    # 在手机上点击启动 / 进入 WebUI；结束后 Ctrl+C 停止抓取

- 采下一个场景前先 `adb shell am force-stop org.example.dshmobile`（冷启动场景），或按场景要求保留进程（热启动）。
- 厂商 ROM 的 logcat 环形缓冲可能很快被冲掉；长时间采集应持续写入文件，而不是事后 `logcat -d`。
- 过滤条件只保留 `DshMobile:I`，其余 tag 静默；工具本身能容忍混入的其它日志行。
- 不要把含 token、URL query、认证头、prompt、环境变量或用户文件内容的日志原样上传；先按工具导出固定列，或人工脱敏（见[隐私禁记清单](#隐私禁记清单)）。

## 生成 CSV 与 P50/P95

给 `--out` 时 CSV 写入文件，stdout 为空；摘要与 warning 一律写 stderr。省略 `--out` 时 CSV 才写到 stdout，摘要仍在 stderr。

    node tooling/startup-perf.mjs \
      --log startup-A-01.log \
      --out artifacts/startup-perf/A-01.csv \
      --scenario A --device "PTP-AN10" --android 17 \
      --commit <apk-sha> --node <node-version> --dsh <dsh-version> \
      2> artifacts/startup-perf/A-01.summary.txt

或只把 CSV 写到 stdout：

    node tooling/startup-perf.mjs --log startup-A-01.log --scenario A > A-01.csv 2> A-01.summary.txt

也支持从 stdin 读取（`-`）：

    adb logcat -d -v threadtime -s DshMobile:I '*:S' | node tooling/startup-perf.mjs --log - --scenario A > A.csv

参数：

| 参数 | 说明 |
|---|---|
| `--log <path>` | 必填；日志文件，`-` 表示 stdin |
| `--out <csvPath>` | 可选；写入 CSV。父目录不存在时报错并 exit 2 |
| `--scenario A` | 场景标签（A–F），写入 `scenario` 列 |
| `--device <name>` | 设备名，写入 `device` 列 |
| `--android <ver>` | Android 版本，写入 `android` 列 |
| `--commit <sha>` | APK commit，写入 `apkCommit` 列 |
| `--node <ver>` / `--dsh <ver>` | Node / DSH 版本，写入对应列 |
| `--help` | 打印 usage 并 exit 0 |

CSV 列顺序固定为：

    runId,scenario,device,android,apkCommit,nodeVersion,dshVersion,stage,elapsedMs,sinceStartMs,result,cacheHit

- `cacheHit` 仅当 `details.cacheHit` 是布尔时输出 `true`/`false`，否则为空。
- `details` 的其它键（`code`、`probes`、`stream` 等）**不会**出现在 CSV 里；摘要也不会打印 `details` 原文。
- `scenario`/`device` 等元数据为空时列仍保留，便于跨文件合并。
- 退出码：`0` 成功；无任何 `startup-perf` 记录 → `1`；参数非法、日志不可读、`--out` 目录不存在 → `2`（usage 写 stderr）。

摘要（stderr）包含：

1. `[stages]` 原始阶段表：每个阶段的 `n`、P50、P95。
2. `[derived groups]` 分组表：每组 `n`、P50、P95、P50 占 `host_total` P50 的百分比，以及 `candidate` 标记。
3. `[web.* stages]`：WebView 阶段单独列出，不参与 `host_total` 百分比。
4. `[run consistency]`：按 `runId` 列出缺少 `bridge.complete` 或分组之和超限的 warning。
5. `[optimization candidates]` 与判定规则说明。

P50/P95 使用 nearest-rank：升序排序后 `idx = ceil(p * n) - 1`（`p = 0.5/0.95`，`n >= 1`），摘要保留 1 位小数。样本量很小时 P95 会把最大值直接暴露出来，这是有意的保守行为。

## 分组公式（派生估算）

分组先按 `runId` 计算，再对每个组聚合 P50/P95。某个 `runId` 缺少组成项时，该组在这个 `runId` 上**跳过**（不补 0、不产生 NaN）；同一 `runId` 内重复出现的阶段取最后一条（重试场景）。

| 分组 | 公式 | 说明 |
|---|---|---|
| `coordinator` | `coordinator.lock_wait + coordinator.sync + coordinator.journal` | 三项缺一即跳过 |
| `guest_assets` | `guest_assets.total` | 已经是总耗时，不再加子阶段 |
| `sandbox` | `sandbox.spawn + sandbox.fixed_delay` | 两项缺一即跳过 |
| `cleanup` | `host.proc_scan + host.cleanup_processes + host.lock_sweep` | 三项缺一即跳过 |
| `node_init` | `http.first_response` | 从 DSH spawn 到首个 HTTP 状态 |
| `http_wait` | `host.ready - http.first_response` | 两者都缺时跳过；可能是负数，说明记录顺序/基准有问题 |
| `host_total` | `bridge.complete` | 百分比的分母 |
| `web.*` | 各 `web.*` 阶段原值 | 单独一组；`web.activity_create` 是 Activity 创建耗时，其余从 LoadUrl 起算；不计入 `host_total` 百分比 |

“P50 占 `host_total` P50 的百分比” = 组 P50 ÷ `host_total` P50 × 100%。不同分组可能来自不同计时起点、甚至阶段间有重叠，所以分组之和偶尔会大于 `host_total`；工具只在超过 `host_total × 1.05` 时给 warning，不判失败。这些分组是**派生估算**，用于排序候选，不应当当作新的测量点。

一致性检查（stderr 的 `[run consistency]`）：

- 缺少 `bridge.complete`：`warning: missing bridge.complete`。
- 可用分组之和 > `bridge.complete × 1.05`：`warning: group sum ... > host_total ...`。
- warning 只提示，不影响退出码；是否排查由采集者判断。

## 判定规则

对分组表（含单独的 `web.*` 行）：

- 组的 P50 ≥ `host_total` P50 的 **30%**，或该组 **P95 > 2000 ms**，标记为 `candidate`（进入优化候选）。
- `web.*` 没有 `host_total` 百分比，只按 P95 > 2000 ms 触发。
- `host_total` 自身不参与 candidate 标记。
- 30% / 2 s 只是**筛查阈值，不是 SLA，也不是发布门槛**；它不保证用户可感知的启动时间，也不替代真机体验结论。
- “常规重复启动”指 A/B/C 场景的正常完成运行（`result = ok`）；F 异常场景不纳入百分比结论。

## 真机矩阵 A–F

| 场景 | 操作 | 重点阶段 | 本轮要求 |
|---|---|---|---|
| A 仅页面 | DSH 保持 READY，关掉 WebUI 再打开（不点停止） | `web.*`（单独观察，不参与 `host_total` 百分比） | **至少 10 次** |
| B 典型重复启动 | 已装资产，正常停止 DSH（进程确认退出，不清用户数据）后启动 | 全部 host 阶段、`bridge.complete` | **至少 10 次** |
| C App 重启后启动 | 停掉 App 进程再打开并启动 DSH（.NET 冷启动 + 日志恢复） | `coordinator.*`、`guest_assets.*`、`host.*` | **至少 10 次** |
| D App 更新后首启 | 相同数据目录覆盖安装后第一次启动（资产状态 / 迁移） | `guest_assets.*`、`host.preconditions` | 按需（先备份），保留日志 |
| E 真实配置 | 含 MCP / 插件 / 大工作区的现有配置，仅只读采样 | `dsh.first_output`、`http.first_response`（插件/会话扫描） | 按需，保留日志 |
| F 温控 / 电源 | 不同温度、亮灭屏、是否充电下重复 B | 尾延迟（P95）与异常 `result` | 按需，保留日志 |

要求：

- A/B/C 各至少 10 次，且同一场景尽量固定设备、ROM、APK commit、Node/DSH 版本；不同构建不要混在同一个结论里。
- 每行 CSV 都带元数据列，合并多个文件后仍可按 `scenario`/`apkCommit` 分组。
- D–F 本轮不设次数门槛，但没有日志就不能声称通过；没有真机证据的结论一律写「待真机验证」（规则 9）。

## 隐私禁记清单

日志与 CSV **绝不能**包含：

- token、launch token、认证头（Authorization/Cookie 等）；
- URL query（含临时参数、端口之外的敏感信息）；
- prompt / 用户输入 / 会话内容；
- 环境变量（尤其是密钥、代理、凭据相关）；
- 用户文件内容或路径中的隐私信息。

工具侧的保证：

- CSV 只有固定 12 列；`details` 除 `cacheHit` 外不导出，摘要/警告也不打印 `details` 原文。
- 解析器只保留 `runId/stage/elapsedMs/sinceStartMs/result/cacheHit`，其余 JSON 字段在内存里即被丢弃（见测试中的隐私回归）。
- `runId` 是随机短标识，不用于关联用户身份。
- 采集和共享日志前仍需人工检查：logcat 可能混入其它 tag 或异常栈。

### PageFinished ≠ 可交互

`web.page_finish` 只表示主 frame 的 `onPageFinished`，不代表首屏可交互：JS 初始化、数据加载、输入法/滚动就绪都可能更晚。不要用它宣称“页面已可用”；WebView 目前没有可靠的可交互事件，真实可交互时间只能「待真机验证」。

## 已知限制

- **跟踪开销**：一次启动约 30 行短 Logcat（无文件 I/O、无额外 guest 命令）；目标建议 < 3%，**尚未在真机测量**。如果日志本身改变启动行为，先关掉开发者选项再谈数据（规则 5：不伪造成功）。
- **WebView 无可交互事件**：只有 `onPageStarted`/`commit`/`onPageFinished` 等近似点，无法覆盖首屏可交互。
- **基准混用风险**：`sinceStartMs` 是全局时刻，`elapsedMs` 按阶段起点；分组是跨基准的估算。
- **样本与丢行**：logcat 环形缓冲可能丢行；失败/取消的 `runId` 可能缺阶段，工具会跳过而不是补值。
- **单次方差大**：少于 10 次的结论不具代表性；P95 在小样本下等于最大值。
- **真机数据范围**：目前只有一台设备（PTP-AN10 / Android 17）、A/B/C 各 10 次；D–F 场景、可交互测点、跟踪开销都没有数据，CI/模拟器结果不能替代真机（规则 9）。

## 相关文件

- 工具：[tooling/startup-perf.mjs](../tooling/startup-perf.mjs)
- 测试：[tests/tooling/startup-perf.test.mjs](../tests/tooling/startup-perf.test.mjs)
- 计时发射点：[src/DshMobile.Core/Diagnostics/StartupTrace.cs](../src/DshMobile.Core/Diagnostics/StartupTrace.cs)
- 编排与 journal：[src/DshMobile.Application/Runtime/RuntimeCoordinator.cs](../src/DshMobile.Application/Runtime/RuntimeCoordinator.cs)
- DSH 宿主/HTTP 就绪：[src/DshMobile.Runtime/Hosting/DshHost.cs](../src/DshMobile.Runtime/Hosting/DshHost.cs)
- guest 资产：[src/DshMobile.Runtime/Guest/GuestAssetProvisioner.cs](../src/DshMobile.Runtime/Guest/GuestAssetProvisioner.cs)
- WebView 阶段：[src/DshMobile.Android/WebUi/CSharpWebUiController.cs](../src/DshMobile.Android/WebUi/CSharpWebUiController.cs)、[src/DshMobile.Android/DshWebActivity.cs](../src/DshMobile.Android/DshWebActivity.cs)
- 首次真机测量（A/B/C 各 10 次 + CSV）：[migration/parity/2026-11-17-PTP-AN10-A17-startup-perf.md](../migration/parity/2026-11-17-PTP-AN10-A17-startup-perf.md)
- Web Profile 深度诊断（E5，CPU/ELU/分类）：[WEB_STARTUP_DIAGNOSIS.md](WEB_STARTUP_DIAGNOSIS.md)、[真机记录](../migration/parity/2026-11-19-PTP-AN10-A17-web-profile-diagnosis.md)
- 根因专项 T0/E1–E4/T1（2026-11-18）：[migration/parity/2026-11-18-PTP-AN10-A17-startup-experiments.md](../migration/parity/2026-11-18-PTP-AN10-A17-startup-experiments.md)（见 [STARTUP_EXPERIMENTS.md](STARTUP_EXPERIMENTS.md)）
- 真机证据规则：[AGENTS.md](../AGENTS.md)、[docs/COMPATIBILITY.md](COMPATIBILITY.md)、[docs/TEST_PLAN.md](TEST_PLAN.md)

最后更新：2026-11-17
