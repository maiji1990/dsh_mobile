# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · `ui_*` 工具注册与状态影子（PR-07 第一段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `54880c0` + 未提交的 PR-07 第一段改动。
运行时：沙箱 / DSH 未启动（`UNINITIALIZED`）——注册只写 user-data 下的 patch 层，与 DSH 是否运行无关。
操作：主界面「手机操控（实验）」→ 页面打开时自动 `relay.status` →「开启中继」→「关闭中继」。证据为 `adb logcat -s DshMobile:I`。

无障碍设置（只读，`adb shell settings get secure enabled_accessibility_services`）：只有另一个应用的两个服务，
本 App 的 `interlock.relay.core.exec.a11y.RelayAccessibilityService` 未开启——两侧都应得出 `a11y=False`。

## 结果

```text
10:39:34.331 relay.status: phase=STOPPED channel=False a11y=False
10:39:34.339 shadow automation.ui-tools: Agree (after relay.status: registered=False conflict=False a11y=False, static facts equal)
10:39:41.545 relay.start: phase=STARTING channel=False a11y=False
10:39:41.546 shadow automation.ui-tools: Agree (after relay.start: registered=True conflict=False a11y=False, layer byte-identical, static facts equal)
10:39:50.691 relay.stop: phase=STOPPED channel=False a11y=False
10:39:50.692 shadow automation.ui-tools: Agree (after relay.stop: registered=False conflict=False a11y=False, static facts equal)
```

- 三次 **Agree**。开启后旧实现写下的 `user-data/.dsh/dshbox/dsh-official-plugin/dsh-official-plugin-overlay.yml` 与
  C# `UiToolsRegistration.Render()` **逐字节一致**；关闭后两侧都看到层已删除。
- 静态事实一致：能力白名单 `ui.node, ui.snapshot`、guest CLI `/opt/pilot/bin/relay`、MCP server 名 `ui`。
- C# 侧比对耗时 ≤ 1 ms（日志时间差）。影子只读：不写、不删 patch 层。

## 未覆盖

- 无障碍「已开启」：未在系统设置里开启本 App 的服务（属于用户操作），只有 `accessibility-enabled.json` 12 例向量证据。
- 冲突分支（层文件是别人的内容 / 是目录）：Release 不可 `run-as`，无法在 App 目录放外来文件；只有向量（新旧双跑）。
- C# 写入路径：旧 `relay.start` 在同一桥调用里同时启动 Relay 并注册，C# 单独接管注册需要先有 C# 执行层，留到后续段。
