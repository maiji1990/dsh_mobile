# ADR-0015：C# 宿主成为默认启动路径

- 状态：已接受（2026-11-14；用户选择「C# host as default」）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first、规则 3 先影子后默认）、[ADR-0010](0010-relay-guest-switch.md)
  （C# Relay 接管 guest 信箱、随沙箱停止）、ADR-0014（仓库内自建 PRoot，解决 16 KB 页对齐）、
  `docs/MIGRATION.md` 进程启动 · C# 宿主一行、[里程碑证据](../../migration/parity/2026-11-11-PTP-AN10-A17-csharp-host-milestone.md)

## 背景

`DshMobile.Runtime.Hosting.DshHost` 已能不经 DSHBox 代码启动沙箱 keepalive PRoot 与 DSH（`--profile web`、
同一组 `--patch` 覆盖层、`/opt/pilot` 与 `/opt/browser` 绑定），解析启动 token、跑健康循环，并在真机上通过里程碑：
READY、WebUI 加载、停止 / 强杀无孤儿、token 不入日志；`launcher-argv` 影子在旧实现启动的进程上逐 token 一致。
它一直是长按「启动 DSH」逐次选择的实验路径。阻碍它成为默认的是**周边功能仍以旧运行时的状态为准**：

| 功能 | C# 宿主运行时的实际情况（逐项核对源码） |
|---|---|
| 旧终端（默认终端界面） | 能用：`newSandboxSession` 自己起 PRoot，不看沙箱状态。但旧 `sandbox.stop` 会先 `stopAll()` 关掉它们，C# 宿主停止时没人关 → **会话活过停止** |
| C# 终端 | 能用；停止时 `CloseTerminalsAsync` 已关闭 |
| 插件列表 / 安装 / 卸载 | 能用：`runGuestCommand` 自己起 PRoot；改动后照旧提示重启 DSH |
| 手机操控 · 旧 Relay | 能开；但旧 `sandbox.stop` 会 `relay.stop` 并移除 `ui_*` 层，C# 宿主停止时没人做 → **信箱和 `ui_*` 层活过停止**；状态页的「DSH 是否已加载 `ui_*`」读旧 `dshState`，恒为未知 |
| 手机操控 · C# Relay（guest） | 能用；`SandboxStopping` 已随 C# 宿主停止 |
| 主页状态行 | 来自旧 `runtime.state` 事件，C# 宿主运行时显示 `sandbox=STOPPED` |
| 诊断 | 旧 `runtime.status` / `health` 报 STOPPED；`supervisor-observe` 影子跳过；guest 命令本身能跑 |
| 健康循环失败 | `DshHost` 停掉 DSH、保留沙箱、进入 `Failed`，但协调器没有跟进：`SyncAsync` 改按旧状态同步成 Ready，再次「启动」被宿主以「有旧 PRoot 树在跑」拒绝，旧实现启动也不被拦 |
| 浏览器 `browser_*` | 代理（`:browser` 进程）跟随旧 `sandboxState`，C# 宿主运行时不启动 → **不可用**。浏览器不在迁移范围内（用户决定），本 ADR 只让它说清楚 |

## 决策

1. **默认翻转，旧实现保留为显式回退**。新增 `MigrationOptions.RuntimeHost`，默认 `SliceMode.CSharp`；
   `RuntimeCoordinator.StartAsync(profile, ct, mode)` 在未指定 `mode` 时用它。长按「启动 DSH」改为选
   「旧实现（回退）」（`SliceMode.Legacy`），用于浏览器自动化与问题对照。DSHBox `sandbox-manager` 与子模块不动，
   按既定决定在所有 PR 完成后统一移除。C# 路径没有布局等前置条件时照旧返回错误码，**不**静默回落到旧实现。
2. **「宿主拥有进程」以 `State != Stopped` 为准**，不再用 `IsActive`。`Failed`（健康循环放弃、沙箱保留）同样算拥有：
   `SyncAsync` 不用旧状态覆盖日志、旧实现启动被拒绝、WebUI 走 C# 路径、停止走 C# 路径。
3. **宿主状态进日志**。协调器订阅宿主 `StateChanged`：已 Running/Ready 时宿主转为 `Failed` →
   日志 `Running / harness Failed`（原因里带 `DSH_UNHEALTHY`；Running 状态不保留错误码，与旧实现相同），与旧实现「DSH 失败、沙箱保留」同形。
   此时再「启动」= 先完整停止宿主（含下面的伴随清理），再重新启动。
4. **停止时清理旧伴随功能，与旧 `sandbox.stop` 同口径**。协调器新增异步钩子 `HostStopping`，在 C# 宿主停止
   （以及 C# 启动失败的清理）时于 `SandboxStopping` 之前调用；Android 侧接为：关闭所有旧终端会话（主线程，
   沿用现有 `CloseTerminalSession`）；C# Relay 不在 guest 模式时调用旧 `relay.stop`（停旧 Relay 并移除 `ui_*` 层，
   正是旧 `sandbox.stop` 所做）。钩子失败只记日志，不阻断停止（停止本身仍以无残留进程为准）。
   这只是在新路径上调用已有桥方法，不给旧桥加需求（规则 2）。
5. **状态与诊断以宿主为准**。宿主拥有进程时：主页状态行显示 C# 宿主状态与日志状态；诊断输出 C# 的宿主状态、
   自己的 HTTP 健康探测与 `/proc` 观察，跳过旧 `status` / `health`（它们不知道这些进程）；
   `supervisor-observe` / `launcher-argv` 影子跳过（被比对的进程就是 C# 自己起的，比对无意义），
   `terminal.launcher-argv` 影子照跑（旧终端会话仍是旧代码起的）；guest 诊断命令与 `guest-exec` 影子照跑。
6. **`ui_*` 是否已被 DSH 加载由宿主记录**。`DshHost` 在每次拉起 DSH（含就地重启）时记下当时 `ui_*` 层是否已登记
   （`UiToolsAtStart`）；手机操控页在宿主拥有进程时用它回答「已加载 / 需重启 DSH」，不再显示未知。
7. **浏览器说清楚**。宿主拥有进程时浏览器页写明 `browser_*` 需要旧实现启动（长按「启动 DSH」→ 旧实现），不伪造可用。

## 影子与验收

- 规则 3 的前提已满足：宿主路径的进程 argv 与旧实现逐 token 一致（`launcher-argv` Agree），里程碑真机通过；
  本切片新增的是编排，不是新的执行逻辑。单测覆盖：默认走 C#、显式旧实现、`HostStopping` 调用与失败、
  宿主 `Failed` 后日志、`Failed` 后重新启动、`Failed` 时拒绝旧实现启动、无布局不回落。
- 真机：默认启动 READY；旧终端 / C# 终端 / 插件列表 / 手机操控（旧与 C# Relay）在 C# 宿主下可用；停止后无旧终端、
  旧 Relay 信箱、`ui_*` 层、PRoot 残留；诊断显示宿主状态；长按回退的旧实现仍能启动。

## 后果

- 默认路径上沙箱与 DSH 进程不再由 DSHBox `sandbox-manager` 启动；它只剩回退、导入、`runtime.prepare`、
  旧终端 / 插件 / 诊断的 guest 命令等用途，移除排在所有 PR 之后。
- 浏览器自动化只在回退路径可用，直到浏览器另行处理（当前不在范围内）。
- 健康循环的「就地重启」分支仍未在真机注入故障验证（沿用里程碑记录）。
