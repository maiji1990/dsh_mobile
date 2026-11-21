# P2 体感启动 · 真机报告 · 2026-11-21

- 设备：Honor PTP-AN10（Android 17，arm64-v8a），屏幕常亮并已解锁；同一天、同一台设备、同一个运行层（DSH `0.2.2-rc.4` / Node `26.13.1`）。
- 构建：`96DBD32F19EB…`（Release + `DshDiagnostics=true`，含 P1 的 PRoot 只读前缀缓存）。
- 依据：[ADR-0031](../ADR/0031-p2-perceived-startup.md)、[STARTUP_PERF.md](../STARTUP_PERF.md) 的解析口径。
- 方法：真机 adb 驱动 `artifacts/p2/*.ps1`；计时来自 logcat 的 `startup-perf` 记录（不是 UI 轮询的墙钟），
  阶段文案来自 `uiautomator dump`（约 1 s 一次，对启动本身有轻微干扰，只用于文案证据，不用于计时）。

## 一、点击启动的分解（场景 B，冷启动一次）

| 阶段 | elapsedMs |
|---|---:|
| `sandbox.spawn` | 52 |
| `sandbox.fixed_delay` | 7 |
| `dsh.spawn` | 37 |
| `dsh.first_output` | **4 805** |
| `dsh.token_available` | 4 806 |
| `http.first_port_open` | 4 775 |
| `http.first_response` | 4 776 |
| `host.ready` | 4 776 |
| `bridge.complete`（点击→READY） | **4 876** |

关键事实：**宿主与沙箱只占约 100 ms，其余 4.7 s 全部是 DSH/Node 自身的模块加载**；而且 DSH 直到约 4.8 s
才写出第一行（stdout 是管道，Node 有缓冲），所以 token / 端口 / READY 几乎同时到达。

## 二、后台预热（Eager warmup，开关打开）

`am force-stop` 重置进程后启动 App（**不点任何按钮**），预热自行完成：

| 阶段 | elapsedMs |
|---|---:|
| `sandbox.fixed_delay` | 8 |
| `dsh.spawn` | 38 |
| `dsh.first_output` | 5 024 |
| `dsh.token_available` | 5 024 |
| `host.ready` | 4 968 |
| `bridge.complete`（App 启动→READY，无点击） | **5 062** |

UI 采样（2 次，间隔 `am force-stop` + 重新启动）：ready=**8.6 s** 是**含 uiautomator 轮询开销**的墙钟；
logcat 的真实值是 5.06 s，二者不可混用。

### 体感收益怎么算

| 路径 | 用户点「启动 DSH / 打开 DSH」后等待 | 说明 |
|---|---:|---|
| 无预热 | 4.88 s（点击→READY） + 2.9 s（WebView 载入到 `web.page_finish`） | 两段串行 |
| 有预热（用户打开 App ≥ 5 s 后才去点） | ≈ 2.9 s（只剩 WebView） | DSH 已在后台 READY |
| 有预热（App 已在后台、DSH 保持就绪） | ≈ 0（WebUI 直接复用） | 场景 A |

即：**同一次「想用 DSH」的动作，等待从约 7.8 s 降到约 2.9 s（−63%）**；若 App 一直活着，则只剩 WebView 的一次创建。
WebView 的 2.9 s 是 P2 之外的另一层成本（`web.page_finish`），没有在本切片里优化，也不宣称它变快了。

## 三、优雅停机

| 指标 | 值 |
|---|---|
| 点「停止」→ 设备上 `libproot` 进程数归零 | **2.36 s**（含最多 1.8 s 的 SIGTERM 等待 + marker 清扫） |
| 停止后残留的 marker 进程 | 0（`ps -A` 复核） |

实现为 SIGTERM（`kill(pid, 15)`）→ 50 ms 轮询等待 → 超时才 SIGKILL；Windows 上直接跳过等待（单测覆盖）。
这 2.36 s 换来的是 Node 有机会 flush compile cache 并释放 DSH writer lock（缺省路径原本是直接 SIGKILL）。

## 四、分阶段提示的真实边界（重要）

实现上是六个真实里程碑（沙箱启动 / 沙箱就绪 / DSH 进程 / 首行输出 / 启动令牌 / READY），但本机数据显示：

- 沙箱阶段只占 ~60 ms，几乎没有可感知的停留；
- **DSH 进程启动后到首行输出之间有约 4.8 s 的空白**（Node 加载 DSH 模块，stdout 静默）；
- 首行输出、令牌、端口、READY 在 1 ms–300 ms 内同时发生。

因此在这台设备上用户实际能看到的是「准备沙箱 → DSH 进程已启动（等待中）→ 已就绪」三段，
**首行输出/令牌两档停留不到一次屏幕刷新**。这不是实现没接上，而是 DSH 自身的输出节奏决定的；
要么接受（不伪造进度），要么让 DSH 在启动早期打印一行心跳（属于上游改动，本切片不做）。

## 结论

1. **预热把「点一下再等」的等待从 4.88 s 挪到 App 启动后**：用户真正操作时只剩 WebView 的约 2.9 s。
2. **预热的代价是真实启动**：前台服务与常驻通知会出现；失败只写操作日志，不弹错误卡片。
3. **优雅停机在真机上 2.36 s 内完成且无残留**，为 compile cache / writer lock 的干净退出留出窗口。
4. **阶段提示只能做到「沙箱 / DSH 进程 / READY」三档**：中间两档被 DSH 的 stdout 缓冲压缩掉，如实记录，不补假进度。

## 限制

1. 预热收益依赖「用户打开 App 后没有立刻点」；如果用户打开即点，跑的是同一条启动路径，收益接近 0。
2. 计时样本少（冷启动 1 次完整 trace、预热 1 次 trace + 2 次 UI 采样、停止 2 次）；场景 A 的「毫秒级」没有单独采数。
3. 预热在 `am force-stop` 后由 `OnStart` 触发来测量；正常使用中是「切回前台」触发，触发条件相同（都走 `MainActivity.OnStart`）。
4. 阶段文案采样用 uiautomator，约 1 s 一次，快于它的阶段变化可能被漏采。
5. 单设备、单日；温度/电量/充电状态未记录。

## 原始数据

- 驱动脚本：`artifacts/p2/measure2.ps1`（冷启动/预热计时）、`artifacts/p2/warmup-measure.ps1`（UI 采样）、`artifacts/p2/stages.ps1`（阶段文案）
- 计时日志：`artifacts/p2/tapstart.log`（冷启动 trace）、`artifacts/p2/warmup-1.log`（预热 trace）、`artifacts/p2/cold.txt`、`artifacts/p2/warmup.txt`、`artifacts/p2/stop.txt`
- APK：`artifacts/p2/P2.apk`（`96DBD32F19EB…`）
