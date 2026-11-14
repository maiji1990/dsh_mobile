# 2026-11-14 · Android 模拟器 · Android 17 (API 37) · C# 宿主默认启动（ADR-0015）冒烟记录

> **这不是真机验收。** 模拟器无法跑通完整链路（见“限制”），本记录只证明：默认启动走 C# 宿主、失败时伴随组件被清理、
> 旧实现回退可用、两条路径互不覆盖、停止后无残留。真机验收（Honor PTP-AN10）仍待补。

设备：`emulator-5554`，`sdk_gphone16k_x86_64`，Android 17 / SDK 37，页大小 16384，abilist `x86_64,arm64-v8a`（ndk_translation）。
构建：Debug（`-p:EmbedAssembliesIntoApk=true`），`adb install -r --abi x86_64 -g`，基于 `53daa72` + 未提交的 `migration/csharp-host-default` 改动
（含本次在模拟器上发现的缺陷修复）。运行时包在此前的 arm64 安装下经系统文件选择器导入（旧导入器，影子 `Agree`），切换 ABI 重装时数据保留。

操作全部由 Claude 经 `adb shell input` 点本 App 自己的按钮 / 对话框完成；未接受任何系统对话框、未开启无障碍、未改系统设置。
每次进入 Activity 时系统都会弹出 “App Compatibility: isn't 16 KB compatible”（列出 DSHBox 预编译的 `libproot.so: LOAD segment not aligned`，
由 PR #28 自建 PRoot 解决），只用 `KEYCODE_BACK` 关闭，从未点 OK / “Don't Show Again”。

## 限制（为什么只能冒烟）

- **arm64 App**（`--abi arm64-v8a`）：导入正常，但 C# 宿主启动时 arm64 CoreCLR 在 ndk_translation 下原生崩溃
  （`Arm64WriteToFpsr CHECK failed`），属翻译层限制。
- **x86_64 App**：x86_64 PRoot 不能执行 arm64 Debian guest，sandbox PRoot 立即 `exit 139`。因此 DSH 在两条路径上都起不来，
  `ui_*` / 终端 / 插件在 C# 宿主下的真实使用、WebUI 加载、健康循环都无法在此验证。
- 浏览器页的 “C# 宿主下 `browser_*` 不可用” 提示只在宿主拥有进程时显示，此处宿主从未成功拥有进程，未看到。

## 序列与结果（`DshMobile` 标签，12:23 UTC 一轮，修复后的构建）

| # | 操作 | 期望 | 实际 |
|---|---|---|---|
| 1 | 短按「启动 DSH」（默认） | 走 C# 宿主，不碰旧 runtime；失败时清理伴随组件、释放前台服务 | `start: C# DshHost (legacy runtime not used)` → `csharp host: FAILED DSH_EXITED`（sandbox PRoot exit 139）→ `legacy relay: stopped with the C# host` → `start FAILED … (state=Failed dsh=Stopped)`；journal `Starting→Failed DSH_EXITED`（implementation 仍记为 legacy-bridge 协调器）|
| 2 | 长按「启动 DSH」 | 弹出本 App 的「启动实现」对话框 | 文案与按钮（取消 / 旧实现（回退））正确 |
| 3 | 点「旧实现（回退）」 | 旧 `sandbox.start` / `dsh.start` | `sandbox.start ok state=Running`；`dsh.start FAILED DSH_NOT_READY`（预期：guest 起不来）|
| 4 | 旧实现运行中再短按「启动 DSH」 | **拒绝**，不跑伴随清理、不碰宿主 | `refused: cannot start the C# host while the runtime is Running` → `RUNTIME_INVALID_TRANSITION`；无 `legacy relay: stopped`、无宿主启停行，journal 未变 |
| 5 | 「诊断」 | 宿主不拥有进程 → 走旧路径 | 旧 status / health；影子 `runtime.supervisor-observe` / `terminal.launcher-argv` / `runtime.guest-exec` 照常运行 |
| 6 | 「停止」 | 宿主不拥有进程 → 旧 `dsh.stop` + `sandbox.stop` | `dsh.stop ok`、`sandbox.stop ok`、`stop: ok (Ready)` |
| 7 | 停止后核查 | 无前台服务、无 PRoot | `dumpsys activity services org.example.dshmobile` 中 ServiceRecord = **0**；`ps -A` 无 `libproot` |

第 1 步前的一轮（修复前构建）同样核对过：失败后 `files/user-data/.dsh/dshbox/dsh-official-plugin/dsh-official-plugin-overlay.yml`（`ui_*` 层）
已随旧 Relay 停止被移除、无 ServiceRecord、无 proot。

## 模拟器上发现并已修复的缺陷

修复前，第 4 步不会被拒绝：第 1 步失败后宿主状态为 `Failed`（但什么进程都没留下），协调器把它当成“上次失败、沙箱仍保留”的重启分支，
先跑伴随清理（停掉了正在运行的旧 Relay / 终端），把 journal 记成 Ready 覆盖了正在运行的旧 runtime，再去启动 C# 宿主。
修复：重启分支只匹配 `{ State: Failed, OwnsProcesses: true }`；不拥有进程的失败宿主不再阻止也不再覆盖旧路径的判断。
回归单测 `Start_CSharpHostFails_CleansUpCompanionsAndReleasesTheService`（修复前失败、修复后通过）。

## 影子报告（本轮新增）

| 时间（UTC） | 切片 | 结论 | 说明 |
|---|---|---|---|
| 12:23:50 | `runtime.supervisor-observe` | Diverge | 旧 status 报 `sandbox=RUNNING`，C# `/proc` 观察者看不到 sandbox 进程。guest 在 x86_64 PRoot 下以 139 崩溃，旧实现的状态是缓存值；推测 C# 侧反映的是真实进程表，未进一步核实。**模拟器特有，不作为真机结论** |
| 12:23:50 | `terminal.launcher-argv` | Inconclusive | 没有打开的旧终端会话 |
| 12:23:52 | `runtime.guest-exec` | Agree | 两边 guest 命令都 exit 139，输出都为空 |

## 单测

Core 65、Runtime 688、Application 200 全部通过（含新增的宿主默认 / 回退 / 拥有权 / 伴随清理顺序 / 健康循环放弃后重启 / 上述回归用例）。
