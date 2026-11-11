# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · runtime 切片真机证据

构建：Release（full trim），`migration/pr02-layer-extract@8a672b1`，APK 124,130,141 B。实现：`legacy-bridge`（旧实现决策），
C# 影子：`runtime.bundle-verify`、`runtime.layer-extract`。App 为非 debuggable 构建，`shadow-reports.jsonl` 无法经 `run-as`
读取；以下为 `adb logcat -s DshMobile:I` 原文摘录（同一内容由 `IDiagnosticsLog` 与 `IShadowReportSink` 同时输出）。

## 启动与 bridge.info

```text
08:39:22.599 runtime coordinator: impl=legacy-bridge journal=NotInstalled/Stopped gen=0
08:40:15.102 bridge 0.1.0 protocol=1 kotlin=2.4.0
08:40:15.103 upstream dshbox=v1.5.1-0-g7c9d33c
08:40:15.103 device sdk=37 abis=[arm64-v8a] model=PTP-AN10
08:40:15.124 csharp runtime impl=legacy-bridge native=NATIVE_UNAVAILABLE journal=/data/user/0/org.example.dshmobile/files/dshmobile-runtime/runtime-journal.jsonl
08:40:30.510 status: sandbox=STOPPED dsh=STOPPED version=0.2.1-rc.5 runtime=True node=True token=False shim=True free=281349 MiB
08:40:32.039 diag arch: aarch64
08:40:32.040 diag debian: 13.6
08:40:32.040 diag node: v24.19.0
08:40:32.040 diag dsh: 0.2.1-rc.5
```

## 导入 + 影子比对

设备侧 `sha256sum`：`0cb608abe0c17931eec2a16945405d930eb8d14879ad065901765b4e45b9f68f`（= `runtime-bundle/runtime-assets.lock`）。

```text
08:41:55.230 import: dshapp-runtime-debian-arm64-0.1.0.zip (192 MiB) ...
08:41:58.884 shadow runtime.bundle-verify: Agree (both accepted; base=sha256 ok, node=sha256 ok, android-side=sha256 ok; csharp 415 ms)
08:41:58.884 shadow runtime.layer-extract: comparing C# dry-run extraction with the installed layers ...
08:42:04.469 shadow runtime.layer-extract: Agree (base: 10325 entries identical | node: 5782 entries identical | android-side: 12 entries identical; 5583 ms)
08:42:04.472 import: took 9.2 s
08:42:04.472 import: ok
08:42:04.473 status: sandbox=STOPPED dsh=STOPPED version=0.2.1-rc.5 runtime=True node=True token=False shim=True free=280959 MiB
```

## 启动 / 重复启动 / 停止（RuntimeCoordinator）

```text
08:42:22.286 sandbox.start (3 ms): ok state=Running harness=Stopped version=0.2.1-rc.5
08:42:43.326 dsh.start web (21.0 s): ok state=Running harness=Ready version=0.2.1-rc.5
08:42:43.329 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=1
08:43:00.410 start: already running (harness Ready)
08:43:05.378 dsh.stop: ok state=Running harness=Stopped version=0.2.1-rc.5
08:43:05.384 sandbox.stop: ok state=Ready harness=Stopped version=0.2.1-rc.5
08:43:05.385 stop: ok (Ready)
```

运行中 `ps`：两个 `libproot.so --rootfs=…/runtime-current/base …` + `node --expose-internals --import /opt/dshbox/link-shim.mjs …/bin.js`；
`HarnessForegroundService isForeground=true`。停止后：`proot|node` 进程 0，`HarnessForegroundService` 记录 0。

## 运行中强杀 → journal 恢复

```text
08:43:40.021 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=2
(am force-stop org.example.dshmobile；proot|node 进程 0)
08:43:47.323 runtime coordinator: impl=legacy-bridge journal=Failed/Stopped gen=2 last error RUNTIME_INTERRUPTED
08:44:26.173 start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5 gen=3
08:44:27.277 stop: ok (Ready)
(proot|node 进程 0)
```

## 结论

两段影子比对均为 `Agree`；协调器启停、重复启动、强杀后无孤儿与 journal 如实恢复均通过。
未覆盖：沙箱启动失败 / 停止失败的清理分支（仅单测）。
