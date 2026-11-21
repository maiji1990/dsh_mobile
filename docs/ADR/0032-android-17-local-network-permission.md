# ADR-0032：Android 17 本地网络保护与 `ACCESS_LOCAL_NETWORK`

- 状态：已接受（2026-11-21；真机证据见
  [2026-11-21 PTP-AN10 / A17 报告](../../migration/parity/2026-11-21-PTP-AN10-A17-local-network-permission.md)）
- 关联：[Android Local network permission](https://developer.android.com/privacy-and-security/local-network-permission)、
  [AGENTS.md](../../AGENTS.md) 规则 5 / 9 / 11、[COMPATIBILITY.md](../COMPATIBILITY.md)

## 背景

Android 17（API 37）起，**targetSdk ≥ 37 的应用默认不能访问本地网络**：到私有地址（`10/8`、`172.16/12`、
`192.168/16`、`169.254/16`、IPv6 ULA / link-local）与 `.local` 名称的 TCP / UDP 流量，在**网络栈深处按 UID** 被拦截，
需要运行时权限 `android.permission.ACCESS_LOCAL_NETWORK`。未授权时 TCP 表现为**超时**（不是拒绝连接），UDP 为 `EPERM`。

本应用默认 targetSdk 37（workload 默认 API 37，见 [COMPATIBILITY.md](../COMPATIBILITY.md)），而 Manifest 只声明了
`INTERNET`。2026-11-21 在 Honor PTP-AN10 / Android 17 上用对照 App 实测（同一设备、同一 Wi-Fi，PC 侧监听 8080）：

| 条件 | LAN TCP | loopback | 公网 |
|---|---|---|---|
| targetSdk 37，无权限 | 超时，PC 侧无请求 | 通 | 通 |
| targetSdk 37，`pm grant ACCESS_LOCAL_NETWORK` | HTTP 200 | 通 | 通 |
| 撤销权限后 | 超时 | 通 | 通 |
| targetSdk 36（新装包） | HTTP 200（隐式授权） | 通 | 通 |

影响面：沙箱里 DSH 的 `curl` / `ssh` / Node socket 访问内网（NAS、开发机、局域网模型服务）、「远程主机」连内网主机、
局域网 MCP。DSH 的 Web UI（`127.0.0.1`）与公网（GitHub 更新、模型 API）不受影响。

关键点：PRoot 只是 ptrace，不改变内核 UID。guest 进程与 App 同为 `u0_aXXX`，所以**沙箱里的 shell 同样被拦**；
反过来，给 App 授权一次即覆盖全部 guest 进程。

## 决策

1. **Manifest 声明权限**，仅此一处：`android.permission.ACCESS_LOCAL_NETWORK`。不声明则 `pm grant` 也是空操作
   （实测返回 0 但 `dumpsys` 无变化），只能重新打包。
2. **在 C# 层申请**（规则 2 C#-first），实现放在 `DshMobile.Android`，判定纯函数放在
   `DshMobile.Application.LocalNetwork.LocalNetworkPolicy`（可单测）：
   - `sdkInt < 37` 或 `targetSdk < 37` → `NotRequired`（不申请，规则：不要在 target 36 及以下申请）；
   - 未声明 → `Undeclared`（构建缺陷，必须显式报错，不得静默超时）；
   - 已授予 → `Granted`；未授予 → `NeedsPermission`。
3. **触发点（用户动作处，不在冷启动弹窗）**：
   - 首页「启动 DSH」：若需要权限，先展示理由对话框；用户同意后走系统权限框，**无论结果都继续启动**（DSH 本身不依赖局域网）。
   - 「更多 → 手机操控 → 中继与权限 → 局域网访问」：常驻一行显示状态（已允许 / 未允许 / 无需），点击可再次申请，也可用于拒绝后的补救。
   - 「远程主机」连接：目标字面量是私有地址或 `.local` 时，先按同一流程提示；拒绝也继续连接（用户可能连的是公网主机）。
4. **不伪造成功、错误可见**（规则 5）：新增稳定错误码
   `LAN_PERMISSION_DENIED` / `LAN_PERMISSION_UNDECLARED`，拒绝时写操作日志并在界面提示「访问内网地址会超时」，
   不把超时伪装成连接成功或网络故障。
5. **不削弱安全**（规则 11）：不引入绕过（不走 shell UID 代理、不改 PRoot、不关闭任何权限门禁）；权限由用户本人在系统框里授予。
6. **不改明文策略**：`network_security_config.xml` 保持只放行 `127.0.0.1`。LNP 与明文策略是两层：guest 的 raw socket 不查明文策略，
   本次问题只由 LNP 引起。

## 不做 / 后续

- 不在 native 层接 `android_getnetworkblockedreason()` 区分「LNP 拦截」与「真超时」：需要新增 NDK 调用与错误通道，
  作为后续切片（有了它可以把超时文案做得更准）。
- 不做 `NsdManager` picker：本应用不需要「发现设备」，是直连用户明确给出的地址，走运行时权限更合适。
- Google Play 的 `ACCESS_LOCAL_NETWORK` 声明要求（2027-08-25 起）不适用：本项目不通过 Play 分发。

## 后果

- Android 17 上首次启动 DSH 会多一个系统权限框（只一次；组内已有蓝牙等权限时系统直接预授予）。
- 拒绝后：DSH 仍可用，内网访问超时；用户可在「更多 → 手机操控 → 中继与权限 → 局域网访问」补救。
- 沙箱、PRoot、DSH 内核零改动。
- `docs/COMPATIBILITY.md` 已补 Android 17 局域网访问一行（通过，证据为 App UID 的授权前后对照）。沙箱内 `curl` 的复验要等
  正式包重装（设备上的正式包签名与本机构建不同，覆盖安装需先卸载、会丢沙箱数据），在此之前按规则 9 不宣称沙箱内已通过。
- 排查时注意 Honor ROM 的行为：**安装后从未打开过的应用会被整体断网**（连 `127.0.0.1` 都超时），看起来像 LNP，实际不是；
  LNP 的判据是「loopback 与公网都通、只有 LAN 超时」。
