# 2026-11-16 vNext PR-00 基线记录

- 基线：`main` @ `7e87118e2daf7f7c53e0a1a16f408f04a7e45806`；CI `android-dotnet` run
  [43793423389](https://github.com/maiji1990/dsh_mobile/actions/runs/43793423389) success（windows-latest，.NET SDK
  `11.0.100-rc.2.26504.105`、workload set `11.0.100-rc.2.26513.2`）
- 本地环境：云端容器，Linux 6.18，4 vCPU；.NET SDK 10.0.115（Ubuntu 源；.NET 11 下载源被网络策略拦截）。测试在临时副本中把
  `net11.0` 重定向为 `net10.0` 运行，仓库未改；**这不是 net11.0 证据**，以 CI 为准
- **不是真机证据**：本记录只有桌面 xUnit 与 CI 日志

## CI（net11.0，run 43793423389）

| 套件 | 结果 |
|---|---|
| Core | Passed 35 / Failed 0 / Skipped 0 |
| Runtime | Passed 798 / Failed 0 / Skipped 1（`DshWebStartStop_AgainstWindowsOpenSsh`） |
| Application | Passed 112 / Failed 0 / Skipped 0 |
| guest | 37 passed, 0 failed |
| Android Release build | Build succeeded，75 Warning(s) / 0 Error(s)；含 `Runtime layers not staged … the APK will not contain android-side/DSH` |

## 本地（net10.0 重定向）

| 套件 | 结果 |
|---|---|
| Core | Passed 35 / Failed 0 |
| Runtime | Passed 797 / Failed 0 / Skipped 2（两个 Windows OpenSSH 测试：Linux 上没有该服务；CI 上只跳过需 `dsh` 的那一个） |
| Application | Passed 112 / Failed 0（连续 10 次） |
| guest | 37 passed, 0 failed |
| Android Release build | **未执行**：无 .NET 11 SDK / android workload / NDK 27 / Android SDK |

## PtyProbeTests 重复运行

原失败：run [43784510013](https://github.com/maiji1990/dsh_mobile/actions/runs/43784510013)，
`IgnoredCtrlC_FailsThatStep_AndTheSessionIsHungUp` → `InvalidOperationException: Sequence contains no matching element`（prompt
步骤 1.5 s 超时，探针提前返回）。修复：maiji1990/dsh_mobile#41。

| 运行方式 | 次数 | 结果 |
|---|---|---|
| 只跑 `PtyProbeTests`（7 例） | 30 | 30 / 30 通过（每次 2–3 s） |
| 整个 Application 套件（112 例，xUnit 类间并行） | 10 | 10 / 10 通过（每次 3–4 s） |
| 线程池饥饿复现（64 个阻塞工作项、`MinThreads=1`，再跑 `IgnoredCtrlC…`；#41 之前必失败） | 3 | 3 / 3 通过（每次约 1 m 3 s：时钟只在空闲时推进，饥饿下变慢而不误判） |

饥饿复现用的是临时测试文件（修改进程级线程池设置，会影响同进程其他测试，故未入库）。原始输出：

```text
10.0.115
probe-only 1: Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s - DshMobile.Application.Tests.dll (net10.0)
probe-only 30: Passed!  - Failed:     0, Passed:     7, Skipped:     0, Total:     7, Duration: 2 s - DshMobile.Application.Tests.dll (net10.0)
full-suite 1: Passed!  - Failed:     0, Passed:   112, Skipped:     0, Total:   112, Duration: 4 s - DshMobile.Application.Tests.dll (net10.0)
full-suite 10: Passed!  - Failed:     0, Passed:   112, Skipped:     0, Total:   112, Duration: 3 s - DshMobile.Application.Tests.dll (net10.0)
starved 1: Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 1 m 3 s - DshMobile.Application.Tests.dll (net10.0)
starved 2: Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 1 m 3 s - DshMobile.Application.Tests.dll (net10.0)
starved 3: Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 1 m 4 s - DshMobile.Application.Tests.dll (net10.0)
```
