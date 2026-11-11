# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · 沙箱 / DSH 启动 argv（PR-03 只读切片）真机记录

构建：Release（`-t:Rebuild`）APK 125,518,685 B；安装由手机上人工确认系统安装对话框后完成（11:08:22）。冷启动 284 ms。
操作：「启动 DSH」→「诊断」→「停止」（每次点击前确认 DSH Mobile 在前台）。C# 用 `ProotCommandBuilder` 构建它会用的
沙箱 keepalive 与 DSH web 启动 argv，与旧实现实际启动的进程的 `/proc/<pid>/cmdline` 逐 token 比对；只读，不启动 / 不杀进程。

```text
11:08:56.749 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=7
11:08:58.349 shadow runtime.supervisor-observe: Agree (sandbox: legacy=up csharp=up (pid 5549, 3 procs); dsh: legacy=up csharp=up (pid 5554, 2 procs); webUi: legacy=up csharp=up (HTTP 401); csharp 16 ms)
11:08:58.353 shadow runtime.launcher-argv: Agree (sandbox: identical (14 args); dsh: identical to first start (22 args); csharp 3 ms)
11:09:00.012 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 442 ms)
11:09:02.137 stop: ok (Ready)
```

停止后 PRoot / node 进程 0，crash 缓冲区 0 条。

结论：C# 构建的两条长命 PRoot 启动命令与旧实现在真机上实际使用的 argv **逐字节一致**；这同时补上了前一切片中短命 guest 命令
未能抓到 `/proc` 对照的缺口中“长命进程”这一半（短命命令仍只有输出一致性证据）。
