# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · 远端 DSH 启停 / token / 转发切片影子（PR-09 第三段，ADR-0013）真机记录

设备：PTP-AN10，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，14:30:17 安装），基于 `5de560c` + 未提交的 PR-09 第三段改动。
主 App `org.example.dshmobile`。远端：本机 Windows OpenSSH（`sshd` 服务由用户本人启动），手机经 `adb reverse tcp:2222 tcp:22` 访问；
主机配置 `wangbc@127.0.0.1:2222`，远端 DSH 端口 3091，固定指纹 `SHA256:PcgR5e+89aos8tA49yT/IiD6ICGKtYCmsIsW9GaFR0c`（第一段之前确认）。
远端 `dsh` 为 npm 全局安装的上游 DeepSeek Harness（`dsh --version` = `0.2.1-rc.5`），本仓库未改动。

操作由用户本人完成，两轮：

1. 旧实现：短按「连接」（JSch）→「启动远端 DSH」→「停止远端 DSH」。
2. C# 实现：长按「连接」（SSH.NET）→「启动远端 DSH」→「停止远端 DSH」。

主机端只用 `run-as` 读 `files/dshmobile-runtime/shadow-reports.jsonl`（安装时 378 行，之后的为本次）、`logcat -d` 观察，
并在 PC 上用 `Get-NetTCPConnection` / `Get-Process` 核对远端端口与进程。

## 影子报告（安装后新增的 `remote.*` 行）

| 时间（UTC） | 切片 | 结论 | C# 耗时 | 详情 |
|---|---|---|---|---|
| 06:30:55 | `remote.hosts` | **Agree** | 0 ms | 进入页面：1 个配置一致 |
| 06:30:55 | `remote.identity` | **Agree** | 42 ms | `SHA256:QS7VgvjFSpaB0kpy9kS0JEVmO490pOFhItu6Meo31Lo`，`hardwareBacked=true`，Keystore 签名经 C# 转换后验签通过 |
| 06:31:00 | `remote.host-key` | **Agree** | 0 ms | 旧 connect `OK` ↔ C# 判定 `Ok`（已固定） |
| 06:31:03 | `remote.connect` | **Agree** | 2887 ms | C# 另开连接：`OK`，node `v26.13.0`，dsh `0.2.1-rc.5`，主机密钥 = 固定值 |
| 06:31:26 | `remote.dsh` | **Agree** | 1571 ms | 旧 `remote.start` 之后：C# 另开连接 + 自建转发到远端 3091，不带 token 请求得 HTTP 401，与旧实现的 401 / `OK` 一致 |
| 06:31:40 | `remote.dsh` | **Agree** | 10413 ms | 旧 `remote.stop` 之后：C# 自建转发得不到应答 → 「仍在运行」为 `false`，与旧实现的 `remoteDshStillRunning=false` 一致 |

## 应用日志（`DshMobile` 标签）

本 ROM 的 logcat 只留下了 C# 那一轮的两行（旧实现那一轮的 `remote.start:` / `remote.stop:` 与 `shadow remote.dsh:` 行在 `logcat -d -b all`
中已不存在）；旧实现的结论以上表影子报告为准。

```text
14:31:56.089 remote C# start: state=READY code=OK http=401 still=- token=True
14:32:12.696 remote C# stop: state=CONNECTED code=OK http=- still=False token=False
```

## PC 端核对（两轮结束后）

- `Get-NetTCPConnection -LocalPort 3091`：无监听、无连接。
- `Get-Process node`：没有 14:31 之后启动的 `node` 进程（两轮各自启动的远端 `dsh web` 都已退出）。

## 结论

- 旧实现启动远端 DSH 后，C# 影子**没有**启动第二个远端进程，只用自己的 SSH.NET 连接与 `ForwardedPortLocal` 访问同一远端端口，
  不带 token 得到 401，与旧实现判定相同（ADR-0013 决策 1–2 在真机成立）。
- 旧实现停止后，影子独立核查到远端 DSH 不再应答，与旧实现「仍在运行 = false」相同。
- C# 会话（长按连接）里完整走通：`dsh --profile web --port 3091` 由 SSH.NET `SshCommand` 启动，从输出读到 launch token
  （`token=True`，只在内存，日志不含 token），经 C# 本地转发不带 token 得 401 → `READY` / `OK`；停止后关闭 exec 通道与转发，
  核查远端不再应答（`still=False`），token 作废（`token=False`），会话保持 `CONNECTED`。
- 远端进程确实结束（PC 上端口释放），停止结果不是假成功。

## 其他观察

- 停止后的影子核查用了 10.4 s：远端端口关闭后，经 SSH 转发的本地连接被接受，但通道打不开，HTTP 请求要等到 10 s 超时才判为
  「无应答」。结论正确，只是慢；影子在后台运行，不阻塞界面。
- logcat 中另有一条 14:03:42 的崩溃（PID 15792，第二段真机测试期间，13:59:20 安装的构建）：
  `MissingMethodException: RemoteHostsShadow.get_ConnectEnabled()`。该成员在源码与两次构建中都存在，用户重进页面后同一构建即正常
  （第二段证据中的 06:03:45 / 06:04:03 两次进入），判断为 Debug 快速部署的程序集新旧混装；本次 14:30 安装后的进程（PID 17723）
  无崩溃。第二段记录当时漏记了这条，已补在第二段证据文件中。

## 未覆盖

- 真机未触发 `DSH_START_FAILED`（无 launch 行 / 非 401）、`FORWARD_FAILED`、启动超时、心跳断线后的 `connection lost`
  （由向量、源码契约与 Windows OpenSSH 桌面端到端覆盖）。
- 远程 WebUI 未打开（ADR-0006 决策 6：独立进程承载，属后续切片）。
- 远程页 Material 3 与默认实现翻转（PR-09 最后一段）。
