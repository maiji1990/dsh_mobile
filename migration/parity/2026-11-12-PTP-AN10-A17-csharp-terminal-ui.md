# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · C# 终端界面（PR-06 第二段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`。构建：Release（`-t:Rebuild`）APK，基于 `1eff849` + 未提交的 PR-06 第二段改动。
入口：主界面长按「终端」→「终端实现」对话框 →「C# 终端」（`CSharpTerminalActivity`）；短按仍打开旧 TerminalView（默认不变）。
运行时：已安装的 `runtime-current/base`，沙箱 / DSH 未启动——C# 终端自己用 `ProotGuestProcess.OpenPtyAsync` 起 PRoot bash。
证据：`adb logcat -s DshMobile:I`、`adb shell ps -A -o PID,PPID,NAME`、截图（输入经 `adb shell input text/keyevent`）。

## 1. 第一次运行：xterm.js 自带输入在 Android IME 下错字（已改）

xterm.js 用隐藏 textarea 接收输入；在本机输入法下字符被吞或重复：键入 `tty` 只到达 `t`，`stty size` 到达为
`sttttty s ssizz`。显示本身正常（提示符、输出、颜色）。

处理：WebView 只负责显示与触摸，键盘改由原生 `TerminalInputView`（`InputType = TYPE_NULL`，IME 只发按键或纯提交，
无自动纠正；组合输入在提交时整段发送；删除转 DEL 字节）——与旧 TerminalView（Termux）同一做法，代码为本仓库编写。

## 2. 第二次运行（TerminalInputView）

| 检查 | 结果 |
|---|---|
| 首个会话 | 自动新建「终端 1」，提示符出现；`terminal.csharp: session 1 (终端 1) started, pid …` |
| `tty` | `/dev/pts/0` |
| 窗口大小 | 弹出键盘后 `stty size` → `15 41`（页面 fit → `resize` → `TIOCSWINSZ`） |
| 普通命令 | `ls /` 列出 guest 根目录；`echo DSHM-$((6*7))` → `DSHM-42` |
| CTRL 锁存 | 点 CTRL 再键入 `c` → `^C`，`echo $?` → `130` |
| 第二会话 | 「新建」→「终端 2」，`tty` → `/dev/pts/1`；`ps` 见两组 `libproot.so` → `bash`，父进程均为 App |
| 标签切换 | 切回「终端 1」重放其历史输出，之后实时输出继续 |
| 离开再回来 | 返回主界面再进入：两个标签与各自历史仍在（会话由 `RuntimeCoordinator.Terminals` 持有，不随 Activity 销毁） |
| 大量输出 | `time seq 1 100000` → 输出到 100000，`real 0m0.085s`；随后 `echo AFTER-FLOOD` 正常回显 |
| 方向键 | `DPAD_UP` 调出上一条命令 `echo AFTER-FLOOD` |
| 影子比对 | C# 会话开着时运行「诊断」：`terminal.launcher-argv` **Inconclusive**（"no legacy terminal session open"——C# 会话的 PID 已从旧会话候选中排除，未被误当旧会话比对）；`supervisor-observe` **Agree** |

截图（会话外不保留）：提示符 / `tty` / resize / ^C / 双会话 / 重放 / 大量输出 / 历史调出各一张。

## 3. 关闭与回收

```text
09:28:52.675 terminal.csharp: view attached to session 2
09:28:54.757 terminal.csharp: session 1 closed (shell signal 9)
09:28:58.164 terminal.csharp: session 2 closed (shell signal 9)
```

- 工具栏「关闭」两次：两个会话移除，`ps` 中无 `proot` / `bash`；最后一个关闭后显示「新建会话」按钮。
- 记录的退出信号是 9：PRoot 在 SIGHUP 宽限期内没有退出，`ProotPtySession` 按设计升级为 SIGKILL（旧 Termux 会话关闭直接发
  SIGKILL）。结果无残留；单个关闭约 2 s 内完成。
- 「新建会话」再起一个（`libproot.so` 32514 → `bash` 32516），然后 `am force-stop`：App、`proot`、`bash` 全部消失（无孤儿）。

## 4. 首页「停止」：第一次失败，修正后复测通过

沙箱未启动、C# 终端开着 2 个会话时点首页「停止」：

```text
09:38:11.494 stop: nothing running
09:38:11.494 stop: ok (Ready)
```

`ps` 仍见 2 组 `libproot.so` → `bash`（1330/1334、1564/1618）。原因：`RuntimeCoordinator.StopAsync` 在运行时状态为
`Ready` 时直接返回“nothing running”，跳过了关闭 C# 终端；而 C# 终端不依赖已启动的沙箱，正好可能只在这个状态下运行。
修正：该分支同样先 `CloseTerminalsAsync()`；新增单测 `CoordinatorStop_WithoutAStartedSandbox_StillClosesCSharpSessions`
（去掉修正时失败，加上后通过）。

复测（修正后 APK，安装 09:54:26）：同样沙箱未启动、C# 终端 2 个会话（5992 → 5995、6069 → 6073），点「停止」：

```text
09:55:27.017 terminal.csharp: session 2 closed (shell signal 9)
09:55:27.037 terminal.csharp: session 1 closed (shell signal 9)
09:55:27.038 terminal.csharp: closed 2 session(s) on stop
09:55:27.040 stop: nothing running
09:55:27.040 stop: ok (Ready)
```

之后 `ps` 中无 `proot` / `bash`。**通过。**

## 5. 未覆盖

- 中文 / emoji / 组合输入（adb `input text` 无法模拟 IME 组合；需人工在真机键入）。
- 应用光标模式程序（`vim` / `less`）中的方向键（DECCKM 切换路径只有单测）。
- 横屏、外接键盘、长时间后台（无前台服务时 App 被杀会连带结束 C# 会话，与 force-stop 结果一致）。
- 沙箱运行中点「停止」时的顺序（先关 C# 会话再停沙箱）只有单测。
