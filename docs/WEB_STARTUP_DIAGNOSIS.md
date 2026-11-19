# Web 启动 Profile 诊断（E5 WEB_STARTUP_DIAGNOSIS）

> 状态：**工具、测试、C# 诊断器与 Node preload 已落地；2026-11-19 已在 PTP-AN10 / Android 17 上完成首轮真机 E5（B0 + OFF/ON ×10 + B1）**，结果见 [真机记录](../migration/parity/2026-11-19-PTP-AN10-A17-web-profile-diagnosis.md) 与 §14。本文固定数据格式、握手协议、安全边界、采集矩阵与验收口径；没有真机 JSONL / report 产物的格子仍写 **not-tested**，不写「通过」（[AGENTS.md](../AGENTS.md) 规则 5、9）。
>
> 相关旧实验（E1–E4）见 [STARTUP_EXPERIMENTS](STARTUP_EXPERIMENTS.md)；生产启动观测见 [STARTUP_PERF](STARTUP_PERF.md)。

## 1. 目的

2026-11-18 的 E3 真机结果里，隔离空配置的 Web 启动仍然约 **16 s**（E3-0 P50 16061.2 ms，E3-P 16820.5 ms），而 PRoot / Node / CLI 入口 / 配置合成合计只有约 **0.2 s**。这说明剩余时间落在 DSH Web Profile 自身的初始化，但当时只有 wall time，没有 CPU / wait / IO 分布，不能据此声称具体 V8 热点。

E5 的目标是回答三个问题：

1. 这约 16 s 里，主线程到底在做什么：V8 编译、模块解析、模块求值、GC、应用初始化，还是空闲/等待？
2. 事件循环的 **active** 与 **idle** 各占多少？idle 是否足以解释 wall time 与 CPU 时间之间的差额？
3. 开启进程内 CPU profile 本身会不会显著拖慢启动？如果会，拖慢多少毫秒、百分之几？

E5 **只做观测**：不改变生产启动顺序、不改变生产配置、不引入 root、不开放 inspector 端口、不下载或替换运行时层。Profile 是主线程 V8 CPU profile；它不能直接证明磁盘 IO、网络或调度等待，相关限制见 §11。

## 2. 只在诊断构建启用

入口是**编译期**条件，不是运行期隐藏：

- [DshMobile.Android.csproj](../src/DshMobile.Android/DshMobile.Android.csproj) 在 **-p:DshDiagnostics=true** 时把 **DSH_DIAGNOSTICS** 加入 DefineConstants。
- [DeveloperActivity.cs](../src/DshMobile.Android/Developer/DeveloperActivity.cs) 的「启动实验」行、[StartupExperimentActivity.cs](../src/DshMobile.Android/Developer/StartupExperimentActivity.cs) 整个文件都在 **#if DSH_DIAGNOSTICS** 内。
- 普通 Debug / Release 构建没有 E5 入口，也没有 E5 preload；不会因为环境变量或 intent 打开采样。
- 诊断构建只用于取证，不作为发布产物分发。

构建与打开路径与 E1–E4 相同：安装 **-p:DshDiagnostics=true** 的 Release 诊断 APK，开启开发者选项，进入「启动实验（诊断）」，选择 E5。一次只运行一个套件；DSH 正在运行时拒绝启动。

E5 的 Node preload 是**固定路径的仓库/APK 资产**，不接受调用方传入的脚本路径、argv、环境变量或超时参数。诊断器只接受固定套件名，与 E1–E4 的 runner 约束一致（[StartupExperimentRunner.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperimentRunner.cs)）。

## 3. 安全模型

| 控制 | 实现 | 说明 |
|---|---|---|
| 进程内 inspector | 在 DSH 的 Node 进程内创建 inspector.Session，调用 Profiler.enable / Profiler.start / Profiler.stop | **不启动 --inspect，不开放 TCP/WebSocket 端口，不监听 0.0.0.0**；profile 只在本进程内存与私有文件之间流转 |
| 固定 preload | 诊断构建打包的固定 preload 文件，由 Host 以固定参数加载 | 不读取用户提供的 JS 路径；不执行任意 --require / --import；不把 shell 字符串拼进命令 |
| 文件握手 | Host 与 preload 只在本次运行的 App 私有目录交换 stop.request、profile、summary、done | 目录由 Host 创建并限定；所有写入用临时文件 + 原子 rename，避免半截文件被当成有效 profile |
| 原始 profile 私有 | 原始 .cpuprofile 只写 App 私有目录（或复制到 --profiles 指定的本机私有目录） | **不自动上传、不提交 git、不写 Logcat、不分享到外部存储**；报告工具只输出聚合分类与数字 |
| 不接触生产凭据 | 不读取生产 token、Authorization、Cookie、SSH 私钥、用户 prompt、会话内容 | 记录字段固定；未知 JSONL 字段被报告工具忽略；原始 profile 的 url/functionName 不进入 CSV/摘要/matrix |
| 不提权 | 不 su、不 root、不关闭 SSH 主机密钥校验、不绕过权限审批 / Accessibility 写入门禁 | 与 [SECURITY.md](SECURITY.md) 及 AGENTS.md 规则 11 一致 |
| 最小暴露 | 诊断 UI 只显示套件结果、错误码和文件路径；不显示原始 profile 内容 | 原始 profile 只供本机人工分析；共享前仍需人工审查 |

进程内 inspector 仍然能读取 V8 的调用栈与函数名，因此它是**诊断构建的显式能力**，不是生产能力。原始 profile 可能包含模块 URL、函数名、查询参数甚至 URL 里的临时 token；这些内容**只能留在私有目录**，不得进入报告产物（见 §10）。

## 4. 握手时序

一次 **E5-PROFILE** 运行按以下顺序完成（**E5-OFF** 走同一路径，但 preload 不启动采样）：

1. **准备**：Host 结束/确认 DSH 未运行、3080 空闲，创建本次运行的 App 私有目录，生成 12 位十六进制 **runId**，写入 **start.request**（可选，用于记录开始时刻）。
2. **启动采样**：Host 用固定诊断命令启动 Web 进程，加载固定 preload。preload 在应用模块加载前创建进程内 inspector.Session，调用 Profiler.enable 与 Profiler.start；如果 inspector 或 Profiler 不可用，立即进入 §6 的 **PROFILE_UNAVAILABLE** 分支。
3. **HTTP ready**：Host 用健康探针轮询到首个 HTTP 状态，记录 **httpReadyMs**、**nodeUserCpuMs**、**nodeSystemCpuMs**、**eventLoopActiveMs**、**eventLoopIdleMs** 等可用指标；拿不到的写 **null**，不写 0。
4. **请求停止**：Host 写入 **stop.request** 并记录写入时刻。**不是 kill**：给 preload 一个固定宽限窗口，让它自己停止采样并落盘。
5. **preload 落盘**：preload 轮询到 stop.request 后调用 Profiler.stop，把 profile 写到临时文件，fsync 后 rename 为最终 .cpuprofile；随后原子写 **summary** 与 **done**。**profileCaptureMs** 是从 Profiler.start 到 Profiler.stop 的采样时长；**stopLatencyMs** 是从 Host 写 stop.request 到看到 done 的延迟。
6. **Host 复制**：Host 看到 done 后校验文件存在且非空，把 .cpuprofile 复制为 **<profilesDir>/<runId>.cpuprofile**，再写一行 E5 JSONL（所有字段固定；profile 是否私有写 profileStoredPrivately）。最后清理本次运行目录（保留 JSONL 与复制后的 profile）。
7. **失败**：超时、写失败、空 profile、inspector 不可用分别按 §6 处理；**不得把缺失/空/非法 profile 当成成功**。Host 仍然写 JSONL 行（能写多少写多少，拿不到 null），让报告工具计数并在摘要里报 **not-complete**。

原子写入的意义：report 工具只接受能完整 JSON.parse 的 .cpuprofile；半截文件、0 字节文件、非法 JSON 分别计入 **missing/empty/invalid**，不会进入分类聚合。

## 5. 字段表与 null 规则

宿主每次运行写一行 JSON（JSONL，schema 1）：

    {"schema":1,"experiment":"E5","variant":"E5-OFF|E5-PROFILE","runId":"12hex","iteration":1,"httpReadyMs":16800.0,"profileCaptureMs":420.0,"profileSampleCount":1240,"nodeUserCpuMs":5800.0,"nodeSystemCpuMs":900.0,"eventLoopActiveMs":6800.0,"eventLoopIdleMs":9500.0,"stopLatencyMs":12.0,"result":"ok|failed|timeout|refused|setup-error","errorCode":null|"PROFILE_TIMEOUT"|"PROFILE_WRITE_FAILED"|"PROFILE_EMPTY","profileStoredPrivately":true,"startedAt":"2026-11-19T02:03:04.567Z"}

| 字段 | 类型 | null / 取值规则 | 含义 |
|---|---|---|---|
| schema | int | 固定 **1**；其它值跳过并计数 | 记录格式版本 |
| experiment | string | 固定 **E5**；其它值跳过并计数 | 实验组 |
| variant | string | **E5-OFF** \| **E5-PROFILE**；其它字符串是非法 enum，跳过并计数 | 是否开启采样 |
| runId | string | 12 位十六进制；缺失按 null；格式不对跳过 | 关联 .cpuprofile 的键，不用于身份 |
| iteration | int | 正式重复从 1 开始；缺失按 null | 配对键；OFF/ON 同一 iteration 配对 |
| httpReadyMs | number | 拿不到时 **null**；web 终点 | 发起 → 首次 HTTP 状态；E5 的验收指标 |
| profileCaptureMs | number | 未采样、未停止或拿不到时 **null** | stop.request → done 的耗时（停采样 + 原子写盘，由宿主计时）；**不是**采样窗口，也不是 httpReadyMs 的一部分 |
| profileSampleCount | int | 未采样或拿不到时 **null** | .cpuprofile 的 samples.length；不是 wall time |
| nodeUserCpuMs | number | 拿不到时 **null**，不估算 | Node 进程用户态 CPU |
| nodeSystemCpuMs | number | 拿不到时 **null**，不估算 | Node 进程内核态 CPU |
| eventLoopActiveMs | number | 拿不到时 **null** | 事件循环 active 时间 |
| eventLoopIdleMs | number | 拿不到时 **null** | 事件循环 idle 时间 |
| stopLatencyMs | number | 未完成握手或拿不到时 **null** | 写 stop.request → 看到 done |
| result | string | **ok** \| **failed** \| **timeout** \| **refused** \| **setup-error**；其它字符串是非法 enum，跳过并计数 | 结果分类 |
| errorCode | string \| null | null 或 **PROFILE_TIMEOUT** \| **PROFILE_WRITE_FAILED** \| **PROFILE_EMPTY** \| **PROFILE_UNAVAILABLE**；其它字符串是非法 enum，跳过并计数 | 可稳定展示的失败原因（见 §6） |
| profileStoredPrivately | bool \| null | 只有确实写到私有目录才是 **true**；缺失按 null | 原始 profile 的私有性证据 |
| startedAt | string | ISO-8601 UTC，毫秒；缺失按 null | 本次发起时刻；不进入 CSV/摘要/matrix |

**null 规则**：

- 取不到的指标必须是 **null**，**不得用 0 代替**。**0** 只表示真实测到的 0（例如确实没有 GC 时间）。
- 缺少可选字段按 **null** 处理；缺少 variant / result / schema 不是「null」，而是非法行。
- variant / result / errorCode 是 enum：enum 之外的字符串一律视为非法行，报告工具跳过并计入 **invalid enums**。
- 未知的额外 JSONL 字段被报告工具忽略，不进入 CSV、摘要或 matrix；note 之类自由文本不导出。
- runId 只允许 12 位十六进制，防止 ../ 路径穿越到 profiles 目录之外。

## 6. 错误码与失败模式

| 代码 / 现象 | 触发条件 | JSONL 表现 | 报告表现 |
|---|---|---|---|
| PROFILE_TIMEOUT | Host 已写 stop.request，宽限窗口内没有 done；主线程长时间阻塞、进程无响应或 preload 未轮询 | result=timeout，errorCode=PROFILE_TIMEOUT，profileCaptureMs / profileSampleCount 可能为 null，profileStoredPrivately=false | profileFile missing 或 empty；not-complete；不进入成功样本 |
| PROFILE_WRITE_FAILED | 私有目录不可写、磁盘满、rename/fsync 失败、Host 复制失败 | result=failed，errorCode=PROFILE_WRITE_FAILED，profileStoredPrivately=false | missing / invalid；not-complete |
| PROFILE_EMPTY | Profiler.stop 返回空 profile、samples.length=0、文件 0 字节 | result=failed（或 timeout），errorCode=PROFILE_EMPTY，profileSampleCount=0 或 null | empty；not-complete |
| PROFILE_UNAVAILABLE | inspector.Session 创建失败、Profiler.enable/start 抛错、Node 构建未启用 inspector | result=failed，errorCode=PROFILE_UNAVAILABLE，所有 profile 指标 null，profileStoredPrivately=false | 计入 errorCode 统计；not-complete；不进入成功样本，绝不是「无开销」 |
| 主线程阻塞导致 stop 延迟 | JS 长任务 / 同步初始化占住主线程 | stopLatencyMs 偏高，stopLatencyMs 为 null 或 PROFILE_TIMEOUT | 摘要输出 stopLatencyMs 中位数；不要把 stop 延迟当成 profile 采样时长 |
| Worker / 子进程 | DSH 把工作放到 Worker、child_process 或 PRoot 子进程 | 主线程 profile 里没有这些线程的栈 | **主线程 profile 不覆盖 Worker/子进程**；报告不能声称「全部 CPU」 |
| 进程被强杀 | 超时后 Host kill | profile 可能缺失或半截 | missing / invalid；绝不成功 |
| profile JSON 非法 | 文件存在但不是合法 .cpuprofile | JSONL 仍可能是 ok（Host 只负责落盘） | 报告 invalid 并 not-complete；不分类、不当作成功 |

**关于 PROFILE_UNAVAILABLE**：它是 schema 1 errorCode enum 的正式成员（报告工具接受它），表示「inspector 在本环境不可用」。它发生时本次运行没有有效 profile，报告必须计入失败并显示 not-complete，不得生成一条「ok 且 0 开销」的记录。

## 7. 采集矩阵

E5 的目标不是一次性跑满所有格子，而是在同一设备、同一构建、同一热状态下拿到可配对的 OFF/ON 数据。矩阵标签与顺序固定：

| 阶段 | 构建 / 变体 | 新采集次数 | 目的 |
|---|---|---:|---|
| **B0** | 生产 APK 基线（复用 2026-11-18 的 E3-P / T0 数据） | 0（复用） | 外部锚点；不重新采集，避免把旧数据混进新批次 |
| **P0-OFF** | 诊断构建，E5-OFF（preload 不启动采样） | 10 | 诊断路径本身的开销地板 |
| **P0-ON** | 诊断构建，E5-PROFILE（进程内 inspector 采样） | 10 | 采样开销 + CPU/ELU/分类证据 |
| **P0-VERIFY** | 诊断构建，OFF/ON 交替复测 | 10（5 对） | 只做稳定性 / profile 完整性抽查；**5 对 < 10，不用于验收判定** |
| **B1** | 生产 APK 回归基线（E5 采集结束后） | 10 | 确认 E5 采集没有改变生产启动 |

合计新采集 **10 + 10 + 10 + 10 = 40 次**；B0 复用旧数据，不计入新批次。每个 block 固定设备、ROM、APK commit、Node/DSH runtime 版本；不同构建不混在同一结论里。

**交错顺序**：

1. 先确认 B0 的设备/构建与本次一致；不一致就重新采 B0，或把 B0 标成 **not-tested**。
2. P0-OFF / P0-ON 按 iteration 交错：奇数轮先 OFF 后 ON，偶数轮先 ON 后 OFF；每轮之间保持固定冷却（例如 30 s），避免热漂移全部落在 ON 上。
3. P0-VERIFY 在冷却 2 分钟后做 10 次 OFF/ON 交替；只检查 profileFile 四态、stopLatencyMs 和开销是否大致复现。
4. B1 最后做 10 次生产启动，与 B0 同构建、同场景。
5. 全程屏幕常亮、充电状态一致；温度不可读时在报告里写 **temperature: unknown**，不要假设恒温。

验收配对只用 P0-OFF(10) 与 P0-ON(10)；P0-VERIFY 的 5 对不进入 acceptable/over 判定。少于 10 对时报告必须写 **insufficient**。

## 8. 开销判定

E5 的验收目标是：

- 按 iteration 配对 httpReadyMs，计算 **E5-PROFILE − E5-OFF**；
- 只保留 **两端都 result=ok 且 httpReadyMs 非 null** 的配对；
- 报告 n、median、P50、P95 差值、占 OFF P50 的百分比；
- 判定：**median 配对差 ≤ 5% 的 OFF P50，且 median 配对差 < 800 ms** → **acceptable**；否则 **over**；
- **n < 10** → **insufficient**，不给出验收结论。

这是**验收目标，不是 SLA**，也不代表「超过就必须关闭 profiling」。如果 over，先检查 preload 是否在主线程做同步序列化、stop 延迟是否被主线程阻塞放大，再决定是否优化或缩小采样范围。报告里的 P50/P95 用 nearest-rank；median 是真正的中间值（偶数样本取中间两个的平均），两者都保留，避免混用。

## 9. 报告产物

工具：[tooling/web-startup-profile-report.mjs](../tooling/web-startup-profile-report.mjs)。纯 Node ESM、无第三方依赖；只读 JSONL 与 profiles 目录，不启动 App/进程/inspector。

    node tooling/web-startup-profile-report.mjs \
      --log artifacts/e5/E5.jsonl \
      --profiles artifacts/e5/profiles \
      --out artifacts/e5/E5.csv \
      --summary artifacts/e5/E5.summary.md \
      --matrix artifacts/e5/E5.matrix.json

- stdout 只放 CSV；给 --out 时 stdout 为空。
- 摘要始终写 stderr；--summary 只是把同一份摘要再写一个文件。
- --matrix 写机器可读 JSON。
- --log - 可从 stdin 读 JSONL。
- 退出码：0 有有效记录；1 无有效记录；2 参数 / 文件错误。

### 9.1 CSV

固定 **15 列**，顺序不得改变：

    variant,iteration,runId,httpReadyMs,profileCaptureMs,profileSampleCount,nodeUserCpuMs,nodeSystemCpuMs,eventLoopActiveMs,eventLoopIdleMs,stopLatencyMs,result,errorCode,profileStoredPrivately,profileFile

profileFile 只能是 **present** / **missing** / **empty** / **invalid**，**不含路径**。null 指标写空单元格；profileStoredPrivately 为 true / false / 空。

### 9.2 摘要

1. [variants]：每变体 n-success/n-attempt、httpReadyMs 的 P50/P95/min/max（只 result=ok 且非 null；nearest-rank）。
2. [paired overhead]：配对差值、n、median、P50、P95、占 OFF P50 百分比，以及 acceptable / over / insufficient。
3. [CPU/ELU]：每轮 (nodeUserCpuMs+nodeSystemCpuMs)/httpReadyMs 的中位数、eventLoopActive/(Active+Idle) 的中位数；固定写「ELU idle 不等于磁盘等待」。
4. [profile integrity]：PROFILE 行数、文件 present/empty/invalid/missing 计数、profileSampleCount / profileCaptureMs / stopLatencyMs 中位数；任何缺失都写 not-complete，不写「通过」。
5. [categories]：类别、样本数、按 timeDeltas 加权的时间（ms）、每轮比例的中位数、覆盖轮数。
6. [data quality]：跳过行数、非法 enum 数、result!=ok 行数、成功样本 < 10 的提示。

### 9.3 分类映射

categorizeProfile 只按栈帧的 url / functionName 判断，不按模块名猜耗时：

| 条件（按顺序） | 类别 |
|---|---|
| functionName === "(idle)" | idle-or-wait |
| functionName === "(program)" | v8-compile |
| functionName === "(garbage collector)" | gc |
| url 含 node:internal/modules，或 functionName 含 require / loadESM / resolve | module-resolution |
| url 含 node_modules / /opt/dshapp/runtime，或扩展名为 .mjs / .js / .cjs | module-eval |
| functionName === "(native)" 或 url 为空 | native-or-unknown |
| 其余 | app-initialization |

每个 sample 用其 timeDeltas 权重（微秒）归到所属 node 的类别；per-run share = 该类别时间 / 该 profile 的加权总时间；跨轮 share 取每轮比例的中位数（某轮缺该类别按 0 计），并报告覆盖轮数。输出只有类别名与数字。

### 9.4 matrix JSON

    {
      "schema": 1,
      "generatedAt": "2026-11-19T02:03:04.567Z",
      "records": 20,
      "variants": [
        {
          "variant": "E5-PROFILE",
          "attempts": 10,
          "successes": 10,
          "p50Ms": 16420.0,
          "p95Ms": 17100.0,
          "minMs": 16000.0,
          "maxMs": 17100.0,
          "profileFiles": { "present": 10, "missing": 0, "empty": 0, "invalid": 0 }
        }
      ],
      "overhead": {
        "n": 10,
        "medianMs": 320.0,
        "p50Ms": 300.0,
        "p95Ms": 640.0,
        "percent": 1.9,
        "acceptable": true
      },
      "categories": [
        { "category": "idle-or-wait", "samples": 1240, "timeMs": 523.4, "share": 0.34 }
      ],
      "dataQuality": { "skipped": 0, "invalidEnums": 0, "failed": 0 }
    }

generatedAt 是工具运行时刻，不是采集时刻。acceptable 在样本不足时为 null。

## 10. 隐私清单

CSV / 摘要 / matrix **绝不能**包含：

- functionName、完整 url、模块路径、用户路径、profiles 目录路径；
- URL query、token、launch token、Authorization / Cookie / SSH 私钥或口令；
- prompt、用户输入、会话内容、模型输出；
- 环境变量、设备标识之外的账号信息；
- 原始 .cpuprofile 本身（它可能包含上述内容）。

工具侧保证：

- CSV 只有固定 15 列；摘要只输出类别名、enum、数字；matrix 只有固定字段；
- profileFile 只写 present/missing/empty/invalid，不写文件名；
- runId 是 12 位十六进制随机值，不用于身份；
- 未知 JSONL 字段（包括 note、token、Authorization）被忽略，不进入任何输出；
- 测试里注入了带 token / URL query / 真实路径 / 函数名的 profile，并断言输出不包含它们（[测试](../tests/tooling/web-startup-profile-report.test.mjs)）。

原始 profile 的处理：

- 只放 App 私有目录；不自动上传、不 git add、不写 Logcat、不复制到外部存储；
- 需要人工分析时，先在本机脱敏/聚合；共享报告时只共享 CSV / summary / matrix；
- 即使报告工具已做聚合，分享原始 profile 前仍需人工检查 URL、函数名和 query。

## 11. 限制

- **ELU idle 不等于磁盘等待**：eventLoopIdleMs 包含定时器、Promise、事件循环等待等；它不能证明磁盘 IO、网络或调度等待。E5 不采集 block IO，不能用 idle 推断「在等磁盘」。
- **采样次数不是 wall time**：profileSampleCount=1240 表示 1240 个样本，不是 1240 ms；时间必须按 timeDeltas 加权。采样间隔、采样偏差和 stop 延迟都会影响结果。
- **只覆盖主线程**：Worker、child_process、PRoot 子进程和原生线程不在主线程 V8 profile 里；报告不能声称「全部 CPU」。
- **单设备**：一次矩阵只覆盖一台设备/ROM/构建；Android 17 / 厂商 ROM 的调度、温控、PRoot 差异需要多设备复测。
- **温度 unknown**：E5 不记录结温/壳温；temperature: unknown，不能假设恒温，也不能把跨批次差异全部归因于代码。
- **HTTP ready ≠ 可交互**：终点是首个 HTTP 状态，不是首屏、不是 onPageFinished；profile 也在 HTTP ready 后停止，后续 JS 初始化不在样本内。
- **profile 本身有开销**：采样会拖慢启动；E5 的 acceptable/over 只回答「这个开销是否可接受」，不回答「优化后能快多少」。
- **inspector 不可用**：PROFILE_UNAVAILABLE 时没有 profile，不能写「无热点」或「无开销」，只能记 not-tested。

## 12. 未测即 not-tested

以下格子目前没有真机证据，必须写 **not-tested**，不得用单元测试、模拟器或本地合成数据替代（AGENTS.md 规则 9）：

| 项目 | 当前状态 |
|---|---|
| 报告工具解析 / 统计 / 分类 / CSV / matrix / 隐私 | 已有 Node 单元测试与本地合成样例；**不是真机证据** |
| C# E5 诊断器 + Node preload 握手 | 并行实现中；未在真机验证 |
| E5-OFF / E5-PROFILE 真机采集 | **not-tested** |
| profile 四态在真机上的分布 | **not-tested** |
| 采样开销 acceptable/over | **not-tested** |
| 原始 profile 的私有目录与清理 | **not-tested**（工具侧只验证了不泄露） |
| 温度 / 多设备 / 厂商 ROM | **not-tested** |

一句话：**没有真机 JSONL 和 report 产物的格子就是 not-tested；本工具全绿不等于 E5 已通过。**

## 13. 相关文件

- 报告工具：[tooling/web-startup-profile-report.mjs](../tooling/web-startup-profile-report.mjs)
- 报告测试：[tests/tooling/web-startup-profile-report.test.mjs](../tests/tooling/web-startup-profile-report.test.mjs)
- E1–E4 记录格式与判定：[docs/STARTUP_EXPERIMENTS.md](STARTUP_EXPERIMENTS.md)
- 生产启动观测：[docs/STARTUP_PERF.md](STARTUP_PERF.md)
- 安全边界：[docs/SECURITY.md](SECURITY.md)
- 测试计划：[docs/TEST_PLAN.md](TEST_PLAN.md)
- 诊断构建开关：[src/DshMobile.Android/DshMobile.Android.csproj](../src/DshMobile.Android/DshMobile.Android.csproj)
- 开发者选项：[src/DshMobile.Android/Developer/DeveloperActivity.cs](../src/DshMobile.Android/Developer/DeveloperActivity.cs)
- 实验界面：[src/DshMobile.Android/Developer/StartupExperimentActivity.cs](../src/DshMobile.Android/Developer/StartupExperimentActivity.cs)
- 固定变体目录：[src/DshMobile.Runtime/Diagnostics/StartupExperiment.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperiment.cs)
- runner（安全门 / 超时 / 清理）：[src/DshMobile.Runtime/Diagnostics/StartupExperimentRunner.cs](../src/DshMobile.Runtime/Diagnostics/StartupExperimentRunner.cs)
- 隔离布局：[src/DshMobile.Runtime/Diagnostics/IsolatedGuestLayout.cs](../src/DshMobile.Runtime/Diagnostics/IsolatedGuestLayout.cs)
- 记录模型：[src/DshMobile.Core/Diagnostics/ExperimentRecord.cs](../src/DshMobile.Core/Diagnostics/ExperimentRecord.cs)
- 仓库规则：[AGENTS.md](../AGENTS.md)（规则 5、9、11）

最后更新：2026-11-19
## 14. 首次真机结果（2026-11-19，PTP-AN10 / Android 17）

诊断构建 APK `5333ca4f…`（`-p:DshDiagnostics=true`），DSH `0.2.2-rc.3`、Node `26.13.1`；同会话 B0 → E5（OFF/ON 交错 ×10）→ B1。
完整元数据、时间线、失败过程与限制见 [真机记录](../migration/parity/2026-11-19-PTP-AN10-A17-web-profile-diagnosis.md)，
数据见 [E5 CSV](../migration/parity/web-profile-2026-11-19.csv)、[matrix](../migration/parity/web-profile-2026-11-19.matrix.json)、
[分类聚合](../migration/parity/web-profile-categories-2026-11-19.csv)、[B0](../migration/parity/startup-perf-2026-11-19-B0.csv)、
[B1](../migration/parity/startup-perf-2026-11-19-B1.csv)。

| 项目 | 结果（P50 / P95） |
|---|---|
| B0 / B1 `bridge.complete` | 17733.0 / 21312.4 ms — 17555.5 / 18192.4 ms（Δ −1.0%） |
| E5-OFF `httpReadyMs` | **16311.0 / 18054.0 ms** |
| E5-PROFILE `httpReadyMs` | **17846.1 / 20042.7 ms** |
| Profiling 开销（逐迭代配对） | median **+1565.8 ms（+9.6%）**，P95 +4212.5 ms → **over**（目标 ≤5% 且 <800 ms） |
| CPU/httpReady 中位数 | OFF 67.3% / PROFILE 71.4% |
| ELU active 中位数 | **100.0%**（idle ≈ 0） |
| Profile 完整性 | present 10 / missing 0 / empty 0 / invalid 0；样本中位数 17,907 |
| `profileCaptureMs` / `stopLatencyMs` 中位数 | 1131.9 ms / 1023.5 ms |

分类（10 轮，`timeDeltas` 加权，每轮占比再取中位数）：`native-or-unknown` **67.0%**、`module-resolution` 20.1%、
`module-eval` 7.1%、`app-initialization` 4.2%、`gc` 1.4%、`v8-compile` 0.1%、`idle-or-wait` 0.0%。

结论：Web 启动是 **CPU 忙型**（ELU 100%、CPU 约 2/3 wall），不是空闲等待；样本主要落在 `native-or-unknown`，
JS 侧模块解析 + 求值约 27%。inspector 无法证明 native 部分里有多少是 PRoot 文件访问——需要单独的 P2 机制旁证，
不能把该分类直接解释为 syscall 耗时。采样本身开销 9.6%，因此 PROFILE 行只用于归因，不能代表正式版耗时。
