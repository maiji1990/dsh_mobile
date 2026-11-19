# Web Profile 启动瓶颈诊断（E5）· 真机记录 · 2026-11-19

- 设备：Honor PTP-AN10（Android 17，arm64-v8a），屏幕常亮并已解锁；全程同一台设备、同一次会话。
- App：`versionName 0.1.0`（`versionCode 10000`）**诊断构建**（`-c Release -p:DshDiagnostics=true`），
  APK SHA-256 `5333ca4f3a2860c48462526a505359b15ae9582ebfcde48ac48cc9717b98aebb`，09:00:20 覆盖安装，数据保留。
- 运行层：既有安装，DSH `0.2.2-rc.3`，Node `26.13.1`；未改 Node / compile cache / MCP / PRoot / 启动顺序 / 就绪策略。
- 说明：本记录对应上述 APK。之后为满足「普通 Release 不含诊断文件」的打包要求，preload 从 APK asset 改为**仅诊断构建**的
  嵌入资源（同一 JS 内容、同一 `--import` 逻辑）；该改动只影响打包方式，诊断行为与测量口径不变，因此未重跑 E5。
- 采集：诊断入口「更多 → 开发者选项 → 启动实验 → Web Profiler A/B」，固定 10 轮交错（奇数轮 OFF→PROFILE，偶数轮 PROFILE→OFF），
  每轮独立隔离 home/workspace/cache；结束后由宿主复制 profile 到 App 外部私有目录，未自动上传。
- 原始脱敏数据：[E5 逐轮 CSV](web-profile-2026-11-19.csv)、[matrix](web-profile-2026-11-19.matrix.json)、
  [分类聚合](web-profile-categories-2026-11-19.csv)、[B0](startup-perf-2026-11-19-B0.csv)、[B1](startup-perf-2026-11-19-B1.csv)。
  原始 `.cpuprofile`（10 个，约 24.9 MB）只留在开发机 `artifacts/startup-profiles/`，未提交、未上传。

## 时间线

| 阶段 | 内容 | 时间（+08:00） | 结果 |
|---|---|---|---|
| B0 | 生产启动（点击 → READY）×10 | 08:34:16–08:38:45 | 10/10 ok |
| E5 #1 | OFF/PROFILE 各 10 次（profile 副本权限 0600，adb 无法读取） | 08:39–08:45 | 20/20 记录，profile 不可取，**作废** |
| E5 #2 | 修复副本权限（0660）后重跑 | 09:00:59–09:06 | 20/20 ok，profile 10/10 可取 |
| B1 | 生产启动 ×10（排除漂移） | 09:09:50–09:14:09 | 10/10 ok |

E5 #1 记录并未用于结论：宿主复制 profile 时默认 0600，`adb pull` 报 `Permission denied`；修复为组可读（0660，与 JSONL 一致）
后重跑。#1 的 JSONL 仍保留在设备上作为失败过程记录，不进入本报告的统计。

## 生产基准 B0 / B1（点击 → DSH READY）

| 指标 | B0 P50 / P95 | B1 P50 / P95 |
|---|---:|---:|
| `bridge.complete` | 17733.0 / 21312.4 ms | 17555.5 / 18192.4 ms |
| `http.first_response` | 17339.7 / 20820.0 ms | 17166.9 / 17775.4 ms |
| `dsh.first_output` | 16056.1 / 19555.5 ms | 15946.9 / 16496.4 ms |
| `guest_assets.total` | 2.7 / 27.0 ms | 3.3 / 26.8 ms |

B0→B1 的 `bridge.complete` P50 相差 −1.0%，本会话内没有明显渐热漂移；B0/B1 与 E5 的终点不同（App 管线 vs 诊断进程），**不相减**。

## E5：OFF / PROFILE（终点＝首个 HTTP 状态）

| 变体 | n | P50 (ms) | P95 (ms) | min–max (ms) |
|---|---:|---:|---:|---|
| E5-OFF | 10 | **16311.0** | 18054.0 | 14416.1–18054.0 |
| E5-PROFILE | 10 | **17846.1** | 20042.7 | 13145.0–20042.7 |

**Profiling 开销（逐迭代配对，PROFILE − OFF）**：n=10，median **+1565.8 ms**、P50 +1523.4 ms、P95 +4212.5 ms，
占 OFF P50 **+9.6%** → 超出方案的「≤5% 且 <800 ms」验收目标，标记 **over**。结论：采样本身在这台设备上过重，
**PROFILE 行的绝对耗时不能代表正式版**，也不得与正式版直接比较。

## CPU / ELU（每轮比值的中位数）

| 变体 | CPU/httpReady | ELU active 占比 |
|---|---:|---:|
| E5-OFF | 67.3% | 100.0% |
| E5-PROFILE | 71.4% | 100.0% |

`eventLoopIdleMs` 在两变体都接近 0：**主线程事件循环全程处于忙状态**，不是「空闲等待 / 磁盘等待」。CPU 只占 wall 的约 2/3，
缺口部分不能用 ELU 解释为 IO——需要看 profile 的 native/unknown 与 GC。

## Profile 完整性与分类（10/10 可取）

- 文件：present 10 / missing 0 / empty 0 / invalid 0；每轮样本中位数 **17,907**。
- `profileCaptureMs`（stop.request → done，含停采样与写盘）中位数 **1131.9 ms**；
  `stopLatencyMs`（preload 轮询发现 stop.request 的延迟）中位数 **1023.5 ms**——主线程忙到约 1 s 才处理停止请求。
- 分类（按 `timeDeltas` 加权，先算每轮占比再取中位数）：

| 分类 | 样本数（10 轮合计） | 采样时间 (ms) | 占比 |
|---|---:|---:|---:|
| native-or-unknown | 114330 | 123519.5 | **67.0%** |
| module-resolution | 37527 | 37198.4 | 20.1% |
| module-eval | 13357 | 13160.3 | 7.1% |
| app-initialization | 7628 | 7726.3 | 4.2% |
| gc | 1787 | 2619.0 | 1.4% |
| v8-compile | 298 | 292.8 | 0.1% |
| idle-or-wait | 96 | 110.9 | 0.0% |

## 结论（按证据排序）

1. **Web 启动是 CPU 忙型，不是空闲等待型**：ELU active ≈ 100%、idle ≈ 0，Node CPU 占 wall 的 67%。
2. **样本主要落在 `native-or-unknown`（67%）**：主线程在原生/无法映射的帧里消耗最多；这与「模块加载 + 文件访问走原生层」一致，
   但 inspector 的采样分类**不能证明**其中有多少是 PRoot 系统调用成本——不能据此断言 I/O 主因。
3. **JS 侧模块解析与求值合计约 27%**（module-resolution 20.1% + module-eval 7.1%），GC 1.4%，V8 编译 0.1%；
   结合 E2（CLI 快速路径 160–192 ms），慢的不是 CLI/配置解析，而是 **Web Profile 依赖图的加载与初始化**。
4. **Profiler 本身开销 9.6%**：可用于归因，不可用于生产耗时对比；后续若需低开销采样，应减少采样频率或换用外部采样。
5. **停止请求延迟约 1 s**：主线程长时间同步执行 → 文件握手比端口/网络方式更适合该环境，但采样窗口末端会略超出 HTTP ready。

## 下一步（不在本记录内）

- **P2 候选**：本轮的 `native-or-unknown` 占主导，值得在**隔离且无 root 要求**的条件下做一次 PRoot 文件访问专项
  （例如比较同一模块图在 PRoot 与可用的非 PRoot 环境中的模块加载耗时；或统计模块加载期间的 fs 调用量级）。
  但这些只能作为机制旁证，不能把不同环境的数值直接当因果。
- 若继续用 inspector：先降低 profiler 开销（采样间隔 / 更短窗口 / 只在关键阶段采样），再做 OFF/ON 对照。
- Worker/子进程计数本轮未采集（记录里没有该字段）；若后续要判断主线程样本是否覆盖全部工作，需要单独加诊断。
- Compile Cache（P1）仍未证明 Web 图命中；E4 的旧数据在新报告中一律 `unverified`，不得据此开启缓存。

## 限制（如实记录）

- 单设备、单会话；未记录 SoC 温度 / 电量 / 充电状态（只有时间戳），跨天数字不可比。
- 只覆盖 **主线程** 的 V8 sample；Worker/子进程未纳入，未采集其数量。
- 分类是按栈帧 `url`/`functionName` 的启发式映射，不是时间归因的证明；`native-or-unknown` 包含所有无法映射的样本。
- `httpReadyMs` 是「首个 HTTP 状态」而非可交互；profile 在 ready 后停止，之后的初始化不在样本内。
- Profiler 开销 9.6%（over），PROFILE 行的绝对时间不可用于生产对比。
- 原始 profile 仅本地私有目录与开发机，未提交；本记录只含脱敏聚合。
