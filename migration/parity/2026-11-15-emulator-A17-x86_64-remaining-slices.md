# 2026-11-15 模拟器冒烟：其余切片 C# 默认 + 旧门面懒加载（ADR-0017）

- 设备：Android 模拟器 `sdk_gphone16k_x86_64`，Android 17，16 KB 页（**不是真机**；真机验收待补）
- 构建：`migration/ui-exec-csharp` 工作区（未提交），Debug，`dotnet build -t:Install`
- 单测：Core 65、Runtime 739、Application 216 全部通过；Android 项目 0 错误
- 环境限制：运行时层为 arm64，guest 命令在 x86_64 上以 signal 4 退出（exit 255），所以 guest 侧结果都是失败——这里验证的是
  **默认路径由谁执行、失败是否如实报告**，不是 guest 功能
- 前提（未改动）：无障碍服务未开启、Shizuku 未安装。没有接受任何系统对话框，没有作答任何审批

## 启动：旧门面不初始化，状态由 C# 给出

| 操作 | 结果（日志 `DshMobile`） |
|---|---|
| 冷启动 | `legacy facade: not initialized (created on the first legacy fallback)`；`runtime coordinator: impl=csharp journal=Ready/Stopped gen=7`；`runtime (C#): journal=Ready/Stopped` |
| 状态行 | `runtime=Ready · dsh=Stopped · 0.2.1-rc.5`（C# journal） |

## guest 资产由 C# 写入

先删除旧桥写过的 `files/dshbox/`、`files/guest-cache/corepack/`、`files/guest-cache/dshmobile/dsh-probe.mjs`、
`files/user-data/.dsh/dshbox/bin/`，冷启动后点「诊断」：

```text
status (C#): sandbox=Ready dsh=Stopped version=0.2.1-rc.5 runtime=True node=True token=False shim=False free=1956 MiB
health (C#): port=False webUi=False http=- error=SocketException
csharp host: not started in this process
guest assets (C#): written: link-shim.mjs, libdshbox-link.so, dsh-probe.mjs, corepack cache (pnpm 11.1.2), dsh/pnpm wrappers (2)
guest diagnostics: C# PRoot launcher, 4 commands in 191 ms
diag arch|debian|node|dsh: FAILED GUEST_CMD_FAILED: guest command exit=255 | proot info: vpid 1: terminated with signal 4
```

`run-as` 核对：`files/dshbox/{link-shim.mjs 2256 B, libdshbox-link.so 67536 B}`、`guest-cache/dshmobile/dsh-probe.mjs`、
`guest-cache/corepack/{.ready-sha256, lastKnownGood.json, v1/}`、`user-data/.dsh/dshbox/bin/{dsh 604 B, pnpm 945 B}`（`-rwx--x--x`，
与旧 `setExecutable(true, false)` 相同）。（状态行的 `shim=False` 读于写入之前；随后「信息」显示 `shim=True`。）

## 各默认路径

| 操作 | 结果 |
|---|---|
| 「DSH 模式探测」 | 6 个 profile 均 `FAILED PROBE_NO_RESULT: GUEST_CMD_FAILED … signal 4`；`probe summary (C#): web=ERROR headless=ERROR sdk=ERROR sdk-minimal=ERROR acp=ERROR plugin=ERROR` |
| 「信息」 | `app … runtime impl=csharp`、设备 / 原生 ABI / journal、`runtime (C#): … shim=True`、`legacy facade: not initialized · long-press = legacy bridge.info` |
| 「版本比较」 | 三组 `Versions.Compare(...) = 1 / -1 / 0 (C#)` |
| 「启动 DSH」 | `start: C# DshHost (legacy runtime not used)` → `csharp host: FAILED DSH_EXITED: sandbox PRoot exited immediately (exit 255)`；状态行 `runtime=Failed · dsh=Stopped · 0.2.1-rc.5 · DSH_EXITED` |
| 插件页 | `plugin list (C#) exit=255`，页上「插件列表读取失败」并显示原因 |
| 插件页短按「安装」`@dshmobile-test/none@1.0.0` → 本应用确认框（说明为 C# 安装守卫、长按 = 旧实现）→「安装」 | `csharp plugin.add …: GUEST_CMD_FAILED in 0.1 s`；刷新列表后失败结果仍留在页上 |
| 长按「导入」→「回滚到上一版」→ 确认 | `rollback: BUNDLE_NO_ROLLBACK_SLOT (no previous runtime body)`；`runtime-current/` 仍为 `android-side base dsh node runtime-profile.json tmp`（未改动） |
| 远程主机页 | 只有 `remote identity (C#)`，无旧 `remote.status`；「断开」→ `remote disconnect (C#)`、页上「已断开（C#）」；未连接时「启动远端 DSH」→「未连接：先点「连接」」 |
| 「打开 DSH」 | `webui: C# WebView (DSH not running)` → `webui(csharp) WAITING_TOKEN` |
| 「终端」 | `terminal.csharp: session 1 … started` → `shell ended (exit 255)`（页上显示 PRoot 输出） |
| 「手机操控」页 | `relay status (C#): stopped, ui_* layer absent`；Shizuku 状态「未运行或未安装」——Shizuku 类来自 App 直接依赖，日志无 `ClassNotFound` / `NoClassDefFound` |

以上全部操作之后再点「信息」：仍为 `legacy facade: not initialized`——整个流程没有初始化旧门面。

## 显式回退：懒加载

长按「信息」：

```text
legacy facade initialized on first use, bridge 0.1.0
event bridge.lifecycle {"state":"INITIALIZED"}
bridge 0.1.0 protocol=1 kotlin=2.4.0
event runtime.state {"sandboxState":"UNINITIALIZED","dshState":"UNINITIALIZED","dshVersion":null}
upstream dshbox=v1.5.1-0-g7c9d33c
```

旧门面在第一次回退时才初始化；其 `runtime.state` 事件（`UNINITIALIZED`）没有改动状态行（仍为 C# journal 的 `runtime=Ready · dsh=Stopped`），
因为本进程没有经回退启动旧运行时。

## 未覆盖

- 真机：guest 成功路径（探测 6 个 profile 的实际结果、插件安装守卫回滚、回滚成功路径）与 `Shizuku` 绑定。
- 回滚成功路径只有单测（换回、中途失败撤销）；模拟器上无 `previous/`。
