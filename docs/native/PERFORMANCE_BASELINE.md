# PERFORMANCE_BASELINE — Android 17 真机 A/B/C 配对采集（PR-08 / N-08-07）

> 采集日期：2026-11-24 10:09–10:24（Asia/Shanghai）
> 设备：Xiaomi M2007J1SC（LineageOS 24.0，Android 17 / API 37，arm64，userdebug，非 root）
> APK：Release io.github.maiji1990.dshmobile-Signed.apk，SHA-256 C0A35590B3EF5B9D266BEFE891C955C767AA8BE49996953C714EE07532B430E8
> 运行层：DSH 0.2.2-rc.5，guest Node 26.13.1（runtime-bundle/runtime-build.lock）
> 采集方式：tooling/startup-perf.mjs（只读 logcat，Tag DshMobile，标记 startup-perf；开发者选项已开启）
> 原始证据：artifacts/startup-perf/pr08/{A,B,C}-{01..10}.log、{A,B,C}-all.csv、all.csv（571 行）、{A,B,C}-summary.txt

## 1. 场景定义与样本

| 场景 | 操作 | 样本 | 备注 |
|---|---|---|---|
| A 仅页面 | DSH 保持 READY，点「打开 DSH」进入 WebUI 6 s 后返回，重复 10 次 | 10 次 / 50 条记录 | 不参与 host_total 百分比（无 bridge.complete，工具按设计给出 missing 警告） |
| B 典型重复启动 | 点「停止」→ 确认未启动 → 点「启动 DSH」→ 等到「DSH 已就绪」 | 10 次 / 260 条记录 | 每次启动前清空 logcat |
| C App 重启后启动 | am force-stop → am start → 点「启动 DSH」→ 等到就绪 | 10 次 / 260 条记录 | 覆盖 .NET 冷启动、journal 回放与 guest 资产校验 |

30 次采集中 skipped lines = 0，result 全部为 ok，无丢行、无失败/取消样本。

## 2. 结果（P50 / P95，毫秒）

### A — 仅页面（WebView 阶段，elapsedMs 从 LoadUrl 起算）

| 阶段 | n | P50 | P95 |
|---|---|---|---|
| `web.load_url` | 10 | 0.0 | 0.0 |
| `web.activity_create` | 10 | 11.5 | 108.4 |
| `web.page_start` | 10 | 59.3 | 100.4 |
| `web.commit_visible` | 10 | 157.6 | 378.8 |
| `web.page_finish` | 10 | 1427.7 | 1692.2 |

### B — 停止后启动

| 阶段 | n | P50 | P95 |
|---|---|---|---|
| `bridge.complete`（点击 → 就绪） | 10 | **16834.5** | 20374.6 |
| `host.ready` | 10 | 16073.5 | 19739.2 |
| `http.first_response` | 10 | 16073.4 | 19739.1 |
| `dsh.first_output` | 10 | 16275.2 | 19828.0 |
| `dsh.spawn` | 10 | 39.7 | 43.3 |
| `sandbox.spawn` | 10 | 46.1 | 52.8 |
| `guest_assets.total` | 10 | 5.6 | 33.5 |
| `protocol.connected` / `protocol.initialized` | 10 | 7.6 / 7.0 | 23.9 / 9.6 |

| 派生分组（B） | n | P50 (ms) | P95 (ms) | 占 `host_total` P50 | 标记 |
|---|---|---|---|---|---|
| `coordinator` | 10 | 4.0 | 6.1 | 0.0% | — |
| `guest_assets` | 10 | 5.6 | 33.5 | 0.0% | — |
| `sandbox` | 10 | 61.5 | 71.7 | 0.4% | — |
| `cleanup` | 10 | 7.7 | 10.9 | 0.0% | — |
| `node_init` | 10 | 16073.4 | 19739.1 | **95.5%** | `candidate` |
| `http_wait` | 10 | 0.0 | 0.8 | 0.0% | — |
| `host_total` | 10 | 16834.5 | 20374.6 | 100.0% | — |

### C — App 进程重启后启动

| 阶段 | n | P50 | P95 |
|---|---|---|---|
| `bridge.complete` | 10 | **17977.4** | 19048.4 |
| `host.ready` | 10 | 17048.0 | 18324.4 |
| `http.first_response` | 10 | 17047.6 | 18324.1 |
| `dsh.first_output` | 10 | 17317.6 | 18438.0 |
| `guest_assets.total` | 10 | 15.1 | 18.7 |
| `coordinator.journal` | 10 | 3.0 | 6.2 |
| `protocol.connected` / `protocol.initialized` | 10 | 11.7 / 19.9 | 18.7 / 29.2 |

| 派生分组（C） | n | P50 (ms) | P95 (ms) | 占 `host_total` P50 | 标记 |
|---|---|---|---|---|---|
| `coordinator` | 10 | 4.8 | 7.6 | 0.0% | — |
| `guest_assets` | 10 | 15.1 | 18.7 | 0.1% | — |
| `sandbox` | 10 | 56.0 | 77.8 | 0.3% | — |
| `cleanup` | 10 | 15.9 | 20.9 | 0.1% | — |
| `node_init` | 10 | 17047.6 | 18324.1 | **94.8%** | `candidate` |
| `http_wait` | 10 | 0.3 | 0.7 | 0.0% | — |
| `host_total` | 10 | 17977.4 | 19048.4 | 100.0% | — |

## 3. 解读

1. **启动时间仍由 DSH 自身初始化支配**：B/C 的 dsh.first_output 与 http.first_response 几乎相等（16.1–17.3 s），说明 PR-08 的治理、探针与租约逻辑没有进入启动热路径；宿主侧 C# 阶段合计约 0.1 s。
2. **B 与 C 差异约 1.1 s**（16.8 s → 18.0 s），P95 反而更稳定（20.4 s → 19.0 s），落在 .NET 冷启动与 logcat 恢复噪声范围内，与 2026-11-17 基线（bridge.complete P50 16.6 s）一致，未出现回归。
3. **A 场景** web.page_finish P50 1.43 s，commit_visible 158 ms；首次渲染到 page_finish 之间仍有约 1.3 s 的 JS/数据加载，和 STARTUP_PERF 的「PageFinished ≠ 可交互」结论一致。
4. protocol.connected / protocol.initialized 在 B/C 均有测点（PR-04 遗留的缺失测点已由 PR-08 的 DshHostOptions.ProtocolHandshakeAsync 补齐）。

## 4. 限制

- 单设备、单构建、单次会话内的连续采样；未做跨 ROM / 跨 APK 对比，也未做温控与亮灭屏分层（D–F 场景仍未采集）。
- web.* 只有 onPageStarted / commit / onPageFinished 近似点，不能宣称「可交互时间」。
- 采集中开发者选项开启（startup-perf 才产生日志），跟踪开销未单独测量。
- CSV 只含固定 12 列；本文件与 artifacts/startup-perf/pr08/ 内不含 token、prompt 或环境变量。

最后更新：2026-11-24
