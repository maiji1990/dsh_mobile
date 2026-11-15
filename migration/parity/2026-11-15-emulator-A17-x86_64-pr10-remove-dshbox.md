# 2026-11-15 模拟器冒烟：PR-10 移除 DSHBox（ADR-0018）

- 设备：Android 模拟器 `sdk_gphone16k_x86_64`，Android 17（SDK 37），16 KB 页（**不是真机**；真机验收按负责人决定跳过，见 ADR-0018 决策 4）
- 构建：`migration/pr10-remove-dshbox` 工作区（未提交，基于 `128c2ea`），Debug，`dotnet build -t:Install -p:AdbTarget=-s%20emulator-5554`
- 单测：Core 35、Runtime 728、Application 82、`tests/guest` node 37 全部通过；`DshMobile.slnx` 0 错误
- 环境限制：运行时层为 arm64，guest 命令在 x86_64 上以 signal 4 退出（exit 255），所以 guest 侧结果都是失败——这里验证的是
  **只剩 C# 实现时各入口能否运行、失败是否如实报告**，不是 guest 功能
- 前提（未改动）：无障碍服务未开启、Shizuku 未安装。没有接受任何系统对话框，没有作答任何审批

## APK 内容（Debug，x86_64 快速部署包）

| 项 | 结果 |
|---|---|
| dex 中的包 | `com/dshbox/` 0、`dshmobile/nativebridge/` 0、`interlock/` 0、`com/github/luben/`（zstd-jni）0、`com/jcraft/`（JSch）0；`rikka/shizuku/` 52（App 直接依赖）、`dshmobile/relay/` 16（ADR-0009 的 `ShellService`） |
| 原生库 | `libproot.so` / `libproot-loader.so` / `libtalloc.so` / `libandroid-shmem.so`（自建，ADR-0014）、`libdshmobile_native.so`、.NET 运行时；无 zstd-jni、无 Termux 库 |
| 资产（除终端页与运行层） | 恰为 `dshbox/link-shim.mjs`、`dshbox/libdshbox-link.so`、`toolchain-cache/corepack-pnpm-11.1.2.tar.zst(.sha256)`（App 自己打包） |
| 合并清单（与 PR-10 之前逐项比对） | 只少浏览器 / 旧终端 / 旧 Relay 组件、`BROWSER_INTERNAL` 权限与 `MonoRuntimeProvider_1`；运行时权限、`<queries>`、`ShizukuProvider`、`V3_SUPPORT` 均保留 |

## 主页

| 操作 | 结果（日志 `DshMobile`） |
|---|---|
| 冷启动 | `runtime coordinator: impl=csharp journal=Ready/Stopped gen=8`；`runtime (C#): journal=Ready/Stopped`；状态行 `runtime=Ready · dsh=Stopped · 0.2.1-rc.5`；无旧门面日志 |
| 信息 | `app org.example.dshmobile 0.1.0 · .NET 11.0.0 · runtime impl=csharp`；`device sdk=37 abis=[x86_64,arm64-v8a]`；`runtime (C#): … runtime=True node=True … shim=True` |
| 准备 | `runtime.prepare: ok (no full layered runtime in APK (expected; base/node come from import); DSH kept at 0.2.1-rc.5)` |
| 启动 DSH | `start: DshHost` → `host: FAILED DSH_EXITED: sandbox PRoot exited immediately (exit 255)` → `start FAILED DSH_EXITED … (state=Failed dsh=Stopped)` |
| 停止 | `stop: nothing running` → `stop: ok (Ready)` |
| 诊断 | `health: port=False webUi=False …`；`host: not started in this process`；4 条 guest 命令 `FAILED GUEST_CMD_FAILED: guest command exit=255 \| proot info: vpid 1: terminated with signal 4` |

## guest 资产来自 App 自己打包的资产

先 `run-as` 删除 `files/dshbox`、`files/guest-cache/corepack`、`files/guest-cache/dshmobile/dsh-probe.mjs`、`files/user-data/.dsh/dshbox/bin`，
冷启动后点「诊断」：

```text
status: sandbox=Ready dsh=Stopped version=0.2.1-rc.5 runtime=True node=True token=False shim=False free=1956 MiB
guest assets: written: link-shim.mjs, libdshbox-link.so, dsh-probe.mjs, corepack cache (pnpm 11.1.2), dsh/pnpm wrappers (2)
```

写回后 `sha256sum`：`libdshbox-link.so` = `7d880b83…1973a`、`link-shim.mjs` = `c3450567…9b497`，corepack 就绪标记 = `d9f9677d…02498`
（与 `src/DshMobile.Android/Assets/README.md` 登记的 DSHBox v1.5.1 字节一致）；包装 `dsh` / `pnpm` 为 `-rwx--x--x`。

## 其它页面

| 页面 / 操作 | 结果 |
|---|---|
| 插件 | `plugin list exit=255`（页面显示读取失败，不显示「没有已安装的插件」） |
| 远程主机 | `remote identity: present=True hw=False selftest=True key=ecdsa-sha2-nistp256 …`；页面说明不再提浏览器；未连接任何主机 |
| 手机操控 → 开启中继 | `c# relay (guest) start: ui_* scripts staged, ui_* layer registered`；页面 `C# Relay: Running · 信箱运行中`，`DSH 工具（mcp__ui__*）：已注册` |
| 只读自检 | `relay selftest: exit=255 in 60 ms`（如实失败） |
| 关闭中继 | `c# relay (guest) stop; ui_* layer removed` → `relay status (C#): stopped, ui_* layer absent` |
| 终端 | `terminal.csharp: session 1 (终端 1) started, pid …, 46x45` → `session 1 shell ended (exit 255)` |

整个过程无 `FATAL` / `AndroidRuntime` 崩溃。

## 冒烟中修正

- 首页标题「Native Bridge POC」、「信息（长按：旧 bridge.info）」、「长按「启动 DSH」可回退旧实现」、远程「长按「连接」= 旧实现回退」与远程说明中的
  「浏览器（browser_*）」——均为已删除功能的文案，已改。
- 测试替身 `IBundledAssets` 在资产缺失时抛 `KeyNotFoundException`（真实实现抛 `IOException`，被逐步报告）；接口文档补写约定，替身改为 `FileNotFoundException`。
