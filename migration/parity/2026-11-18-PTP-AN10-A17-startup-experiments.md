# 启动根因专项 E1–E4 · 真机记录 · 2026-11-18

- 设备：Honor PTP-AN10（Android 17，arm64-v8a），屏幕常亮并已解锁；全程同一台设备、同一次会话。
- App：`versionName 0.1.0`（`versionCode 10000`）**诊断构建**（`dotnet build -c Release -p:DshDiagnostics=true`），
  APK SHA-256 `38f677ad920971ba698284b89622dd134ba5840757444839d8f9d9351f1cede9`；`adb install -r` 覆盖安装，
  数据保留。诊断入口只在开发者选项里出现（普通 Release 构建没有该入口）。
- 运行层：既有安装，DSH `0.2.2-rc.3`，Node `26.13.1`。未改 Node / compile cache 默认值 / MCP / PRoot / 启动顺序 /
  就绪策略；3080 端口在每次实验前由诊断器确认空闲，DSH 必须已停止，否则拒绝运行。
- 数据：诊断器每个测量一行 JSONL，写在 App 外部私有目录
  `Android/data/io.github.maiji1990.dshmobile/files/startup-experiments/`；汇总用
  `tooling/startup-experiment-report.mjs`（CSV 22 列 + P50/P95 + 配对差值 + matrix）。
- 原始脱敏数据：[experiments CSV](startup-experiment-2026-11-18.csv)、[matrix](startup-experiment-2026-11-18.matrix.json)、
  [T0](startup-perf-2026-11-18-T0.csv)、[T1](startup-perf-2026-11-18-T1.csv)。

## 时间线（同一会话，按方案 T0→E1→E2→E3→E4→T1）

| 顺序 | 内容 | 时间（+08:00） | 结果 |
|---|---|---|---|
| T0 | 现有 B 基准（停止 → 启动 ×10） | 11:00:51–11:05:40 | 10/10 ok |
| E1 | PRoot / Node 最小成本（4 变体 ×10，含 2 次预热） | 11:06:53– | 40/40 ok |
| E2 | DSH CLI 入口（4 变体 ×10） | 同上批次 | 30/30 ok（E2-3 首轮参数错误，见下） |
| E3 | Web 生产配置 vs 隔离配置（3 变体 ×10） | 11:07:25–11:15:46 | 30/30 ok |
| E4 | Compile cache 关闭 / 冷 / 热（A10 + B1 + C10） | 11:15:53–11:21:47 | 21/21 ok |
| E2-3 | 修正参数后单独重测 `--dump-config` ×10 | 11:28:46 | 10/10 ok |
| T1 | 复测 B 基准 ×10（排除渐热 / 工具影响） | 11:29:17–11:33:54 | 10/10 ok |

## 基准 T0 / T1（App 真实启动：点击 → DSH READY）

| 指标 | T0 P50 / P95 | T1 P50 / P95 |
|---|---:|---:|
| `bridge.complete`（点击 → 就绪） | 18921.4 / 23863.3 ms | 18814.0 / 19571.2 ms |
| `http.first_response`（spawn → 首个 HTTP 状态） | 18525.8 / 23372.5 ms | 18407.9 / 19173.2 ms |
| `dsh.first_output`（spawn → 首行输出） | 17229.5 / 21997.8 ms | 17371.1 / 17891.0 ms |
| `sandbox.spawn` + 固定 300 ms | 47.5 / 69.3 ms | 50.0 / 61.9 ms |
| `guest_assets.total` | 2.9 / 28.9 ms | 2.9 / 26.7 ms |

T0 与 T1 相差 0.1 s（18.92 vs 18.81 s），说明本会话内没有渐热漂移；但两者都比 2026-11-17 的
`bridge.complete` P50 16.56 s 慢约 2.3 s，属**跨天/热状态差异**，因此**跨批次的绝对耗时不可直接比较**，
本记录只做同批次、同终点的配对比较。

## E1 / E2：PRoot、Node、DSH CLI 入口（终点＝进程退出）

| 变体 | 内容 | n | P50 (ms) | P95 (ms) | min–max (ms) |
|---|---|---:|---:|---:|---|
| E1-0 | `/bin/true`（PRoot + guest 最小开销） | 10 | 14.7 | 20.5 | 10.1–20.5 |
| E1-1 | `node --version` | 10 | 23.6 | 29.4 | 15.3–29.4 |
| E1-2 | `node -e`（V8 + JS 入口，打印固定 marker） | 10 | 60.1 | 62.7 | 47.0–62.7 |
| E1-3 | E1-2 + 正式 `--import` link shim | 10 | 56.5 | 69.0 | 51.5–69.0 |
| E2-0 | `node -e 0` | 10 | 48.5 | 51.8 | 43.1–51.8 |
| E2-1 | `bin.js --version` | 10 | 160.3 | 184.2 | 143.8–184.2 |
| E2-2 | `bin.js --help` | 10 | 165.3 | 169.9 | 147.9–169.9 |
| E2-3 | `bin.js --profile web --dump-config` | 10 | 191.8 | 214.5 | 172.9–214.5 |

配对差值（同一迭代相减）：E1-0→E1-1 +6.8 ms、E1-0→E1-2 +41.0 ms、E1-2→E1-3 **+4.6 ms**（shim 几乎不增加成本）、
E2-0→E2-1 +111.6 ms、E2-0→E2-2 +116.5 ms。

**E2-3 首轮失败（已修正）**：首次以 `bin.js --dump-config` 直接调用，10 次全部 `exit 1`——该子命令需要
`--profile`。修正为 `--profile web --dump-config` 后单独重测 10/10 ok。上表采用修正后的数据；首轮 10 条失败记录
仍在原始 JSONL 中，未混入统计。

## E3：Web 生产配置 vs 隔离配置（终点＝首个 HTTP 状态）

| 变体 | 配置 | n | P50 (ms) | P95 (ms) | min–max (ms) |
|---|---|---:|---:|---:|---|
| E3-P | 生产 `user-data` / `DSH_HOME` / 覆盖层（与 App 启动同味） | 10 | 16820.5 | 17440.0 | 15330.9–17440.0 |
| E3-0 | 隔离空 `DSH_HOME` + 空 workspace，无覆盖层 | 10 | 16061.2 | 21997.0 | 13886.2–21997.0 |
| E3-1 | E3-0 + 已登记覆盖层（`--patch`） | 10 | 16004.4 | 17083.5 | 14194.6–17083.5 |

配对：E3-P→E3-0 **−807.0 ms**（P50）、E3-P→E3-1 −913.1 ms；E3-0→E3-1 −289.2 ms（覆盖层没有可辨增量）。
也就是说，即使用户配置、MCP/插件声明、工作区与覆盖层全部拿掉，Web 启动仍是 **16.0 s 量级**；用户配置只占约 0.8 s。

## E4：Node Compile Cache 对照（终点＝首个 HTTP 状态）

| 变体 | 缓存状态 | n | P50 (ms) | P95 (ms) | 缓存文件（运行前） |
|---|---|---:|---:|---:|---|
| E4-A | 关闭 `NODE_COMPILE_CACHE` | 10 | 15401.3 | 17025.3 | — |
| E4-B | 首次启用、空缓存 | 1 | 17207.6 | — | 0（运行后仍为 0） |
| E4-C | 已预热缓存（由 `bin.js --version` 正常退出写入） | 10 | 17126.3 | 18820.9 | 68 个 / 253,264 B |

配对：E4-A→E4-C **+1063.7 ms**（P50）、P95 +4439.4 ms，即**没有任何收益**；E4-B 在 Web 进程被强杀后缓存文件仍为 0，
说明 **Web 进程自身无法把 compile cache 落盘**；E4-C 的 68 个文件只来自 `--version` 快速路径，并不代表 Web 模块图
被缓存。因此本轮结论是：**compile cache 按当前方式预热无效，不得据此开启或宣称收益**；要验证 Web 图缓存需要
`module.flushCompileCache()` / inspector 之类的独立诊断 PR（方案 §7.1）。

## 结论（按证据排序）

1. **PRoot 与 Node 最小执行不是瓶颈**：E1-0 14.7 ms、E1-2 60.1 ms、E1-3 56.5 ms（shim +4.6 ms）。
2. **DSH CLI 入口与配置合成不是瓶颈**：E2-1 160 ms、E2-2 165 ms、E2-3（`--profile web --dump-config`）192 ms。
3. **用户配置 / MCP / 覆盖层 / 工作区不是主因**：E3-0 隔离空环境仍 16.06 s，仅比生产配置少约 0.8 s。
4. **剩余约 15.8–16.8 s 落在 DSH Web Profile 自身初始化**（Node 起来之后、listen 之前：依赖图加载 / V8 编译 /
   服务与工具注册）。**这是定位结论，不是因果证明**——本轮没有 CPU profile，不能断言具体热点。
5. **Compile cache 无效**（E4），且 Web 进程退出前无法落盘。

## 下一步（不在本记录内）

- 按方案 §7.1 在**单独诊断构建**中对 Web Profile 取 Node CPU profile（或 inspector `Profiler.start/stop` 后正常退出），
  对同一变体做「无 profiler / 有 profiler」对照，报告 profiler 开销；不能把 profiler 构建的绝对耗时当正式版。
- 若要评估 compile cache 的真实收益，必须先证明 Web 图被缓存（`flushCompileCache` 或正常退出），否则仍标
  `not-evaluable`。
- Perfetto / PRoot syscall 采样只在 CPU profile 仍不足以解释时再做；Android 无 root 时明确记 unavailable，不要求 root。

## 限制（如实记录）

- 单设备、单次会话；未记录 SoC 温度 / 电量 / 充电状态（只有时间戳），因此跨天数字不可比。
- CPU / RSS / IO 指标在诊断器中均为 `null`（Android 无 root 下不可靠读取），没有编造。
- 每变体 10 次（E4-B 为 1 次，已标 `insufficient samples`）；未做 95% 置信区间。
- E3-P 使用生产 `user-data`（会写会话/日志），E3-0/E3-1/E4 使用隔离目录并在结束时删除；未修改用户数据。
- 无 CPU profile、无 Perfetto、无 PRoot syscall 采样；「可交互」仍未定义。
