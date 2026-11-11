# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · 进程监管观察 + 健康探测（PR-03 只读切片）真机记录

构建：Release（full trim，`-t:Rebuild` 干净构建）APK 125,502,301 B；健康探测为原生 socket 版（去掉 `HttpClient` 后比上一构建小 2.7 MB）。
操作：主界面「诊断」（停止态）→「启动 DSH」→「诊断」（运行态）→「停止」。每次点击前确认 DSH Mobile 在前台（脚本守护）。
C# 只读：自己读 `/proc` 找沙箱 / DSH 树、自己探测 `127.0.0.1:3080`，与旧实现的状态 / 健康读数比对；不启动、不杀进程。

```text
10:49:27.068 runtime coordinator: impl=legacy-bridge journal=Ready/Stopped gen=5
10:49:49.769 shadow runtime.supervisor-observe: Agree (sandbox: legacy=down csharp=down; dsh: legacy=down csharp=down; webUi: legacy=down csharp=down (SocketException); csharp 11 ms)
10:49:51.414 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 424 ms)
10:50:02.086 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=6
10:50:03.748 health: process=True port=True webUi=True lastError=-
10:50:03.754 shadow runtime.supervisor-observe: Agree (sandbox: legacy=up csharp=up (pid 29927, 3 procs); dsh: legacy=up csharp=up (pid 29945, 2 procs); webUi: legacy=up csharp=up (HTTP 401); csharp 4 ms)
10:50:05.388 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 419 ms)
10:50:07.728 stop: ok (Ready)
```

同一时刻 `ps -A`：`29927 29350 libproot.so`、`29945 29350 libproot.so`、`29960 29945 node`（与 C# 找到的根 PID 一致）；停止后 PRoot / node 进程 0；crash 缓冲区 0 条。
此前一轮（HttpClient 版探测，10:33–10:34）同样三态 `Agree`（PID 26644 / 26649，HTTP 401）。

运行中两个长命 PRoot 的完整 argv 已从 `/proc/<pid>/cmdline` 读出并存为 `tests/TestVectors/device-proot-cmdlines.json`（下一切片的黄金输入）。

## 过程中的问题

- 增量 Release 构建在**程序集集合变化**后（去掉 `System.Net.Http` 等）产出的 APK 启动即崩：`Invalid compressed assembly descriptor index 50`（libmonodroid abort）。
  `-t:Rebuild` 干净构建后正常。CI 总是干净构建，不受影响；本地增量构建需注意。
- 安装该包时 MagicOS 弹出系统安装确认，`adb install` 等待，由手机上人工确认后继续（未代点系统对话框）。
- 一次误发的 `input keyevent KEYCODE_MOVE_HOME`（光标键，非 Home）在 App 刚启动、位于前台时送达，无影响；其后所有输入均经前台守护。
