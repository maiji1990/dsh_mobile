# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · C# SSH 连接切片影子（PR-09 第二段，ADR-0012）真机记录

设备：PTP-AN10，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，13:59:20 安装），基于 `784e597` + 未提交的 PR-09 第二段改动。
主 App `org.example.dshmobile`。远端：本机 Windows OpenSSH（`sshd` 服务由用户本人启动），手机经 `adb reverse tcp:2222 tcp:22` 访问；
已有主机配置 `wangbc@127.0.0.1:2222`，固定指纹 `SHA256:PcgR5e+89aos8tA49yT/IiD6ICGKtYCmsIsW9GaFR0c`（第一段之前由旧实现确认）。

操作由用户本人完成：打开「远程主机」页，短按「连接」（旧 JSch 实现），再长按「连接」（C# SSH.NET 实现）。
主机端只用 `run-as` 读 `files/dshmobile-runtime/shadow-reports.jsonl`（安装时 368 行，之后的为本次）、`logcat -d` 观察。

## 影子报告（安装后新增的 `remote.*` 行）

| 时间（UTC） | 切片 | 结论 | C# 耗时 | 详情 |
|---|---|---|---|---|
| 06:03:29 / 06:03:45 / 06:04:03 | `remote.hosts` ×3 | **Agree** | 0 ms | 进入页面：1 个配置一致 |
| 06:03:29 / 06:03:45 / 06:04:03 | `remote.identity` ×3 | **Agree** | 48 / 44 / 20 ms | `SHA256:QS7VgvjFSpaB0kpy9kS0JEVmO490pOFhItu6Meo31Lo`，`hardwareBacked=true`，Keystore 签名经 C# 转换后验签通过 |
| 06:04:11 | `remote.host-key` | **Agree** | 0 ms | 旧 connect `OK` ↔ C# 判定 `Ok`（已固定） |
| 06:04:12 | `remote.connect` | **Agree** | 809 ms | C# 另开 SSH.NET 连接：`OK`，node `v26.13.0`，dsh `0.2.1-rc.5`，服务器出示的主机密钥 `SHA256:PcgR5e+…FR0c` = 固定值；结果码 / 指纹 / 探测与旧实现逐项相同 |

## 应用日志（`DshMobile` 标签）

```text
14:04:11.831 remote.connect: state=CONNECTED code=OK
14:04:11.833 shadow remote.host-key: Agree (after remote.connect: OK ↔ Ok (pin set))
14:04:12.647 shadow remote.connect: Agree (after remote.connect: OK, node v26.13.0, dsh 0.2.1-rc.5, host key SHA256:PcgR5e+89aos8tA49yT/IiD6ICGKtYCmsIsW9GaFR0c)
14:04:15.496 remote C# connect: state=CONNECTED code=OK
```

## 结论

- C# 经 SSH.NET + 自定义 `KeystoreHostAlgorithm` 在真机用**同一把**不可导出的 Keystore 密钥完成公钥认证（PC 上 `authorized_keys` 未改动），
  私钥未导出、未新建密钥。
- 同一台 Windows OpenSSH（有 ed25519 / ecdsa / rsa 三把主机密钥）对 C# 出示的主机密钥与 JSch 相同，固定指纹直接通过——
  ADR-0012 决策 4（主机密钥算法列表与 JSch 相同）在真机成立。
- 能力探测结果与旧实现逐字一致；影子连接（含探测、断开）0.8 s。
- 长按「连接」的 C# 连接：先断开旧连接，`CONNECTED` / `OK`。

## 未覆盖

- 真机上未触发 `HOST_KEY_UNKNOWN` / `HOST_KEY_CHANGED` / `AUTH_FAILED` / `UNREACHABLE`（由 Windows OpenSSH 桌面端到端测试 `RemoteSshOpenSshTests` 与向量覆盖）。
- C# 会话期间「启动远端 DSH」的不支持提示、心跳断线后的 `connection lost` 状态没有日志证据。
- 远端 `dsh web` 启停 / token / 转发 / 停止核查仍为 JSch（PR-09 第三段）。
- APK 体积：Debug 包看不出裁剪后的增量；BouncyCastle 的资源文件（如 `org/bouncycastle/pqc/crypto/picnic/*.properties`，约 1.3 MB）会进入 APK，Release 体积待记录。
