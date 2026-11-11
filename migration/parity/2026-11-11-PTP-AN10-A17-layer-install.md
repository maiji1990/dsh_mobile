# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · C# LayerInstaller 真机验收

构建：Release（full trim）APK 124,216,157 B（工作区，基于 `main@7a1951f` + 本切片）；覆盖安装保留数据。
操作：长按「导入运行环境包」→「C# 导入器」→ 系统选择器选 `dshapp-runtime-debian-arm64-0.1.0.zip`（设备侧 SHA-256 与 lock 一致）。
旧导入器**未运行**；导入前 runtime-current 为上一轮旧导入器安装的同一包。`adb logcat -s DshMobile:I` 原文：

```text
09:32:18.329 runtime coordinator: impl=legacy-bridge journal=Ready/Stopped gen=3
09:41:30.665 import: dshapp-runtime-debian-arm64-0.1.0.zip (192 MiB) ...
09:41:30.678 import: C# LayerInstaller (legacy importer not used)
09:41:36.403 csharp install: ok in 5723 ms (base=10324, node=5781, android-side=11)
09:41:41.332 verify runtime.layer-install: Agree (base: 10325 entries identical | node: 5782 entries identical | android-side: 12 entries identical; 4924 ms)
09:41:41.538 runtime.prepare: ok (no full layered runtime in APK (expected; base/node come from import); DSH kept at 0.2.1-rc.5)
09:41:41.542 import: took 10.9 s
09:41:41.542 import: ok
09:41:41.543 status: sandbox=STOPPED dsh=STOPPED version=0.2.1-rc.5 runtime=True node=True token=False shim=True free=280960 MiB
09:41:54.972 sandbox.start (4 ms): ok state=Running harness=Stopped version=0.2.1-rc.5
09:42:12.007 dsh.start web (17.0 s): ok state=Running harness=Ready version=0.2.1-rc.5
09:42:12.010 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=4
09:42:13.917 health: process=True port=True webUi=True lastError=-
09:42:15.444 diag arch: aarch64
09:42:15.445 diag debian: 13.6
09:42:15.445 diag node: v24.19.0
09:42:15.445 diag dsh: 0.2.1-rc.5
09:42:17.777 stop: ok (Ready)
(proot|node 进程 0)
```

## 结论

- C# 安装的层被旧运行时直接接受（`runtime=True`：profile + 每层哨兵通过旧实现 `verifyLayersBroken`）。
- 装后树与同一 zip 的干跑逐项一致（16,119 项，含哨兵）；干跑本身已在前一轮真机证明与旧导入器结果一致。
- 在 C# 安装的运行环境上 PRoot → Debian → Node → DSH web 启动 `Ready`，健康检查全部通过，停止无孤儿。
- 未覆盖：首次安装（无既有 runtime）、低磁盘、导入中途被杀；`previous/` 内容（Release 不可 `run-as`）。
