# 测试计划（TEST_PLAN）

完整用例见方案第 15 节。此处记录已实现的测试与 M1 验收步骤。

## 自动化（本机 / CI）

| 层 | 项目 | 覆盖 |
|---|---|---|
| 契约 | `tests/DshMobile.Core.Tests` | 请求/响应 JSON 形状与字段名、错误码常量与 Java 端一致、非法响应解析 |
| Native 单测 | `native/native-bridge/src/test`（JVM） | 协议解析、未知 method、重复 requestId、deadline、关闭后拒绝 |

## M1 验收（需真机，手动 + 日志）

1. `adb install` 后打开 App，首页显示 Bridge 版本与 `bridge.info`（含 `kotlinVersion`、`sdkInt`、`abis`）。
2. 点击 “DSHBox Kotlin 调用”：显示 `Versions.compare("0.1.1","0.1.1-rc.2") = 1`。
3. 点击 “1000 次压测”：完成后显示 `ok=1000 failed=0 inFlight(C#)=0 inFlight(Native)=0` 与耗时。
4. 压测进行中旋转屏幕、切后台再回来：压测继续完成、计数一致、无崩溃。
5. `adb shell am kill` 结束进程后重开：Facade 重新初始化，显示正常。
6. 重复第 3 步至少 10 次。以每轮日志 `stress: after GC managed=… java=… grefs=…`（强制双端 GC 后测量）判断泄漏：`grefs` 与 `java` 不应随轮次持续增长。`dumpsys meminfo` 未 GC 的数值只作参考。
7. 在 Android 10 与 Android 16/17 至少各一台执行，结果写入 `docs/COMPATIBILITY.md` 真机矩阵。

## M2 验收（需真机）

前置：`pwsh runtime-bundle/fetch-runtime.ps1`；`adb push artifacts/runtime/downloads/dshapp-runtime-debian-arm64-0.1.0.zip /sdcard/Android/data/<applicationId>/files/`。

1. 全新安装后点「准备运行环境」：提示 base/node 缺失。
2. 点「导入运行环境包」：`runtime=True node=True`，DSH 层已装配；记录耗时。
3. 点「启动 DSH」：sandbox RUNNING、dsh READY、`token=True`；记录耗时。
4. 点「诊断」：health 全 true；`node`/`dsh` 版本与 `runtime-assets.lock` 一致。
5. `adb shell ps -A | grep <app user>`：运行中有 `libproot.so` / `node`；HOME 后台 ≥ 90 s 仍在。
6. 点「停止」后再 `ps`：仅剩 App 进程。启动后 `am force-stop`：App 用户下零进程。
7. 待补：磁盘不足、层缺失（删除 `runtime-current/node`）、损坏的 zip、导入中断、强杀后再次启动。

## TASK-005 DSH 模式探测（需真机）

1. 运行环境已导入；点「启动 DSH」，状态行须 `shim=True`；`adb shell ps -A -o PID,NAME,ARGS | grep node` 须含 `--import /opt/dshbox/link-shim.mjs`。
2. 点「DSH 模式探测」，等待 `probe summary`。期望：web/sdk/sdk-minimal/acp = pass，headless = limited（无凭据），plugin 视 pnpm 供给情况。
3. 每个 `FAIL` 检查行带退出码与 stderr 尾部，写入 COMPATIBILITY.md。
4. 待补（需模型凭据）：headless 一次性任务、sdk `session/prompt`、acp `session/new`+`session/prompt`、子代理、工具调用与审批。

## TASK-006 WebUI（需真机）

1. 启动 DSH 后点「打开 DSH」：状态 `WAITING_TOKEN → LOADING → LOADED`，显示官方 WebUI 首页。
2. `adb logcat --pid <app>` 中 `token=` 只允许 `***`（DSHBox 脱敏）与 `BinderProxy`，其它任何出现都算失败。
3. 返回键：无历史时关闭页面并 `DETACHED`。
4. 停止 → 启动 DSH（新 token）→ 重开：仍 `LOADED`。
5. 待补：页面打开时重启 DSH（就地重载）、旋转、外链、软键盘、文件选择与下载（TASK-007）、401 恢复。

## TASK-007 文件 / SAF（需真机；选择器步骤由人手动）

1. 首页「文件」：列出 `/root/projects`；`.dsh` 带 🔒。进入 `.dsh`，点 `.credentials.yaml` 必须被拒绝（无预览/导出）。
2. 「导入」→ 选择器选文件 → 状态「已导入文件 N: …」，列表出现该文件。
3. 点该文件 →「导出到…」→ 选择位置 → 状态「已导出 …」；目标文件大小一致。
4. WebUI 附件：先在 DSH 中选定工作区，再点输入框「+」→ 选择器选文件 → 附件出现在输入框（不发送）；日志 `webui file chooser: N file(s)`。
5. 首页「导入运行环境包」→ 选择器选 zip → 日志 copy / import 耗时与 `runtime=True node=True`。
6. 单元测试覆盖：路径规范化、符号链接（含悬空）拒绝、受保护目录、不覆盖、原子写入、文件名净化（`WorkspaceFileSystemTests`）。

## TASK-008 终端（需真机）

注意：若设备使用中文等组合式输入法，`adb shell input text/keyevent` 注入会被输入法拦截，命令需人工输入。

1. 首页「终端」：出现会话标签「终端 1」与提示符。
2. 输入 `whoami; id; uname -m; node --version`：分别为 root / uid=0 / aarch64 / 与 runtime-assets.lock 对应的 Node 版本。
3. `sleep 30` 后点键栏 ^C：应立即返回提示符并回显 `^C`。
4. 返回首页再进入终端：同一会话与输出仍在。
5. 「+」两次 → 两个标签；点标签切换；「×」关闭活动会话；`adb shell ps -A` 中对应 `libproot.so/bash` 消失。
6. 关闭最后一个会话：显示「没有打开的会话」提示；「+」后恢复终端。
7. 首页「停止」：App 用户下沙箱进程为 0。
8. 待补：中文/emoji/宽字符、大量输出滚动、横屏与分屏、软键盘遮挡、粘贴大段文本。
