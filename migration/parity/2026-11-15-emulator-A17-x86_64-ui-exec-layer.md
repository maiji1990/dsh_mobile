# 2026-11-15 模拟器冒烟：ui_* 执行层 C# 收口（ADR-0016 补充）

- 设备：Android 模拟器 `sdk_gphone16k_x86_64`，Android 17，16 KB 页（**不是真机**；真机验收待补）
- 构建：`migration/csharp-default-all` 工作区（未提交），Debug，`dotnet build -t:Install`
- 单测：Core 65、Runtime 721、Application 210 全部通过
- 前提（未改动）：无障碍服务未开启、Shizuku 未安装；悬浮窗权限此前已在系统里授予。没有接受任何系统对话框，**没有作答任何审批**。

## 默认路径不经旧桥

| 操作 | 结果（日志 `DshMobile`） |
|---|---|
| 打开「手机操控」页（C# Relay 未运行） | `relay status (C#): stopped, ui_* layer absent`；没有旧 `relay.status` 调用 |
| 先删除 `files/guest-cache/dshmobile/`，再短按「开启中继」 | `c# relay (guest) start: ui_* scripts staged, ui_* layer registered`；写入的 `ui-facade.mjs` / `ui-facade-check.mjs` 的 SHA-256 与仓库源文件一致（`1999936c…505a` / `f637344b…d3d4`）；`files/pilot/{bin/relay, capabilities.json, USAGE.md, version.json}` 已发布 |
| 「只读自检」 | `relay selftest: C# PRoot launcher, exit=255`（guest 为 arm64，x86_64 上 guest 命令以 signal 4 退出，与旧实现相同的环境限制） |
| 「关闭中继」 | `c# relay (guest) stop; ui_* layer removed`，随后 C# 停止态状态；没有残留的前台服务 |
| 无旧 Relay 回退时点「协议信封比对」 | 显示「这项比对只针对旧 Relay（回退）…」，不再向无人服务的信箱写请求 |
| 长按 →「旧 Relay（回退）」，再短按「开启中继」 | 旧 `relay.start` ok，影子 `automation.ui-tools` **Agree**；切回 C# 时先停旧 Relay（服务列表只剩 `RelayCsChannelService`），C# 重新写入脚本并登记 |

## 经 guest 信箱的真实请求（C# 执行层作答）

请求按 guest CLI 的方式写入 `files/pilot/run/inbox/<id>.part` 后改名为 `.json`：

| 请求 | 回包 |
|---|---|
| `ui.snapshot {}` | `E_GATE_SYSTEM_MISSING`（无障碍未开启，失败即关闭，无审批卡） |
| `ui.hack {}` | `E_NOT_IMPLEMENTED`（准入） |
| `sys.shell {"verb":"id"}` | `E_GATE_SYSTEM_MISSING`（无 Shizuku） |
| `app.launch {"package":"org.example.dshmobile"}` | `ok`，`alreadyInFront: true`（收尾回宿主，上游语义：不需审批，也不启动任何东西） |
| `pkg.query {"keyword":"settings"}` | 本页审批卡出现（窗口 FLAG_SECURE），**未作答**，60 s 后 `E_GATE_APPROVAL_TIMEOUT` |

## 发现并修复：页内审批卡到点不撤

修复前：`pkg.query` 超时 20 s 之后，页面窗口仍为 FLAG_SECURE，卡片仍在页顶（`dumpsys window` 中 secure 窗口数为 1）。上游页内卡 / 悬浮卡 / 通知卡
到点都经 `expireApproval` 关闭这一问，C# 页内卡的倒计时到 0 只停表。修复后：同一场景在 60 s 时回包，secure 窗口数变为 0，日志
`c# relay page card: pkg.query expired (request …)`。

## 未覆盖

经用户批准后的实际执行（节点写、坐标输入、截图、Direct / Shizuku 各能力）、经 DSH Agent 的端到端 `ui_*` 调用、guest 内自检的成功路径：需要真机和用户本人作答审批。
