# 2026-11-11 · Honor PTP-AN10 · Android 17 (API 37) · C# PRoot guest exec（PR-03 首段）真机记录

构建：Release（full trim）APK 125,195,101 B（工作区，基于 `main` + PR #5 + 本切片）；覆盖安装保留数据，冷启动 191 ms。
操作：主界面「诊断」。旧实现 `runtime.diagnostics` 先运行并决策显示；随后 C# `ProotGuestProcess` 以 .NET `Process`
直接从 nativeLibraryDir 启动 `libproot.so`（不经任何 DSHBox 代码）运行同样 4 条固定命令并比对。`adb logcat -s DshMobile:I` 原文：

```text
10:01:20.351 status: sandbox=STOPPED dsh=STOPPED version=0.2.1-rc.5 runtime=True node=True token=False shim=True free=281227 MiB
10:01:21.573 diag arch: aarch64
10:01:21.574 diag debian: 13.6
10:01:21.574 diag node: v24.19.0
10:01:21.574 diag dsh: 0.2.1-rc.5
10:01:21.575 csharp guest argv: /data/app/~~N9DpSLJcKhNTuJU637Ktog==/org.example.dshmobile-3tp-f2wmjNXPGU4WvwXamA==/lib/arm64/libproot.so --rootfs=/data/user/0/org.example.dshmobile/files/runtime/runtime-current/base --bind=/system --bind=/apex --bind=/proc --bind=/dev --bind=/data/user/0/org.example.dshmobile/files/runtime/runtime-current/node:/usr/local --bind=/data/user/0/org.example.dshmobile/files/runtime/runtime-current/dsh:/opt/dshapp/runtime --bind=/data/user/0/org.example.dshmobile/files/dshbox:/opt/dshbox --bind=/data/user/0/org.example.dshmobile/files/guest-cache:/opt/dshbox-cache --bind=/data/user/0/org.example.dshmobile/files/user-data:/root/projects --bind=/data/user/0/org.example.dshmobile/cache/npm-cache:/root/.npm --bind=/data/user/0/org.example.dshmobile/files/pilot:/opt/pilot --bind=/data/user/0/org.example.dshmobile/files/browser-tools:/opt/browser --cwd=/root --kill-on-exit /usr/bin/bash -c uname -m
10:01:22.068 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 488 ms)
10:02:59.756 shadow runtime.guest-exec: Agree (arch=aarch64; debian=13.6; node=v24.19.0; dsh=0.2.1-rc.5; csharp 450 ms)
```

## 结论与限制

- 两次运行均 `Agree`：C# 启动的 PRoot 在真机得到与旧实现完全相同的四项输出，用时约 0.45–0.49 s（旧实现约 1.2 s）。
- argv 与旧实现**逐字节**对照**未完成**：为抓旧实现短命进程的 `/proc/<pid>/cmdline`，在设备上用 `nohup` 跑了 `pidof libproot.so` 轮询脚本，两次都抓到 0 行（原因未查明）。
  argv 的一致性目前由代码移植 + 桌面结构测试 + 输出一致性间接支撑，不是直接证据。
- 第三步（DSH 运行中抓长命 PRoot 的 cmdline）未完成：发现手机前台已切到其它应用（有人在使用手机），一次点击落在了那个应用的启动页上；
  随即停止一切屏幕输入。设备上的临时脚本 / 抓取文件已删除，无 PRoot / node 残留进程。
