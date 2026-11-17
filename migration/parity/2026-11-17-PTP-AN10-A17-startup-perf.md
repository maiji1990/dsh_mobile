# DSH 启动分段计时 · 真机验收 · 2026-11-17

- 设备：Honor PTP-AN10（Android 17，arm64-v8a），屏幕常亮并已解锁，全程同一台设备、同一次安装。
- App：`versionName 0.1.0`（`versionCode 10000`），由本机 **Release** 构建（Android Debug 证书），APK SHA-256
  `30634bafaa971b5b94a1b68c5cdbc17de68a1089041244ffd553cb95b035d63a`；`adb install -r` 覆盖安装，
  `firstInstallTime` 仍为 2026-11-16 18:10:19，数据保留。
- 运行层：既有安装，DSH `0.2.2-rc.3`，随包 Node `26.13.1`；未修改 Node / compile cache / MCP / PRoot / 启动顺序 / 就绪策略。
- 计时开关：开发者选项已开（「更多 → 关于」连点 7 次），因此 `startup-perf` 行写入 Logcat Tag `DshMobile`。
- 采集方式：每轮 `adb logcat -c` → 触发 → 等待完成 → `adb logcat -d -s DshMobile:I '*:S'` 存为单轮日志，
  三轮共 30 轮，然后用 `tooling/startup-perf.mjs` 汇总（P50/P95，nearest-rank）。
- 原始数据（脱敏、固定 12 列）：[A](startup-perf-2026-11-17-A.csv)、[B](startup-perf-2026-11-17-B.csv)、
  [C](startup-perf-2026-11-17-C.csv)（同目录 CSV）。

## 场景与时间

| 场景 | 定义 | 轮次 | 时间（+08:00） | 结果 |
|---|---|---|---|---|
| A | DSH 保持 READY，关掉 WebUI 再打开（不点停止） | 10 | 17:14:15–17:15:03 | 10/10 `webui LOADED` |
| B | 正常「停止」→ 再「启动 DSH」（典型重复启动） | 10 | 17:05:58–17:09:29 | 10/10 `start: ok`（gen 11–20） |
| C | `am force-stop` 杀 App → 重新打开 → 启动 DSH | 10 | 17:09:32–17:14:12 | 10/10 `start: ok`（gen 21–30） |

采集期间未控制电量 / 温度 / 充电状态，也未清理用户数据或工作区。

## B：正常停止 → 启动（10 次，全部成功）

| 阶段 | P50 (ms) | P95 (ms) |
|---|---:|---:|
| `ui.click_to_bridge` | 1.1 | 2.2 |
| `coordinator.sync` | 0.7 | 2.6 |
| `coordinator.journal` | 2.1 | 26.1 |
| `guest_assets.total` | 2.4 | 3.9 |
| `sandbox.spawn` | 47.9 | 54.5 |
| `sandbox.fixed_delay` | 300.3 | 301.7 |
| `host.proc_scan` | 3.0 | 5.7 |
| `host.cleanup_processes` | 6.7 | 8.3 |
| `host.lock_sweep` | 18.0 | 24.4 |
| `dsh.spawn` | 53.8 | 55.8 |
| `dsh.first_output`（从 DSH spawn 起算） | 15291.9 | 16749.8 |
| `dsh.token_available` | 15292.0 | 16750.0 |
| `http.first_port_open` | 16167.1 | 17992.0 |
| `http.first_response` | 16167.1 | 17992.1 |
| `host.ready` | 16167.2 | 17992.2 |
| **`bridge.complete`（点击 → 就绪）** | **16557.5** | **18388.6** |

派生分组（P50 占 `bridge.complete` P50）：

| 分组 | P50 (ms) | P95 (ms) | 占比 |
|---|---:|---:|---:|
| coordinator | 2.8 | 28.6 | 0.0% |
| guest_assets | 2.4 | 3.9 | 0.0% |
| sandbox（spawn + 固定 300 ms） | 348.2 | 355.0 | 2.1% |
| cleanup（扫描 / 清理 / 锁） | 27.7 | 35.7 | 0.2% |
| **node_init（DSH spawn → 首个 HTTP 状态）** | **16167.1** | **17992.1** | **97.6%** |
| http_wait（首个响应 → READY） | 0.0 | 0.1 | 0.0% |

## C：App 重启后启动（10 次，全部成功）

| 阶段 / 分组 | P50 (ms) | P95 (ms) | 占 `bridge.complete` P50 |
|---|---:|---:|---:|
| `coordinator.sync` | 0.8 | 1.1 | — |
| `coordinator.journal` | 3.0 | 4.0 | — |
| `guest_assets.total` | 9.3 | 18.9 | 0.1% |
| `sandbox.spawn` + `sandbox.fixed_delay` | 355.7 | 367.5 | 2.1% |
| `dsh.spawn` | 43.8 | 51.8 | — |
| `dsh.first_output` | 15678.4 | 15850.1 | — |
| `http.first_response`（node_init） | 16756.0 | 17163.8 | 97.5% |
| `host.ready` | 16756.0 | 17163.8 | — |
| **`bridge.complete`** | **17179.4** | **17588.0** | 100% |

C 比 B 多约 0.6 s（`bridge.complete` P50 17179 vs 16558），差异落在 DSH 自身初始化（node_init）与 App 冷启动后首次
guest 资产校验，宿主侧各阶段仍在毫秒级。

## A：仅打开 WebUI（10 次，全部 LOADED）

| 阶段（从 LoadUrl 起算） | P50 (ms) | P95 (ms) |
|---|---:|---:|
| `web.activity_create` | 7.0 | 61.3 |
| `web.load_url` | 0.0 | 0.0 |
| `web.page_start` | 37.6 | 7118.7 |
| `web.commit_visible` | 70.9 | 7206.9 |
| `web.page_finish` | 281.5 | 7482.1 |

逐轮 `web.page_finish`：452.6、**7482.1**、305.7、234.6、240.6、229.8、282.9、269.8、281.5、295.6 ms。
9 轮在 230–453 ms；第 2 轮约 7.5 s 才 `OnPageStarted`，属 WebView 进程冷启/回收后的单次离群，P50 不受影响、
P95 完全由它决定。**`page_finish` 只代表主文档导航结束，不代表首屏可交互**——本轮没有可交互测点。

## 观察与结论

1. **慢在 DSH 初始化，不在宿主侧路径**：B/C 中 `bridge.complete` 的 97.5%–97.6% 是从 DSH spawn 到首个 HTTP 状态的等待；
   App 侧协调器、guest 资产、沙箱、清理合计约 380–420 ms（2.3% 左右）。
2. **DSH 在 spawn 后约 15.3–15.7 s 才输出第一行**（`dsh.first_output`），再约 0.9–1.1 s 才开始响应 HTTP
   （`http.first_response` − `dsh.first_output`）。因此在 `dsh.spawn` 之后加任何 App 侧优化都不会改变数量级。
3. **1 s 轮询没有可测代价**：`http_wait`（首个响应 → READY）P50 0.0–0.0 ms、P95 0.1 ms，说明首个 HTTP 响应出现时
   健康判定已同时满足，未因轮询间隔额外等待。
4. **WebUI 常规加载约 0.23–0.45 s**；离群值来自 WebView 进程冷启，需要单独复测确认是否可稳定复现。
5. 下一步（不在本 PR 范围）：按 [STARTUP_PERF.md](../../docs/STARTUP_PERF.md) 的 E 场景只读采样 MCP / 插件 / 工作区规模，
   并在**单独诊断构建**里对 Node/DSH 初始化取 CPU profile；不要在本 PR 里改 Node 版本或 compile cache。

## 限制（如实记录）

- 单设备、单次安装、A/B/C 各 10 次；未控制温度、电量、充电状态与后台负载，未做 F 场景。
- 未测跟踪自身开销（<3% 的目标仍未验证）。
- 无「可交互」测点；`web.page_finish` 不等于可交互。
- E（MCP/插件/大工作区）未单独隔离，本轮 B/C 用的是设备上的既有配置。
- A 场景日志不含 `bridge.complete`，因此工具对每个 WebUI runId 给出 “missing bridge.complete” 提示——这是预期，不是缺陷。
