# 2026-11-15 模拟器冒烟：所有影子切片 C# 默认（ADR-0016）

- 设备：Android 模拟器 `sdk_gphone16k_x86_64`，Android 17，16 KB 页（**不是真机**；ADR-0016 的真机验收逐切片待补）
- 构建：`migration/csharp-default-all` 工作区（未提交），Debug，`dotnet build -t:Install`
- 影子报告：[2026-11-15-emulator-A17-x86_64-csharp-default-all.jsonl](2026-11-15-emulator-A17-x86_64-csharp-default-all.jsonl)
  （本轮 12 行，时间为 UTC）
- 单测：Core 65、Runtime 715、Application 208 全部通过；Android 项目 0 错误

## 环境限制（与实现无关）

- 模拟器里装的 guest 运行时是 `dshapp-runtime-debian-arm64`（ELF 机器 0xb7）。在 x86_64 上，guest 内命令以
  `proot info: vpid 1: terminated with signal 4` 退出（exit 255），**旧实现与 C# 相同**。所以诊断 / 插件 / 终端只验证了
  「默认走 C# 路径，并如实报出失败」，没有验证 guest 内的输出。
- Android 17 不允许应用 uid 访问 `10.0.2.2`（shell 用户可以），旧 JSch 与 C# 都 TIMEOUT。远程测试用
  `adb reverse tcp:22022 tcp:22022` 加主机 `127.0.0.1` 绕过，测完已移除。sshd 是 Windows OpenSSH `sshd -d`，
  它只服务一个连接。

## 结果

| 页面 / 操作 | 默认（短按）= C# | 长按 → 旧实现（回退） |
|---|---|---|
| 准备 | `prepare: C# RuntimePreparer`，`runtime.prepare (csharp): ok`（DSH 保持 0.2.1-rc.5），旧 `runtime.prepare` 未调用 | `prepare: legacy runtime.prepare (fallback, C# shadow)` → ok；影子 `runtime.dsh-provision` **Agree** |
| 诊断 | `guest diagnostics: C# PRoot launcher`；4 条固定命令逐条 `FAILED`（exit 255，见环境限制）并显示输出 | 监管观察影子 `runtime.supervisor-observe` **Agree**（旧状态 STOPPED/STOPPED） |
| 插件 | C# 列表，失败显示「插件列表读取失败」（exit 255），不再误显示为「没有已安装的插件」 | — |
| 终端 | 短按打开 `CSharpTerminalActivity`，会话以 255 退出（环境） | 「旧终端（回退）」可选 |
| 手机操控 · 开启中继 | `c# relay (guest) start: legacy stopped, ui_* layer registered`，状态「C# Relay: Running」；发布 `files/pilot/{bin/relay, capabilities.json, USAGE.md, version.json}`；停止后移除 `ui_*` 层。「协议信封比对」被拦下，提示先回退旧 Relay | 「旧 Relay（回退）」启动 / 停止；影子 `automation.ui-tools` **Agree**（start / status / stop，层逐字节一致） |
| 远程主机 · 身份 | C# 读 Keystore 身份（公钥行 + 指纹，自检通过）；密钥已存在，`Ensure` 的创建分支未走到 | — |
| 远程主机 · 保存 | C# 写入 `dshmobile_remote_hosts` / `hosts`（旧格式与键顺序） | — |
| 远程主机 · 连接 | C# 连接 → `HOST_KEY_UNKNOWN`，确认框里的指纹与 sshd 主机密钥一致；C# 固定指纹 → C# 重连 `CONNECTED/OK`（sshd 接受 ECDSA 用户密钥） | 旧 JSch 用 **C# 写入的固定指纹** 连接 `CONNECTED/OK`；影子 `remote.host-key` **Agree**。`remote.connect` 影子 Diverge（C# `UNREACHABLE`）：`sshd -d` 只服务一个连接，C# 影子的第二个连接被拒，属测试设施所致 |
| 远程主机 · 删除 | C# 删除后列表为 `[]` | — |

未在模拟器复测：C# 导入（需要系统文件选择器；此前在真机作为可选路径通过）、远端 DSH 启停（模拟器 sshd 无 dsh）、
Keystore 密钥创建、任何 guest 内执行。
