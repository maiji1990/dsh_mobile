# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · 自建 PRoot / loader / talloc / shmem（ADR-0014）真机验收

设备：PTP-AN10，`11.0.0.117(SP2C00E117R102P1)`，页大小 4096。构建：Debug，`native/proot/build.ps1` 产物
（`artifacts/native/proot/SHA256SUMS`，arm64：`libproot.so` `6114fdc9…`、`libproot-loader.so` `95a47c09…`、`libtalloc.so` `9eec3822…`、
`libandroid-shmem.so` `6d579322…`）。运行时：已安装的 `runtime-current/base`（Debian 13.6、Node v24.19.0、DSH 0.2.1-rc.5）。
操作：App 自己的按钮（`adb shell input`），每次点击前确认本 App 在前台；App 自己的对话框由开发者流程点选，无系统弹窗。

## 1. shell 用户 A/B（不安装 App）

两套 arm64 二进制分别推到 `/data/local/tmp/ptest/{dshbox,ours}`，同一脚本在两套下运行，输出**逐字节相同**：

```text
## version      5.1.107.90
## id (-0)      uid=0(root) gid=0(root) …  context=u:r:shell:s0
## echo + exit  hello-42 / rc=3
## bind + cwd   /mnt  ·  dshbox ours ptest.sh
## fork/exec    aarch64
## tracer       TracerPid:
## link2symlink x
## sysvipc      sysvipc-ok
===== diff dshbox -> ours: IDENTICAL OUTPUT
```

测试后已删除 `/data/local/tmp/ptest`。

## 2. App 内（`-p:DshProotSource=self` 安装，15:26:30）

安装后 `nativeLibraryDir` 中四个文件的 SHA-256 与 `SHA256SUMS` 一致；`/proc/<pid>/exe` 指向同一 `lib/arm64/libproot.so`。

旧实现启动（安装后第一次）与诊断：

```text
15:27:58.826 dsh.start web (22.6 s): ok state=Running harness=Ready version=0.2.1-rc.5
15:28:08.493 shadow runtime.supervisor-observe: Agree (… pid 30912, 3 procs; … pid 30918, 3 procs; webUi … HTTP 401)
15:28:08.502 shadow runtime.launcher-argv: Agree (sandbox: identical (14 args); dsh: identical to first start (24 args))
15:28:10.785 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 705 ms)
```

终端（交互）：`uname -m; cat /etc/debian_version; node -v; grep TracerPid /proc/self/status; ls /proc/self/fd | wc -l`
→ `aarch64` / `13.6` / `v24.19.0` / `TracerPid: 31204`（= 本 App 的 `libproot.so`）/ `4`。打开终端后再诊断：

```text
15:30:14.998 shadow terminal.launcher-argv: Agree (1 session(s) identical (22 args))
15:30:17.006 shadow runtime.guest-exec: Agree (…; csharp 768 ms)
15:30:44.538 terminal.pty-probe: PASS: open ok; prompt ok; tty ok (pts/24x80/echo); resize ok (30x100); ctrl-c ok (status=130); exit ok; no-orphans ok
15:30:51.458 stop: ok (Ready)        → PRoot / node 进程 0
```

C# 宿主（长按「启动 DSH」→「C# 宿主」）：`host: started sandbox PRoot pid 31783` / `dsh PRoot pid 31788` → `dsh ready (HTTP 401)`，
停止后 `no marker process left`，进程 0。

## 3. 启动耗时 A/B（同一设备、同一时段，各 3 次启动 → READY → 停止）

| 路径 | DSHBox 二进制 | 自建二进制 |
|---|---|---|
| C# 宿主 ready | 17.9 / 18.2 / 18.3 s | 18.3（首次）/ 16.5 / 18.5 / 17.8 s |
| 旧实现 `dsh.start web` | 18.6 / 18.6 / 18.6 s | 22.6（安装后首次）/ 17.1 / 18.6 / 18.5 s |

无可见差异（本日两套都比 11-11 记录的 9–10 s 慢，属设备状态，与二进制无关）。每次停止后 PRoot / node 进程 0；
15:26 起 crash 缓冲区 0 条。

## 4. 默认切换

以上通过后 `DshProotSource` 默认改为 `self`，以默认值重新安装（15:38）：安装的 `libproot.so` / `libtalloc.so` 为自建哈希，
旧实现再启停 3 次（上表后三个数）全部 `ok`、进程 0。

## 未覆盖 / 限制

- x86_64 二进制只做了桌面自检与比对，未在模拟器上运行。
- guest 内 SysV 共享内存（`shmget` 经 libandroid-shmem）未在 App 内实测；shell A/B 只覆盖 `--sysvipc` 启动。
  libandroid-shmem 按 API 24 构建（与 DSHBox 版相同的 `/dev/ashmem` 路径），见 ADR-0014「后续」。
- WebUI 页面本身未在本轮打开（就绪判定为 HTTP 401，同既有记录）。
