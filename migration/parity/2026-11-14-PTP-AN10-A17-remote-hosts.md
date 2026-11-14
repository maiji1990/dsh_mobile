# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · 远程主机切片影子（PR-09 第一段）真机记录

设备：PTP-AN10，`AN6B024B01054420`。构建：Debug（`dotnet build -t:Install`，13:23 安装），基于 `daa9720` + 未提交的 PR-09 第一段改动。
主 App `org.example.dshmobile`（uid 10267）。安装前本 App 的无障碍服务本来就未开启（`enabled_accessibility_services` 只有第三方服务）。

操作由用户本人完成：打开「远程主机」页（进入两次），点一次「保存」（已有的一台主机，已固定指纹）。本次没有点「连接」（没有开 SSH 服务器），
因此没有 `remote.host-key` 报告。主机端只用 `run-as` 读 `files/dshmobile-runtime/shadow-reports.jsonl`、`logcat -d` 观察。

## 影子报告（`slice` 以 `remote.` 开头的全部行）

| 时间（UTC） | 切片 | 结论 | C# 耗时 | 详情 |
|---|---|---|---|---|
| 05:23:57 | `remote.hosts` | **Agree** | 0 ms | `remote.hosts.list` 之后：1 个配置逐字段一致 |
| 05:23:57 | `remote.identity` | **Agree** | 45 ms | `SHA256:QS7VgvjFSpaB0kpy9kS0JEVmO490pOFhItu6Meo31Lo`，`hardwareBacked=true`，Keystore 签名经 C# DER→SSH 转换后验签通过 |
| 05:24:16 | `remote.hosts` | **Agree** | 0 ms | `remote.hosts.save` 之后：更新已有主机，同一地址 / 端口保留固定指纹；保存后的存储列表与 C# 预测一致 |
| 05:24:28 | `remote.hosts` | **Agree** | 0 ms | 第二次进入页面：1 个配置一致 |
| 05:24:28 | `remote.identity` | **Agree** | 24 ms | 同上 |

结论：C# 经标准 .NET for Android 互操作读到的 Keystore 公钥（别名 `dshmobile-ssh-ecdsa-p256`）与旧实现发布的公钥行 / 指纹逐字节一致；
私钥在 Keystore 内完成签名（硬件保护），转换为 SSH 签名格式后可用公钥验证——C# 走 SSH 认证所需的签名路径在真机成立，私钥未导出、未新建密钥。
C# 对 SharedPreferences 里旧实现写入的主机列表的解析，以及对保存请求的校验 / 指纹继承预测，与旧实现一致。

## 未覆盖

- `remote.host-key`（连接后的指纹判定）、信任（`remote.trust`）、删除、保存被拒（校验文案）在真机上未触发，只有向量 + 单测。
- SSH 连接、能力探测、远端 `dsh web`、转发仍是旧 JSch 实现（PR-09 后续段）。
