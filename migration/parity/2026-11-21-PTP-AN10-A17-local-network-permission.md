# Android 17 局域网权限（LNP）· 真机报告 · 2026-11-21

- 设备：Honor PTP-AN10（Android 17 / API 37，arm64-v8a），Wi-Fi `192.168.0.130/24`；对端为同一局域网的 PC `192.168.0.181:8080`（`python3 -m http.server`）。
- 依据：[ADR-0032](../ADR/0032-android-17-local-network-permission.md)。
- 被测构建：本仓库 Debug 构建（`net11.0-android`，compileSdk/targetSdk 37），为与已安装的正式包共存，构建时用
  `-p:ApplicationId=io.github.maiji1990.dshmobile.lanprobe -p:EmbedAssembliesIntoApk=true`；除 ApplicationId 外与正式包的
  Manifest / C# 代码完全相同（`ACCESS_LOCAL_NETWORK` 已声明、targetSdk 37、debuggable 便于 `run-as`）。
- 方法：`adb shell run-as <pkg> ... toybox nc -w 6 192.168.0.181 8080`（在 **App 自己的 UID** 里发 TCP），对照 PC 侧访问日志。

## 一、对照实验（先证明机制，用的是独立探针 APK）

同一设备、同一 Wi-Fi、同一 target/权限组合，只在中间改授权位：

| 条件 | LAN TCP | loopback `127.0.0.1:3080` | 公网 `223.5.5.5:443` |
|---|---|---|---|
| targetSdk 37，未声明权限 | 超时，PC 侧零请求 | 通 | 通 |
| targetSdk 37，声明并 `pm grant ACCESS_LOCAL_NETWORK` | `HTTP/1.0 200` | 通 | 通 |
| 同一进程撤销权限后 | 超时 | 通 | 通 |
| targetSdk 36（全新包名安装） | `HTTP/1.0 200` | 通 | 通 |

> 注意：探针第一次测时全超时（连 loopback 也不通），原因是 Honor ROM 对「安装后从未打开过」的应用掐掉网络。
> 把 App 拉到前台后 loopback / 公网立即恢复、只剩 LAN 超时——**LNP 的干净信号是「全网通、只有 LAN 超时」**。

## 二、本仓库构建（ADR-0032 改动后）

| 步骤 | 操作 | 结果 |
|---|---|---|
| 1 | 安装 `lanprobe` 构建，`dumpsys package` | `targetSdk=37`，`ACCESS_LOCAL_NETWORK` 已声明（`granted=false`） |
| 2 | App UID（`u0_a496`）→ LAN TCP | `nc: Timeout`，PC 侧无请求 |
| 3 | `pm grant ... ACCESS_LOCAL_NETWORK` → 同一条命令 | `HTTP/1.0 200`，PC 日志 `192.168.0.130 - "GET / HTTP/1.0" 200` |
| 4 | 撤销权限 → 「手机操控 → 中继与权限」里的行文案 | `未允许：沙箱里访问内网地址（192.168.x 等）会超时；点此允许` |
| 5 | 点该行 → 理由对话框 | 标题「允许局域网访问？」，含「允许 / 暂不」，文案完整 |
| 6 | 点「允许」（ROM 未弹系统框，直接置 `USER_SET`）→ 该行文案 | `已允许：DSH 及其沙箱可以访问手机所在局域网的设备` |
| 7 | 授权后再从 App UID 连 LAN | `HTTP/1.0 200` |

## 三、没有覆盖到的

- `lanprobe` 构建里**没有安装运行层 / DSH**，因此没有在 App 内真正跑「沙箱 curl」；但沙箱进程与 App 同 UID（见下），
  UID 级授权对 guest 同样生效。沙箱内 `curl` 的复验要等正式包重装后再做一次。
- 正式包（`io.github.maiji1990.dshmobile`）的签名与本机构建不同（设备上为 `9fe685…`，本机构建为 `37d6bb…`），
  覆盖安装需要先卸载、会丢沙箱数据，本次没有动它。
- Honor ROM 的「未打开过的应用断网」会让 LNP 的排查出现假象，测试时必须让 App 处于前台。

## 四、可见进程

```
u0_a214  22874  io.github.maiji1990.dshmobile        ← 正式包主进程
u0_a214  20348  libproot.so                          ← PRoot，父进程即 App（同 UID，故 LNP 同样适用）
u0_a496  20252  io.github.maiji1990.dshmobile.lanprobe ← 本次被测构建
```
