# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · 里程碑：C# 宿主启动 → WebUI → 停止（无 DSHBox 代码）

构建：Release（`-t:Rebuild`）APK 126,051,165 B，安装于 14:30:55（无系统确认弹窗）。冷启动 289 / 261 / 254 ms。
入口：长按「启动 DSH」→ 确认对话框「C# 宿主」（开发者逐次选择；默认启动仍走旧实现）。每次点击前确认本 App 在前台。

迁移方案第 12 节的第一项业务结论——**C# 不调用 `DshNativeFacade` / `RuntimeController.kt` / DSHBox `SandboxManager`，
在 Android 17 真机完成「导入 Debian → 启动 PRoot → dsh web READY → WebUI → 停止无孤儿」**——本记录覆盖其中
「启动 PRoot → READY → WebUI → 停止」；「导入」已由 C# LayerInstaller 在同一设备通过（`2026-11-11-PTP-AN10-A17-layer-install.md`）。

## C# 宿主启动 → READY

```text
14:31:56.565 start: C# DshHost (legacy runtime not used)
14:31:56.628 host: started sandbox PRoot pid 31912
14:31:56.990 host: started dsh PRoot pid 31919
14:32:06.028 host: dsh web launch token refreshed
14:32:06.659 host: dsh ready (HTTP 401) in 9711 ms
14:32:06.664 csharp host: ready in 10.1 s
14:32:06.667 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=8
```

`ps -A`：`31912 31709 libproot.so`、`31919 31709 libproot.so`、`31922 31919 node`（父进程 31709 = App）。旧实现同设备 DSH READY 用时 17–21 s。

## token 不外泄

全部 logcat（所有 tag）中 `token=` 共 6 处：唯一的 DSH 行已脱敏（`proc-dsh: dsh web: http://127.0.0.1:3080/?token=***`）；
其余 5 处为 Android 系统自身的 binder 句柄（`token=android.os.BinderProxy@…`、`WCT{…}`、`RemoteToken{…}`），非 DSH launch token。
token 只存在于 C# 内存与交给 `WebView.LoadUrl` 的 URL 中。

## C# WebView 承载官方 WebUI

```text
webui: C# WebView on the C#-hosted DSH
webui(csharp) LOADING /
webui(csharp) LOADED /
```

截图（本地，未入库）显示官方 DSH WebUI 已登录的首页（“探索未至之境”、工作区 / 模式选择、侧栏），无 401。

## 诊断与停止

```text
14:37:42.784 status: sandbox=STOPPED dsh=STOPPED …            ← 旧实现不知道 C# 托管的进程（预期）
14:37:42.789 health: process=True port=True webUi=True         ← 旧实现的端口探测看到了 C# 托管的 DSH
14:37:42.789 shadow runtime.supervisor-observe: skipped (the C# host owns the processes; legacy reports them as stopped)
14:37:44.474 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 461 ms)
14:37:46.429 csharp host: stopped, no marker process left
14:37:46.431 stop: ok (Ready)
```

停止后 PRoot / node 进程 0，crash 缓冲区 0 条。

## 运行中强杀

```text
14:39:00.296 csharp host: ready in 9.4 s
(运行中进程 3 个；am force-stop；之后 PRoot / node 进程 0)
14:39:05.362 runtime coordinator: impl=legacy-bridge journal=Failed/Stopped gen=10 last error RUNTIME_INTERRUPTED
```

## 默认路径回归

使用过 C# 宿主后，默认（旧实现）启动：DSH web 9.0 s Ready，停止 `ok`，进程 0，崩溃 0。

## 未覆盖 / 限制

- C# 宿主运行期间，终端、手机操控、浏览器等仍依赖旧运行时状态的功能不可用（对话框已提示）。
- 健康循环的就地重启、预算用尽分支只在单测层面覆盖，真机未注入 DSH 故障。
- WebUI 内的文件上传、外链、401 恢复未在本轮操作；下载仍不支持。
