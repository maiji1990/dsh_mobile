# ADR-0031：P2 体感启动（后台预热、优雅停机、分阶段提示）

- 状态：已接受（2026-11-21；与 P0/P1 正交，默认关掉预热但优雅停机与阶段提示默认生效）
- 关联：[ADR-0015](0015-csharp-runtime-host.md)（C# 宿主）、[ADR-0030](0030-proot-startup-fastpath.md)（P1 PRoot 快路径）、
  [STARTUP_PERF.md](../STARTUP_PERF.md)、[AGENTS.md](../../AGENTS.md) 规则 5 / 9 / 11

## 背景

P0/P1 把生产冷启动从约 17.7 s 压到 5.8 s（ADR-0030 报告）。但用户仍然要「点一下、等 6 秒」：

1. 冷启动的等待是**黑盒**：首页只有一句「正在启动 Linux 沙箱与 DSH…」，无法区分卡在沙箱、DSH 进程还是 HTTP 探测。
2. 每次启动都是**从零开始**：即使用户 1 分钟前刚停过 DSH，也不会复用任何已就绪状态。
3. 停止是 **SIGKILL**：Node 没有机会 flush compile cache、释放 writer lock，下一次启动就得更久，还会留下需要清扫的陈旧锁。

## 决策

### 1. 后台预热（Eager warmup，默认关闭）

- 设置项「后台预热 DSH」（更多 → 运行环境）写入 `UiPrefs.EagerWarmupEnabled`，**默认关闭**。
- 触发点：`MainActivity.OnStart`。开关打开、且 `WarmupPolicy` 判定允许时，调用
  `BridgeHost.TryWarmupAsync` → 与用户点击完全相同的 `RunStartAsync`（同一单写者锁、同一 journal、同一前台服务）。
- **决策纯函数**放在 `DshMobile.Application.Runtime.WarmupPolicy`（可单测、不依赖 Android）：
  顺序为 关闭 → 用户本次会话按过停止 → 其他操作占用 → 运行层未就绪 → 低电量（≤15% 且未充电）→ 省电模式 → 启动。
- **静默**：预热失败不弹错误卡片/Toast，只写操作日志（`Report` 只在用户点击的路径上调用）。
- **不复活用户主动停止的东西**：用户按过「停止」后，本进程内不再预热（`_userStopped`），直到用户自己点启动。
- 预热仍是真实启动：前台服务与常驻通知会出现（Android 的要求，不隐藏）；失败时状态机如实记 Failed。

### 2. 优雅停机（默认生效）

- `DshHost.StopAsync` 在 SIGKILL 之前先向 DSH 的 PRoot 进程发 **SIGTERM**（`PosixGuestTerminator` → `kill(pid, 15)`），
  最多等 **1.8 s**（`GracefulStop`，50 ms 轮询），仍未退出才走原有的 `KillEverythingAsync`（SIGKILL + marker 清扫）。
- 目的：让 Node 有机会 flush compile cache 并释放 DSH writer lock，减少下一次启动的冷缓存与陈旧锁。
- 平台差异如实处理：Windows 上 `TryTerminate` 返回 false → 直接走 SIGKILL，不额外等待（`GracefulStopTests` 覆盖）。
- 健康循环里的自动重启仍用 SIGKILL：那是「已经判定不健康」的路径，多等 1.8 s 没有收益。

### 3. 分阶段提示（默认生效，不伪造 READY）

- `DshHost` 新增 `DshHostPhase`（`SandboxStarting → SandboxReady → DshStarting → DshFirstOutput → DshTokenAvailable → Ready`，
  以及 `Stopping` / `Idle` / `Failed`），每个里程碑在真实代码点触发（spawn、首行输出、token 解析、健康策略判定）。
- `RuntimeCoordinator.HostPhase` / `HostPhaseChanged` → `BridgeHost.HostPhase`/`Changed` → 首页的详情文案。
- 首页只在 `HomePhase.Ready` 显示「已就绪」；`DshHostPhase.Ready` 与状态机 `DshHostState.Ready` 在同一处设置，顺序固定。

## 不变 / 不做

- 不改 DSH 内核、Cordis 插件、`link-shim.mjs`，不改任何权限审批与安全校验（规则 1、11）。
- 预热不绕过前置条件（PRoot 只从 nativeLibraryDir、运行层必须已装），也不跳过 journal。
- 不用「假装 READY」或进度条百分比换体感：阶段文案都对应真实事件。

## 后果

- 打开预热后，「回到 App → 稍后点击启动」的等待从数秒降到亚秒级（真机数据见
  [P2 报告](../../migration/parity/2026-11-21-PTP-AN10-A17-P2-perceived-startup.md)）；关闭时行为与今天完全一致。
- 停止最多多花 1.8 s（仅在 SIGTERM 无效时才走满），换来 compile cache 与 writer lock 的干净退出。
- 新增一个用户可见开关、5 条阶段文案；默认值保守（预热关）。

## 限制

- 预热受 Android 前台服务约束：Android 要求设备可见的常驻通知，因此「静默」指不打扰、不弹窗，不是零通知。
- 预热与用户点击共用同一个单写者锁：点得早会排队等预热完成，而不是并发启动两个 DSH。
- 阶段文案依赖 `DshHost` 的事件粒度；HTTP 探测期间不会按百分比前进（没有真实进度可报）。
