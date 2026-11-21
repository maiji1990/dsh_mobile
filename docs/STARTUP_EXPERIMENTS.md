# 启动实验诊断（STARTUP_EXPERIMENTS）

> 状态：记录格式（JSONL schema 1）、C# 诊断器（固定变体、隔离目录、超时/取消/清理）、[report 工具](../tooling/startup-experiment-report.mjs)与[测试](../tests/tooling/startup-experiment-report.test.mjs)已落地；**2026-11-18 已在 Honor PTP-AN10 / Android 17 上完成 T0 + E1/E2/E3/E4 + T1 的首轮真机采集**，结果见[真机记录](../migration/parity/2026-11-18-PTP-AN10-A17-startup-experiments.md)（下文有摘要）。D–F 场景、CPU profile、compile cache 真实落盘后的收益仍**待真机验证**（[AGENTS.md](../AGENTS.md) 规则 9）。E5 Web Profile 的采集协议、报告工具与验收口径见 [WEB_STARTUP_DIAGNOSIS](WEB_STARTUP_DIAGNOSIS.md)；该切片**尚无真机 E5 数据**。2026-11-20 已在同一设备完成 E6（P0 候选优化）配对采集，结果见[真机报告](../migration/parity/2026-11-20-PTP-AN10-A17-P0-startup.md)。2026-11-21 完成 E7（P1 PRoot 优化）两轮配对，并把只读前缀缓存接进生产后做了 T0/T1/T0b 配对，见 [P1 生产配对报告](../migration/parity/2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md)。本诊断独立于生产启动，默认构建里没有入口，也不改变任何启动语义、权限审批或安全校验。

## 目的与边界

目的是把「启动到底慢在哪一层」拆成可复现的对照实验，而不是继续在整条启动链上猜：

- **E1**：PRoot / Node 进程本身的最小成本（从 `/bin/true` 到 Node 脚本和 link-shim）。
- **E2**：DSH CLI 入口的模块解析/加载成本（`--version`、`--help`、`--dump-config`）。
- **E3**：Web profile 从进程启动到首次 HTTP 响应的成本，以及生产配置 / 隔离最小配置 / 叠加覆盖层的差异。
- **E4**：Node compile cache 关闭 / 冷 / 热三种状态，且必须先证明缓存确实落盘。

边界：

- **独立诊断**：生产 `DshHost` / `BridgeHost` 的 start/stop 永远不会调用实验 runner；实验入口只在诊断构建的开发者选项里出现。
- **不改生产启动**：不改启动顺序、不改生产配置；E3-0 / E3-1 / E4 使用隔离目录，不写入生产 `user-data`（E3-P 是生产配置对照，见[安全模型](#安全模型)）。
- **不部署新运行时**：只使用设备上已经安装、校验过的运行时层；不下载、不导入、不替换层，也不启用新权限。
- **不降低安全校验**：不关闭 SSH 主机密钥校验，不绕过权限审批 / Accessibility 写入门禁，不引入 root / `su`，不把调用方输入拼进 shell。
- **只测量，不补数**：拿不到 CPU、RSS、缓存计数时写 `null`，不写 0、不估算；没有 marker 时 `guestFirstMarkerMs` 必须是 `null`（规则 5「不伪造成功」）。
- **终点不同不可相加**：`exit` 与 `web` 的耗时终点不同，E1/E2 与 E3/E4 不合并比较、不相加、不互相替代。
- **筛查不是 SLA**：本文的门槛用于决定下一步查什么，不是发布标准，也不是因果结论。

## 首次真机结果（2026-11-18，PTP-AN10 / Android 17）

同一次会话按 T0 → E1 → E2 → E3 → E4 → T1 执行；每变体 10 次（E4-B 为 1 次冷启动）。设备/APK/时间线与原始 CSV 见
[真机记录](../migration/parity/2026-11-18-PTP-AN10-A17-startup-experiments.md)。

| 变体 | 终点 | P50 (ms) | P95 (ms) |
|---|---|---:|---:|
| T0 / T1 `bridge.complete`（点击 → READY） | web | 18921.4 / 18814.0 | 23863.3 / 19571.2 |
| E1-0 `/bin/true` | exit | 14.7 | 20.5 |
| E1-2 `node -e` marker | exit | 60.1 | 62.7 |
| E2-1 `bin.js --version` | exit | 160.3 | 184.2 |
| E2-3 `--profile web --dump-config` | exit | 191.8 | 214.5 |
| E3-P 生产配置 | web | 16820.5 | 17440.0 |
| E3-0 隔离空配置 | web | 16061.2 | 21997.0 |
| E3-1 隔离 + 覆盖层 | web | 16004.4 | 17083.5 |
| E4-A 关闭 compile cache | web | 15401.3 | 17025.3 |
| E4-C 已预热 compile cache | web | 17126.3 | 18820.9 |

结论（定位，不是因果证明）：PRoot / Node / CLI 入口与配置合成都在 **0.2 s 以内**；隔离空配置的 Web 启动仍约 **16.0 s**，
生产配置只多约 0.8 s；compile cache 按「CLI 预热」方式**没有收益**，且 Web 进程被强杀时缓存文件为 0。剩余约 16 s 落在
DSH Web Profile 自身初始化，下一步应做 CPU profile（§7.1），在此之前不声称具体 V8 热点。注意 T0/T1 本次是 18.8–18.9 s，
比 2026-11-17 的 16.56 s 慢约 2.3 s（跨天/热状态差异），因此只做同批次、同终点的配对比较。

## E6：P0 候选优化配对结果（2026-11-20，PTP-AN10 / Android 17）

同一设备、同一次会话；隔离最小配置，每变体 10 次，**块内配对**（同一 iteration 内连续跑 E6-0 → E6-1 → E6-2 → E6-3），
终点统一为 `httpReadyMs`。统计口径与原始数据见[真机报告](../migration/parity/2026-11-20-PTP-AN10-A17-P0-startup.md)。

| 变体 | 内容 | 均值 (ms) | 配对 Δ vs E6-0 | 95% CI | 更快块数 | 符号检验 p |
|---|---|---:|---:|---|---:|---:|
| E6-0 | 隔离最小配置基线 | 17142.5 | — | — | — | — |
| E6-1 | `--preserve-symlinks` | 14487.2 | −2655.4（−15.5%） | [−3196.2, −2114.6] | 10/10 | 0.00195 |
| E6-2 | 只读 FS 缓存垫片 | 15348.9 | −1793.6（−10.5%） | [−2400.1, −1187.0] | 10/10 | 0.00195 |
| E6-3 | 两者叠加 | 12476.9 | −4665.6（−27.2%） | [−5211.3, −4119.8] | 10/10 | 0.00195 |

两项候选优化在隔离最小配置下都成立，且**近似可加**（各自收益相加 −4449 ms，实测叠加 −4666 ms，差 217 ms 在噪声内）。
但这两个开关**都还没有接入生产 argv**（`ProotCommandBuilder` 仍只加 link shim），宿主侧 300 ms 死等 / 探针加速也不在 E6
覆盖范围内，所以「生产冷启动变快」仍以 T1 vs T0 的配对结果为准（G6）。

生产配对已完成（2026-11-20，同一设备、同一运行层）：宿主侧 P0 改动让 `bridge.complete` 均值从 17 715 ms
降到 16 879 ms（相邻块配对 n=20，−836 ms / −4.7%，95% CI [−1 405, −267]，p=0.041），且 DSH 自身
`dsh.first_output` 无差异（生产对照成立）。细节见[生产配对报告](../migration/parity/2026-11-20-PTP-AN10-A17-T1-production-pairing.md)。

## E7：P1 PRoot 优化配对结果（2026-11-21，PTP-AN10 / Android 17）

同一设备、隔离最小配置，每变体 10 次；块内配对（同一 iteration 内连续跑 E7-0 → E7-4），终点 `durationMs` = `httpReadyMs`。
同一天跑了两轮：第一轮 08:46 起（后段设备熄屏），第二轮 09:37 起（全程亮屏）。两轮均 50/50 `ok`、0 失败、0 超时。
统计口径与生产接入见[真机报告](../migration/parity/2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md)。

| 变体 | 内容 | 第 1 轮 P50 | 第 2 轮 P50 | 第 1 轮 配对 Δ vs E7-0 | 第 2 轮 配对 Δ vs E7-0 |
|---|---|---:|---:|---|---|
| E7-0 | 新 PRoot 基线（仅 `01-statx-fast-exit`） | 6 460.1 | 9 919.2 | — | — |
| E7-1 | + `PROOT_NO_CLOSE_INTERCEPT=1` | 6 208.7 | 9 010.5 | −924.7（8/10，CI 跨 0） | −1 418.9（8/10） |
| E7-2 | + `PROOT_RO_CACHE_PREFIXES` | 5 580.4 | 6 685.2 | −2 135.7（10/10） | −3 719.0（10/10） |
| E7-3 | `01+02+03` | 5 642.0 | 6 333.0 | −2 684.1（10/10） | −4 187.0（10/10） |
| E7-4 | `01+02+03` + P0-2 垫片 | **4 835.7** | **4 965.1** | −3 359.6（10/10） | −5 487.0（10/10） |

- **补丁 03（只读前缀 dentry 缓存）是 P1 的主力**：两轮 10/10 更快，CI 远离 0。
- **补丁 02 方向一致但强度不足**：两轮各 8/10，第一轮 CI 跨 0；两轮合并 n=20 后均值 −1 172 ms、CI [−2 145, −199]、
  16/20 更快，但单轮不显著，且语义面（`shadow_pipe_read_end` / netlink 清理）更大，因此**不进生产默认**。
- **E7-4 的垫片叠加**两轮都是 10/10（第 2 轮 −1 300 ms），与垫片已在生产（`646741c`）的事实一致。
- 两轮的绝对水平差很多（E7-0 P50 6.5 s vs 9.9 s），跨轮绝对值不可比；只允许轮内配对。跨轮合并统计仅作为「方向一致性」的补充。

生产接入与 T0/T1 配对（`bridge.complete` 7 798 → 5 842 ms，−25.1%，12/12 更快）见
[2026-11-21 P1 生产配对报告](../migration/parity/2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md)。

## 诊断入口：只在「开发者选项 + 诊断构建」出现

入口是**编译期**条件，不是运行期隐藏：

- `src/DshMobile.Android/DshMobile.Android.csproj` 在 `-p:DshDiagnostics=true` 时把 `DSH_DIAGNOSTICS` 加入 `DefineConstants`。
- `DeveloperActivity` 的「启动实验」行包在 `#if DSH_DIAGNOSTICS` 里；`StartupExperimentActivity` 整个文件也在该条件内编译。
- 普通 Debug / Release 构建没有这个类、没有这个入口，也不接受任何「打开实验」的 intent。

构建诊断 APK（先按常规准备原生库与运行时资产）：

    pwsh native/build-native.ps1
    pwsh runtime-bundle/fetch-runtime.ps1
    dotnet build src/DshMobile.Android/DshMobile.Android.csproj -c Release -p:DshDiagnostics=true
    # 安装：adb install -r <...>-Signed.apk（诊断构建与正式安装同签名时数据保留）
    # 性能测量请用 Release：Debug 构建会改变宿主侧开销。

打开路径：

1. 「更多 → 关于」连点 7 次，开启开发者选项。
2. 「更多 → 开发者选项 → 启动实验（诊断）」。
3. 选择 E1+E2、E3、E4 或「全部（E1→E2→E3→E4）」。一次只运行一个套件；运行中其他行禁用，可点「取消当前实验」。

诊断构建只用于实验和取证，不作为发布产物；不要把带 `DSH_DIAGNOSTICS` 的 APK 当成正式包分发。

## 安全模型

| 控制 | 实现 | 说明 |
|---|---|---|
| 固定枚举 | [StartupExperiment.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperiment.cs) 的 `StartupExperiments.All` | 只有 E1-0…E1-3、E2-0…E2-3、E3-P/E3-0/E3-1、E4-A/E4-B/E4-C；runner 只接受套件名（`E1E2`/`E3`/`E4`），不接受 argv、命令、可执行文件路径或超时参数 |
| 一次一个任务 | `StartupExperimentRunner` 的 `SemaphoreSlim(1,1)` + `EXPERIMENT_BUSY` | 第二个请求直接拒绝，不排队、不并发；UI 同时禁用其他行 |
| DSH 在跑则拒绝 | `_productionRunning()`（`HostOwnsProcesses`）→ `EXPERIMENT_DSH_RUNNING` | 不碰正在运行的生产进程和端口 |
| 3080 被占用则拒绝 | 连接 `127.0.0.1:3080` → `EXPERIMENT_PORT_BUSY` | 其他 App、残留进程或手工服务占用时拒绝启动，避免误连/误杀 |
| App UID | PRoot 和 guest 进程继承 App 的 Linux UID | 不 `su`、不提权；受 Android 沙箱和 SELinux 约束，guest 看不到其他 App 的数据 |
| nativeLibraryDir 内 PRoot | `ProotBinary` 优先 `nativeLibraryDir/libproot.so`；loader / shmem 同样优先 APK 内副本 | 不从用户可写目录执行任意 ELF；运行时层只提供只读回退 |
| 隔离目录 | [IsolatedGuestLayout.cs](../src/DshMobile.Runtime/Diagnostics/IsolatedGuestLayout.cs) | E3-0/E3-1/E4 的 workspace、guest-cache、compile-cache 都在本次实验根下；只从生产目录复制已登记的**非秘密**覆盖层 YAML，不复制会话、凭据、MCP 配置或工作区 |
| 超时 / 取消 / 清理 | 每变体 30–180 s；`CancellationToken`；`Process.Kill(entireProcessTree: true)`；等待退出并等待 3080 释放；`finally` 删除实验根 | 取消会结束子进程树并删除本次隔离目录；残留的临时树不影响生产 |
| 不调用 KillTreesByMarker | runner 只结束自己 `Process` 启动的进程树 | 生产启动里的 marker 扫描可能误杀其他进程；实验路径明确不使用它 |
| 输出最小化 | 只写固定字段 JSONL；UI 只显示套件结果和文件路径 | 不把环境变量、token、URL、prompt、用户路径写进记录 |

套件级拒绝不会伪造一条测量：runner 返回稳定错误码（`EXPERIMENT_DSH_RUNNING`、`EXPERIMENT_PORT_BUSY`、`EXPERIMENT_BUSY`、`EXPERIMENT_UNKNOWN_SUITE`、`EXPERIMENT_BAD_REPS`），UI 显示该码，**不写实验记录**。`result` 里的 `refused` / `setup-error` 是记录格式为「前置条件拒绝 / 准备失败」保留的取值，工具按最小字段处理，绝不把它们混进成功样本。

E3-P 是生产配置对照：它使用与生产相同的命令、生产 `user-data` 和缓存绑定，用来回答「是不是生产配置本身的问题」。它只在 DSH 已停止时、每次迭代启动一次 Web 探针；如果设备上已有重要会话/配置，先备份，或只跑 E3-0 / E3-1。E3-0 / E3-1 / E4 不写入生产 `user-data`；它们只读取已登记的覆盖层 YAML 和 shim 文本，复制到本次隔离目录。

## E1–E4 变体表

所有变体都由代码常量定义；`iteration` 从 1 开始，E1/E2 每个变体在正式 10 次前有 2 次不计入 JSONL 的预热（iteration 0）。

| 变体 | 实验 | kind | 终点 | 隔离 | cache | 目的 |
|---|---|---|---|---|---|---|
| E1-0 | E1 | exit | PRoot 启动 `/bin/true` 到退出 | 否（生产 bind；变体自身不写） | 不涉及 | PRoot / 最小 guest 进程地板 |
| E1-1 | E1 | exit | PRoot + `node --version` 到退出 | 否 | 不涉及 | Node 可执行文件启动成本 |
| E1-2 | E1 | exit | PRoot + `node -e` 打印固定 marker 到退出 | 否 | 不涉及 | Node 脚本启动 + marker |
| E1-3 | E1 | exit | E1-2 + `--import link-shim` | 否 | 不涉及 | link-shim 的额外成本 |
| E2-0 | E2 | exit | `node -e "0"` 到退出 | 否 | 不涉及 | 无 DSH 的 Node 基线 |
| E2-1 | E2 | exit | `node --expose-internals <dsh-bin> --version` 到退出 | 否 | 不涉及 | DSH 入口 + 模块解析 |
| E2-2 | E2 | exit | `... --help` 到退出 | 否 | 不涉及 | 命令/插件表加载 |
| E2-3 | E2 | exit | `... --dump-config` 到退出 | 否 | 不涉及 | 配置/插件扫描 |
| E3-P | E3 | web | 生产命令启动到首次 HTTP 状态 | 否（生产配置对照） | 不涉及 | 生产配置基线 |
| E3-0 | E3 | web | 隔离最小配置到首次 HTTP 状态 | 是 | 不涉及 | 排除生产配置/用户数据影响 |
| E3-1 | E3 | web | 隔离 + 已登记覆盖层到首次 HTTP 状态 | 是 | 不涉及 | overlay / patch 成本 |
| E4-A | E4 | web | 隔离、无 `NODE_COMPILE_CACHE` 到首次 HTTP 状态 | 是 | off | 关闭 compile cache |
| E4-B | E4 | web | 隔离、空缓存目录到首次 HTTP 状态 | 是 | cold | 冷缓存；运行后统计实际落盘文件 |
| E4-C | E4 | web | 隔离、已预热 compile cache 到首次 HTTP 状态 | 是 | warm | 热缓存；必须先证明缓存文件 > 0 |
| E6-0 | E6 | web | 隔离最小配置基线（同 E3-0）到首次 HTTP 状态 | 是 | 不涉及 | P0 优化对照基线 |
| E6-1 | E6 | web | 隔离 + `--preserve-symlinks` 到首次 HTTP 状态 | 是 | 不涉及 | 验证跳过 ESM `finalizeResolution` `realpathSync` 的收益 |
| E6-2 | E6 | web | 隔离 + `--import startup-fs-cache-shim.mjs` 到首次 HTTP 状态 | 是 | 不涉及 | 验证只读运行层 FS 元数据缓存垫片收益 |
| E6-3 | E6 | web | 隔离 + `--preserve-symlinks` + `startup-fs-cache-shim.mjs` 到首次 HTTP 状态 | 是 | 不涉及 | 验证两项 P0 优化叠加收益（2026-11-20 已测耗时；兼容性待验证） |
| E7-0 | E7 | web | 隔离最小配置基线（新 PRoot 仅默认启用 `01-statx-fast-exit`，ADR-0030） | 是 | 不涉及 | P1 PRoot 优化对照基线 |
| E7-1 | E7 | web | E7-0 + `PROOT_NO_CLOSE_INTERCEPT=1` | 是 | 不涉及 | 验证跳过 `PR_close` seccomp 拦截的收益 |
| E7-2 | E7 | web | E7-0 + `PROOT_RO_CACHE_PREFIXES=/opt/dshapp/runtime:/usr/local` | 是 | 不涉及 | 验证 PRoot 只读前缀 dentry 缓存收益 |
| E7-3 | E7 | web | E7-0 + `PROOT_NO_CLOSE_INTERCEPT=1` + `PROOT_RO_CACHE_PREFIXES` | 是 | 不涉及 | 验证 PRoot P1 三项优化叠加收益 |
| E7-4 | E7 | web | E7-3 + `--import startup-fs-cache-shim.mjs` | 是 | 不涉及 | 验证 PRoot P1 + 生产 P0-2 垫片组合收益（2026-11-21 两轮 10/10 成立） |

固定 marker 只有一行：`DSHM_NODE_READY`（`process.stdout.write("DSHM_NODE_READY\n")`）。E1-2 / E1-3 之外的变体没有 marker，`guestFirstMarkerMs` 必须为 `null`。

### 终点不同，不可相加

- `exit`：Host 发起调用 → 进程退出。E1 / E2 属于这一类。
- `web`：Host 发起调用 → 首次 HTTP 状态（健康探针）。E3 / E4 属于这一类。

因此：

- E1/E2 的 P50 与 E3/E4 的 P50 **不能相加**，也不能说「E2 + E3 = 用户等待」；Web 起点、探针轮询（约 100 ms）和超时边界都不同。
- 同一个实验内才允许做配对差值；跨实验、跨 kind 一律 `not comparable`。
- `durationMs` 是统一终点列；**web 变体（E3/E4）的统计与配对统一用 `httpReadyMs`**（同一终点），exit 变体（E1/E2）继续用 `durationMs`。`httpReadyMs` 为 null 的 web 行不进入成功样本，也不参与配对，避免把 exit 行或未就绪行混进来。

## 记录字段（JSONL schema 1）

一行一个 JSON 对象，UTF-8，不带数组外壳。字段固定，顺序稳定：

    {"schema":1,"experiment":"E1","variant":"E1-2","kind":"exit","runId":"a1b2c3d4e5f6","iteration":1,"durationMs":123.4,"spawnMs":12.3,"guestFirstMarkerMs":150.2,"httpReadyMs":null,"httpStatus":null,"userCpuMs":null,"systemCpuMs":null,"peakRssKb":null,"exitCode":0,"timedOut":false,"result":"ok","cacheMode":"off","cacheFileCount":null,"cacheBytes":null,"startedAt":"2026-11-18T02:03:04.567Z","note":null}

| 字段 | 类型 | null / 取值规则 | 含义 |
|---|---|---|---|
| `schema` | int | 固定 `1`；其它值跳过并计数 | 记录格式版本 |
| `experiment` | string | `E1` \| `E2` \| `E3` \| `E4` \| `E6` \| `E7` | 实验组 |
| `variant` | string | 固定变体 id，见上表；未知 id 跳过并计数 | 变体 |
| `kind` | string | `exit` \| `web` | 终点类型，决定能否配对 |
| `runId` | string | 12 位十六进制随机标识；最小化 `refused` / `setup-error` 行缺失时按 null | 单次测量的关联键，不用于身份 |
| `iteration` | int | 正式重复从 1 开始；缺失按 null | 配对键；预热 iteration 0 不写入 |
| `durationMs` | number | 失败/拒绝/无终点时 null | exit：Host 发起 → 进程退出；web：Host 发起 → 首次 HTTP 就绪 |
| `spawnMs` | number | `Process.Start` 失败时 null | 仅 `Process.Start` 自身耗时，与 guest 工作分开 |
| `guestFirstMarkerMs` | number | 没有 marker、marker 未出现或无法计时时 **必须 null** | guest 输出固定 marker 的时刻（相对本次发起） |
| `httpReadyMs` | number | 仅 web；exit 或未就绪时 null | 首次 HTTP 就绪；web 变体的统计与配对用这一列 |
| `httpStatus` | int | 仅 web；未拿到时 null | 首次 HTTP 状态码（如 200/401） |
| `userCpuMs` | number | 拿不到时 null，不估算 | 用户态 CPU 时间 |
| `systemCpuMs` | number | 拿不到时 null，不估算 | 内核态 CPU 时间 |
| `peakRssKb` | int | 拿不到时 null，不估算 | 峰值 RSS（KiB） |
| `exitCode` | int | 进程未退出/被杀死且不可读时 null | 进程退出码；web 探针后会主动结束进程，不能当作 DSH 正常退出 |
| `timedOut` | bool | 缺失按 null | 是否命中本变体超时 |
| `result` | string | `ok` \| `failed` \| `timeout` \| `refused` \| `setup-error` | 结果分类，见下 |
| `cacheMode` | string | `off` \| `cold` \| `warm` \| `unknown`；缺失按 null | compile cache 状态 |
| `cacheFileCount` | int | 不统计时 null | 缓存目录中的文件数（E4-B/C） |
| `cacheBytes` | int | 不统计时 null | 缓存目录字节数（E4-B/C） |
| `startedAt` | string | ISO-8601 UTC，毫秒 | 本次发起时刻 |
| `note` | string \| null | 固定短 token 或 null；自由文本会被 C# 侧脱敏为 `[redacted]` | 短原因（如 `TIMEOUT`、`SPAWN`、`PORT_LEAK`、`CACHE_EMPTY`），不进入 CSV |

`result` 的含义：

- `ok`：exit 变体进程以 0 退出；web 变体拿到首次 HTTP 状态并且探针后端口释放。
- `failed`：非 0 退出、过早退出、spawn 失败、端口未释放等。
- `timeout`：命中本变体超时（E1/E2 30 s，E2-3 180 s，E3/E4 120 s）。
- `refused`：前置条件拒绝，未真正开始测量；可能只有少量字段。
- `setup-error`：启动前准备失败（目录、层、隔离布局等）；可能只有少量字段。

缺字段一律按 `null` 处理，不补 0、不补默认成功。`schema != 1`、未知 `variant`、坏 JSON、缺 `result`、与固定目录冲突的 `experiment` / `kind`（以及非 `refused` / `setup-error` 行缺 `runId`）在 report 工具里**跳过并计数**；最小化的 `refused` / `setup-error` 行可以没有 `runId`，CSV 里该列留空。

## 如何跑

### 1. 采集前

- 安装诊断构建（见上），开启开发者选项。
- **停止 DSH**，确认 3080 没有残留监听；runner 会再次检查，占用则直接拒绝。
- 固定设备、ROM、APK commit、Node 版本、DSH runtime 版本；同一次对比不要换构建。
- 保持屏幕、温度、充电状态尽量一致；一次只跑一个套件。

### 2. 运行

「开发者选项 → 启动实验（诊断）」：

- `E1 + E2（各 10 次）`：先 E1 四个变体，再 E2 四个变体；每个变体有 2 次不计入 JSONL 的预热。
- `E3（各 10 次）`：E3-P → E3-0 → E3-1。
- `E4（A 10 次 + B / C）`：E4-A 10 次；E4-B 冷缓存 1 次并统计落盘；E4-C 先用 E2-1 预热缓存，只有 `cacheFileCount > 0` 才继续 10 次，否则写一条 `CACHE_EMPTY` 的 C 记录并停止。
- `全部（E1→E2→E3→E4）`：按固定顺序串行；不要手工改变顺序。

运行中可点「取消当前实验」：取消会结束本次子进程树、删除本次隔离目录；已经写入的行保留。

### 3. 产物位置

JSONL 写在 App 的外部私有目录：

    /storage/emulated/0/Android/data/io.github.maiji1990.dshmobile/files/startup-experiments/
    # 某些设备/文件管理器显示为 Android/data/io.github.maiji1990.dshmobile/files/startup-experiments/

文件名形如 `20261118-020304-E1E2.jsonl`、`20261118-021000-E3.jsonl`、`20261118-023000-E4.jsonl`。隔离工作目录在 App 内部私有 `files/startup-experiments/<yyyyMMdd-HHmmss>/`，运行结束由 runner 删除；JSONL 不随隔离目录删除，便于事后分析。

取回：

    adb pull /sdcard/Android/data/io.github.maiji1990.dshmobile/files/startup-experiments/ artifacts/startup-experiments/

Android 11+ 的 scoped storage 可能让部分 ROM 的 `adb pull` 拒绝访问 `Android/data`；此时在设备文件管理器里复制到 `Download/`，或用 `adb shell` 从同一路径复制到 `/sdcard/Download/` 再 pull。不要把整个 `logcat` 或用户目录一起打包。

### 4. 生成 CSV / 摘要 / matrix

工具有两个输出面：

- **stdout 只放 CSV**；给 `--out` 时 stdout 为空。
- **摘要始终写 stderr**；`--summary` 只是把同一份摘要再写一个文件。
- `--matrix` 写机器可读的 JSON。

一次性生成三种产物：

    node tooling/startup-experiment-report.mjs \
      --log artifacts/startup-experiments/20261118-020304-E1E2.jsonl \
      --out artifacts/startup-experiments/E1E2.csv \
      --summary artifacts/startup-experiments/E1E2.summary.md \
      --matrix artifacts/startup-experiments/E1E2.matrix.json \
      --device "PTP-AN10" --android 17 --apk <apk-sha> --node <node-version> --dsh <dsh-version> \
      2> artifacts/startup-experiments/E1E2.summary.stderr.txt

只把 CSV 写到 stdout：

    node tooling/startup-experiment-report.mjs --log E1E2.jsonl --device "PTP-AN10" --android 17 > E1E2.csv 2> E1E2.summary.txt

从 stdin 读取（`-`）：

    cat E1E2.jsonl | node tooling/startup-experiment-report.mjs --log - > E1E2.csv 2> E1E2.summary.txt

参数：

| 参数 | 说明 |
|---|---|
| `--log <path>` | 必填；JSONL 文件，`-` 表示 stdin |
| `--out <csv>` | 可选；写 22 列 CSV。父目录不存在 → exit 2 |
| `--summary <md>` | 可选；把 stderr 摘要同时写到该文件 |
| `--matrix <json>` | 可选；写 matrix JSON |
| `--device <name>` | 可选；写入 `device` 列 / matrix |
| `--android <ver>` | 可选；写入 `android` 列 / matrix |
| `--apk <sha>` | 可选；写入 `apk` 列 / matrix |
| `--node <ver>` / `--dsh <ver>` | 可选；写入对应列 / matrix |
| `--help` | 打印 usage 并 exit 0 |

退出码：

- `0`：至少有一条有效记录，已输出报告。
- `1`：没有任何有效记录（全部被跳过，或文件为空）。
- `2`：参数错误、`--log` 不可读、输出目录不存在、写文件失败。

CSV 列顺序固定为 **22 列**（前 5 列是元数据，缺失写空）：

    device,android,apk,node,dsh,experiment,variant,kind,iteration,runId,durationMs,spawnMs,guestFirstMarkerMs,httpReadyMs,httpStatus,exitCode,timedOut,result,cacheMode,cacheFileCount,cacheBytes,startedAt

`note`、CPU/RSS 原始采样、任何自由文本都**不会**进入 CSV。

### 5. 摘要（stderr）包含什么

1. `[variants]`：每个 variant 一行，`kind`、`成功/尝试`、P50、P95、min/max。exit 只统计 `result = ok` 且 `durationMs` 非 null 的样本；**web 只统计 `result = ok` 且 `httpReadyMs` 非 null 的样本**。nearest-rank，P50/P95 保留 1 位小数。
2. `[paired differences]`：只有同一 `experiment` + 同一 `kind`、且 `iteration` 能一一对应时才逐对相减（后者减前者），输出 `n`、配对 P50 差值、P95 差值，并写 `paired, same endpoint`。**差值按 kind 选终点：exit 用 `durationMs`，web 用 `httpReadyMs`**。不同 kind 或无法配对写 `not comparable`；不同 endpoint 的 P50 绝不直接相减。
3. `[E1/E2 screening]`：E1-2 的 P50 是否 > 1000 ms，E2-1 / E2-2 的 P50 是否 > 1000 ms；结尾固定写「这是筛查，不是因果结论」。
4. `[E4 cache]`：A 与 C 按 `httpReadyMs` 做同样的配对差值。结论依据是 `webCacheVerified` 三态，而不是只看文件数：
   - `verified`：只有记录里显式 `webCacheVerified === true`（新数据将来提供；旧记录没有该字段）；
   - `unavailable`：所有 `cacheFileCount` 为 null 或缺失；
   - `unverified`：有文件数（例如 > 0）但没有 Web 模块覆盖证明。
   旧数据通常落在 `unverified`，必须打印 `webCacheVerified: unverified: Web 模块覆盖未证明`，不给出热启动收益结论。`cacheEvaluable` 字段保留以兼容旧消费者，但不是结论依据。
5. `[data quality]`：跳过行数（按原因分解）、缺字段计数、`result != ok` 行数；每个 variant 成功样本 < 10 时打印 `insufficient samples`。

### 6. matrix JSON

    {
      "schema": 1,
      "generatedAt": "2026-11-18T02:03:04.567Z",
      "device": "PTP-AN10",
      "android": "17",
      "apk": "…",
      "node": "…",
      "dsh": "…",
      "variants": [
        {
          "experiment": "E1",
          "variant": "E1-2",
          "kind": "exit",
          "attempts": 10,
          "successes": 10,
          "p50Ms": 850.2,
          "p95Ms": 1200.0,
          "minMs": 700.1,
          "maxMs": 1200.0,
          "cacheEvaluable": false,
          "webCacheVerified": "unavailable"
        }
      ],
      "notes": [
        "P50/P95 are nearest-rank over result=ok rows with a non-null duration; screening only, not an SLA.",
        "Different endpoints (exit vs web) and different experiments are never added or subtracted."
      ]
    }

`cacheEvaluable` 只有 E4-C 可能为 `true`；其它变体固定 `false`，保留它只为兼容旧消费者。`webCacheVerified` 每个 variant 都有，取值为 `verified` / `unverified` / `unavailable`：只有记录显式 `webCacheVerified === true` 才是 `verified`，`cacheFileCount` 全为 null/缺失是 `unavailable`，其余是 `unverified`。**E4 的结论只看 `webCacheVerified`，不看 `cacheEvaluable`。** `generatedAt` 是工具运行时刻，不是采集时刻。

## 判定规则与决策门槛（方案第 11 节）

下面的门槛来自 **2026-11-18 启动根因方案第 11 节（判定与决策门槛）**，在本仓库只作为筛查口径。它们是**筛查，不是 SLA，也不是因果结论**；因果判断必须回到代码/配置证据，并由 T1 真机复测确认。

| 观察 | 数据条件 | 筛查含义 | 下一步 |
|---|---|---|---|
| G1 Node 地板 | E1-2 P50 > 1000 ms | Node 进程 + 最小脚本启动是一阶成本，先别把问题归给 WebView | 用 E1-1 vs E1-2 vs E1-3 的配对差值拆出 `node` 与 link-shim；再与 E2 对比 |
| G1' Node 地板不显著 | E1-2 P50 ≤ 1000 ms | 单凭 E1 不能证明 Node 是主因 | 不据此优化 Node；继续看 E2/E3 |
| G2 DSH 入口 | E2-1 或 E2-2 P50 > 1000 ms | DSH 模块解析/命令表加载是一阶成本 | 用 E2-2 vs E2-3 看 `--help` → `--dump-config` 的配置/插件增量 |
| G3 生产配置 | E3-P vs E3-0，同 kind、可配对，配对 P50 差值明显 | 生产配置/用户数据可能影响 Web 启动 | 先查配置、插件、MCP、工作区；不要直接改生产启动 |
| G4 覆盖层 | E3-1 vs E3-0，同 kind、可配对，配对 P50 差值明显 | overlay / patch 有可测成本 | 检查已登记覆盖层；保持安全校验不变 |
| G5 缓存 | E4-C 的 `webCacheVerified = verified`（显式 Web 模块覆盖证明），且 A/C 的 `httpReadyMs` 配对差值稳定 | compile cache 是候选优化点 | 只有在 T1 生产路径复测后才考虑接入；`unverified` / `unavailable` 一律记录「Web 模块覆盖未证明」 |
| G6 生产回归 | T1 vs T0，同 kind、同 endpoint、可配对，且样本足够 | 变更是否真的改善了生产启动 | 保留或回退；不以 E1–E4 的局部差值代替 T1 |

使用规则：

- 先看 `result` 和样本量：`result != ok` 不进入 P50/P95；成功样本 < 10 先补采。
- 只看同一 `(experiment, kind)` 的配对差值；跨实验、跨 kind、iteration 对不齐都写 `not comparable`。
- 1000 ms 是筛查阈值，不是「超过就改、低于就安全」的发布门槛。
- E4-C 的 `webCacheVerified` 不是 `verified` 时，摘要里的 `unverified: Web 模块覆盖未证明` / `unavailable: cacheFileCount 全为 null` 就是最终结论；不得把 A/C 的差值解释成热启动收益。
- T1 是唯一能对「生产启动是否变快/变慢」下结论的格子；E1–E4 只回答「哪一层值得查」。

## 隐私禁记清单

JSONL、CSV、摘要和 matrix **绝不能**包含：

- token、launch token、认证头（Authorization / Cookie 等）、SSH 私钥或口令；
- URL query（含临时参数、端口之外的敏感信息）；
- prompt、用户输入、会话内容、模型输出；
- 环境变量（尤其是密钥、代理、凭据相关）；
- 用户文件内容或路径中的隐私信息；
- CPU profile / heap dump / 原始采样（如需 profile，必须先人工审核并只保留聚合结果，不能直接上传原始文件）。

工具侧保证：

- JSONL 只有固定字段；`note` 只接受短 token，自由文本在 C# 侧被脱敏为 `[redacted]`（[ExperimentRecord.cs](../src/DshMobile.Core/Diagnostics/ExperimentRecord.cs) 的 `IsToken`）。
- report 工具只导出固定 22 列，`note`、CPU/RSS 原始值、任何未列字段都不进入 CSV / matrix / 摘要。
- `runId` 是随机短标识，不用于关联用户身份。
- 共享 JSONL 前仍需人工检查：确认没有把别的日志、路径或异常栈拼进文件。

### PageFinished ≠ 可交互

E3/E4 的终点是**首次 HTTP 状态**，不是 `onPageFinished`，更不是「页面可交互」。JS 初始化、数据加载、输入法/滚动就绪都可能更晚；不要用这些数字宣称首屏已可用。真实可交互时间只能另设测点并「待真机验证」。

## 真机矩阵：T0 → E1 → E2 → E3 → E4 → T1

顺序固定，目的是先建立基线、再逐层拆因、最后回到生产验证。T0/T1 用现有的 [STARTUP_PERF](STARTUP_PERF.md) 采集；E1–E4 用本文的诊断器。

| 阶段 | 运行内容 | 回答的问题 | 最少样本 | 产物 |
|---|---|---|---|---|
| T0 基线 | 正常停止 DSH 后重复启动（B 场景），用 `startup-perf` 记录 `bridge.complete` / `web.*` | 改动前的生产启动分布 | ≥ 10 次成功 | `t0.csv` + summary |
| E1 | `E1 + E2` 套件里的 E1-0…E1-3 | PRoot / Node / link-shim 的地板 | 每变体 ≥ 10 次成功 | `E1E2.jsonl` |
| E2 | 同一套件里的 E2-0…E2-3 | DSH CLI 入口/配置加载成本 | 每变体 ≥ 10 次成功 | 同一 JSONL |
| E3 | E3-P / E3-0 / E3-1 | 生产配置、隔离配置、覆盖层的 Web 启动差异 | 每变体 ≥ 10 次成功 | `E3.jsonl` |
| E4 | E4-A ≥ 10；E4-B 1 次冷缓存；E4-C 先证明落盘再 ≥ 10 | compile cache 是否值得做 | A/C ≥ 10；B 1 次 | `E4.jsonl` |
| T1 回归 | 与 T0 完全相同的生产启动路径，重复 ≥ 10 次 | 变更后生产启动是否改善/退化 | ≥ 10 次成功 | `t1.csv` + summary |

要求：

- 同一轮矩阵固定设备、ROM、APK commit、Node/DSH runtime 版本；不同构建不要混在同一个结论里。
- 每次测量前确认 DSH 已停止、3080 空闲；E3-P 会使用生产配置，先备份重要数据。
- E4-B 的冷缓存只有 1 次是设计使然（重置缓存后第一次），不计算 P50；如果 `cacheFileCount = 0` 或没有显式 `webCacheVerified=true`，E4-C 记 `unverified: Web 模块覆盖未证明`，不要补跑成「热」。
- 成功样本 < 10 时报告会写 `insufficient samples`；先补采，不要用 3–5 次的结果下结论（小样本 P95 等于最大值）。
- 没有真机日志/JSONL 的格子写「待真机验证」，CI、模拟器、chroot 数据不能替代真机（规则 9）。

## 已知限制

- **Android 无 root 拿不到 CPU/RSS/IO**：`/proc` 限制、SELinux 和 App UID 让 `userCpuMs` / `systemCpuMs` / `peakRssKb` 经常不可读；此时固定 `null`，不是 0。IO 统计也没有可靠来源。
- **PRoot 下无法 strace**：PRoot 自己处理 syscall/路径翻译，`strace` 通过 PRoot 不可用或不可信；本工具不做 syscall 级归因。
- **HTTP 就绪 ≠ 页面可交互**：健康探针约 100 ms 轮询 + 250/600 ms 连接/读取超时，`httpReadyMs` 有量化误差；不要用它判断首屏体验。
- **Web 进程被探针后结束**：E3/E4 拿到首个 HTTP 状态后主动结束进程，`exitCode` 不能当作 DSH 正常退出；E4-B 的缓存落盘也必须以文件计数为准，不能假设进程退出会 flush。
- **E4-C 可能证明不了缓存**：如果预热后 `cacheFileCount = 0` 或没有显式 `webCacheVerified=true`，工具只能写 `unverified: Web 模块覆盖未证明`；这不是失败，而是「没有证据」。
- **E3-P 会碰生产配置**：它是生产配置对照，可能写日志/会话；重要设备先备份，或只跑 E3-0/E3-1。
- **单次方差大**：少于 10 次的结果不具代表性；P95 在小样本下就是最大值。
- **真机覆盖**：E1–E4 已有 2026-11-18 的首轮真机数据；E5 Web Profile **尚无真机数据**。Android 17 / 厂商 ROM 的 PRoot、调度、温控差异只能靠真机矩阵覆盖。

## 相关文件

- 工具：[tooling/startup-experiment-report.mjs](../tooling/startup-experiment-report.mjs)
- 测试：[tests/tooling/startup-experiment-report.test.mjs](../tests/tooling/startup-experiment-report.test.mjs)
- 记录模型：[src/DshMobile.Core/Diagnostics/ExperimentRecord.cs](../src/DshMobile.Core/Diagnostics/ExperimentRecord.cs)
- 固定变体目录：[src/DshMobile.Runtime/Diagnostics/StartupExperiment.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperiment.cs)
- runner（安全门/超时/清理）：[src/DshMobile.Runtime/Diagnostics/StartupExperimentRunner.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperimentRunner.cs)
- 隔离布局：[src/DshMobile.Runtime/Diagnostics/IsolatedGuestLayout.cs](../src/DshMobile.Runtime/Diagnostics/IsolatedGuestLayout.cs)
- 诊断界面：[src/DshMobile.Android/Developer/StartupExperimentActivity.cs](../src/DshMobile.Android/Developer/StartupExperimentActivity.cs)
- 开发者选项入口：[src/DshMobile.Android/Developer/DeveloperActivity.cs](../src/DshMobile.Android/Developer/DeveloperActivity.cs)
- 诊断构建开关：[src/DshMobile.Android/DshMobile.Android.csproj](../src/DshMobile.Android/DshMobile.Android.csproj)
- PRoot 前缀与隔离 bind：[src/DshMobile.Runtime/Guest/ProotCommandBuilder.cs](../src/DshMobile.Runtime/Guest/ProotCommandBuilder.cs)
- 生产启动观测：[docs/STARTUP_PERF.md](STARTUP_PERF.md)、[tooling/startup-perf.mjs](../tooling/startup-perf.mjs)
- E5 Web Profile 协议与验收：[docs/WEB_STARTUP_DIAGNOSIS.md](WEB_STARTUP_DIAGNOSIS.md)、[tooling/web-startup-profile-report.mjs](../tooling/web-startup-profile-report.mjs)
- 规则与证据要求：[AGENTS.md](../AGENTS.md)（规则 5、9、11）、[docs/TEST_PLAN.md](TEST_PLAN.md)、[docs/COMPATIBILITY.md](COMPATIBILITY.md)

最后更新：2026-11-19
