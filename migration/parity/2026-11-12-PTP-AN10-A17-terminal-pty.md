# 2026-11-12 · Honor PTP-AN10 · Android 17 (API 37) · C# PTY 后端 + 终端 argv 影子（PR-06 第一段）真机记录

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`，页大小 4096。构建：Release（`-t:Rebuild`）APK，基于 `ad9bf1f` + 未提交的
PR-06 改动，安装 08:37:17。运行时：已安装的 `runtime-current/base`（旧实现导入），沙箱 / DSH 均未启动（`UNINITIALIZED`）。
操作：主界面长按「诊断」→「开始自检」；每次点击前确认 DSH Mobile 在前台。Release 不可 `run-as`，证据为 `adb logcat -s DshMobile:I`
与 `adb shell ps -A`（可见本 App uid 的进程，见下）。

## 1. 原生库

```text
08:27:03.468 native: libdshmobile_native ABI 1
```

APK 内 `lib/arm64-v8a/libdshmobile_native.so`（12,160 B）、`lib/x86_64/…`（11,808 B）；构建脚本已检查 LOAD 段 16 KB 对齐与 8 个导出。

## 2. 第一次运行：resize 判定失败（探针缺陷，非 PTY 缺陷）

```text
08:27:15.631 pty open: ok (pid 24030, 44 ms)
08:27:15.631 pty prompt: ok (prompt seen, 36 ms)
08:27:15.631 pty tty: ok (pts=True size24x80=True echo=True, 18 ms)
08:27:15.631 pty resize: FAILED (got: ^[[K^Mroot@localhost:~# stty size^M
^[[?2004l^M30 100^M
^[[?2004hroot@localhost:~#, 10005 ms)
08:27:15.631 pty ctrl-c: ok (prompt back after 1 ms, status=130, 1502 ms)
08:27:15.631 pty exit: ok (exit 0, 47 ms)
08:27:15.631 pty no-orphans: ok (2 process(es) gone, 500 ms)
```

guest 实际输出了 `30 100`（窗口大小已生效），但设备上的 bash 开启了 bracketed paste：换行后紧跟 `ESC[?2004l`，原始字节里没有
`"\n30 100"`。修正：`PtyProbe.Plain` 先去掉转义序列与 CR 再匹配，失败详情改为单行（logcat 会拆分多行消息）；假 shell 改为与真机
一致（bracketed paste + SIGWINCH 重绘提示符），并把上面这段原样输出写成回归单测 `Plain_RemovesBracketedPasteAndCsi_SoDeviceOutputMatches`。

## 3. 修正后连续 3 次：PASS

```text
08:37:48 terminal.pty-probe: PASS
run 2: terminal.pty-probe: PASS: open ok (pid 25051, 36 ms); prompt ok (prompt seen, 19 ms); tty ok (pts=True size24x80=True echo=True, 15 ms); resize ok (30x100 seen, 5 ms); ctrl-c ok (prompt back after 2 ms, status=130, 1504 ms); exit ok (exit 0, 56 ms); no-orphans ok (2 process(es) gone, 498 ms)
run 3: terminal.pty-probe: PASS: open ok (pid 25224, 38 ms); prompt ok (prompt seen, 21 ms); tty ok (pts=True size24x80=True echo=True, 14 ms); resize ok (30x100 seen, 8 ms); ctrl-c ok (prompt back after 2 ms, status=130, 1504 ms); exit ok (exit 0, 48 ms); no-orphans ok (2 process(es) gone, 499 ms)
```

即：C# 经 `dshm_pty_spawn` 起的 PRoot 登录 shell 拿到真实控制终端（`/dev/pts/N`，24×80）；`TIOCSWINSZ` 调整到 30×100 后 guest
`stty size` 可见；写入 0x03 由行规程转成 SIGINT，前台 `sleep 30` 被中断（`$?`=130）且 shell 存活；`exit` → 退出码 0；PRoot 与
bash 两个进程均已消失。独立核对：每次运行后 `adb shell ps -A` 中无本 App 的 `libproot.so` / `bash`（同一命令能列出
`org.example.dshmobile` 本身，可见性成立）。

## 4. 终端 argv 影子（旧 TerminalView 会话 vs C# `TerminalCommandBuilder`）

打开旧终端（「终端」按钮）→ 会话 1 运行（`libproot.so` 25422 → `bash` 25437）→ 返回主界面（会话保留）→ 点「诊断」：

```text
08:41:44.015 shadow runtime.supervisor-observe: Agree (sandbox: legacy=down csharp=down; dsh: legacy=down csharp=down; webUi: legacy=down csharp=down (SocketException); csharp 10 ms)
08:41:44.019 shadow terminal.launcher-argv: Agree (1 session(s) identical (22 args); csharp 0 ms)
```

旧会话 argv（`ps -o ARGS`，22 个）：`libproot.so --kill-on-exit -0 --rootfs=…/runtime-current/base --bind=/system --bind=/apex
--bind=/proc --bind=/dev --bind=…/node:/usr/local --bind=…/dsh:/opt/dshapp/runtime --bind=…/files/dshbox:/opt/dshbox
--bind=…/guest-cache:/opt/dshbox-cache --bind=…/pilot:/opt/pilot --bind=…/browser-tools:/opt/browser`、3 个 `.procfake` 绑定、
`--bind=…/user-data:/root/projects --cwd=/root /usr/bin/bash --noprofile --login`。本机无 `/linkerconfig` 绑定（两边一致）。

结论：C# PTY 后端在真机上完成提示符 / 控制终端 / 窗口大小 / ^C / 退出 / 无残留全流程；C# 终端 argv 与旧终端逐 token 一致。
局限：只测了 1 个旧会话（未测多会话影子）；终端界面仍为旧 TerminalView（xterm.js 未开始）；未测 Android 10 / 16 KB 页设备
（本机 4 KB）；未测 App 被杀时 PTY 子进程的回收（旧实现靠 `--kill-on-exit` + 会话挂断）；环境变量未与旧会话 `/proc/<pid>/environ`
对照（Release 无法读取，只有向量双跑证据）。
