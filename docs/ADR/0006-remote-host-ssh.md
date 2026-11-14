# ADR-0006：远程 DSH 主机——SSH（JSch）+ Android Keystore 密钥 + 主机指纹固定

- 状态：已接受（2026-11-10；用户选择：测试主机为本机 Windows PC，认证用手机内生成的密钥）
- 修订（2026-11-10）：其中“复用 DSHBox / Kotlin 实现”的部分按 [ADR-0007](0007-source-ownership-csharp-first.md) 降级为过渡实现，将逐切片替换为 C#；本 ADR 的安全与契约约束（审批、单一 `ui_*` / `browser_*` 契约、主机指纹固定、不可导出私钥）继续有效，替换实现必须同样满足。
- 修订（2026-11-14）：决策 1、2 的 JSch 部分由 [ADR-0012](0012-csharp-ssh-client-sshnet.md) 的 C# 实现（SSH.NET + Keystore 自定义 `HostAlgorithm`）逐段替换；其余决策不变。
- 来源：方案第 11 节、12.2「Remote」、M7；TASK-012（验收：远程会话隔离）
- 关联：ADR-0004 / ADR-0005（本机手机操控与浏览器能力不对远端开放）

## 背景

DSHBox 没有远程主机能力，本任务为新建。方案要求：用户 SSH 密钥 + 显式 Host Key 校验 + 本地端口转发；只使用远端实际
提供的能力；凭据由 Android Keystore 保护；远端不得默认获得本机手机能力；本机与远端切换不串会话、不泄漏 Token。

## 决策

1. **SSH 实现**：主进程 Kotlin（`native-bridge`）使用 `com.github.mwiede:jsch` 2.28.8（Revised BSD / ISC，无必需依赖，
   导出为白名单 jar）。不在 guest 内跑 `ssh`（私钥会以明文落在工作区）。
2. **密钥**：Android Keystore 生成不可导出的 EC P-256 签名密钥（别名固定），以自定义 JSch `Identity` 完成
   `ecdsa-sha2-nistp256` 认证；私钥从不离开 Keystore，App 只展示 OpenSSH 格式公钥供用户加入 `authorized_keys`。
   V1 不导入既有私钥、不支持密码登录。
3. **主机指纹**：自定义 `HostKeyRepository`。首次连接返回 `HOST_KEY_UNKNOWN` 与 SHA256 指纹，**不建立会话**；用户在界面
   核对并确认后固定；之后指纹不符一律 `HOST_KEY_CHANGED` 拒绝连接，只能由用户删除该主机后重新确认。主机配置
   （名称、地址、端口、用户、远端 DSH 端口、固定指纹）存于 App 私有 SharedPreferences，不含秘密。
4. **远端只跑上游 DSH**：所有远端命令为固定命令（不拼接调用方输入以外的字段，且主机字段经白名单校验）：能力探测
   `node --version`、`dsh --version`（各自独立 exec，兼容 Windows `cmd.exe` 与 POSIX shell）；启动
   `dsh --profile web --port <端口>`（远端只监听回环），launch token 从其输出解析，**只存在于原生内存**，
   不进入控制面 JSON / 日志。
5. **转发**：只建立本地转发 `127.0.0.1:<随机端口> → 远端 127.0.0.1:<DSH 端口>`；**不建立任何远程转发**（`-R`），
   远端无法连回手机；远端 DSH 不获得 `ui_*` / `browser_*` 等本机能力。可达性以经转发 `GET /` 得到 401（鉴权生效）判定。
6. **会话隔离**：远端状态由独立的 `RemoteHosts` 管理，与本机运行时互不共享对象、端口、token；V1 同一时刻只连一个远端。
   远端 WebUI 将在独立进程（独立 WebView 数据目录）中承载——回环地址的 Cookie 不按端口区分，同进程会与本机 DSH 串会话；
   该部分作为后续切片，首个切片不打开远端 WebUI。
7. **断线恢复**：会话断开（心跳失败 / 通道关闭）时状态如实变为 `DISCONNECTED`，转发与 token 作废；`remote.connect`
   重新建立，绝不沿用旧 token。
8. **结果码**：桥接层只用通用错误码；SSH 结果作为载荷中的稳定 `code` 返回：`OK`、`HOST_KEY_UNKNOWN`、`HOST_KEY_CHANGED`、
   `AUTH_FAILED`、`UNREACHABLE`、`TIMEOUT`、`DSH_MISSING`、`DSH_START_FAILED`、`FORWARD_FAILED`、`NOT_CONNECTED`。

## 实施中确认的细节

- 真机（Android 17）Keystore 报告密钥为硬件保护；手机显示的公钥指纹与 PC 上 `ssh-keygen -lf` 完全一致。
- Windows OpenSSH（cmd.exe）可直接执行 `node --version` / `dsh --version` / `dsh --profile web --port N`（npm 的 `dsh` 垫片）；
  远端 DSH 只监听 `127.0.0.1`。关闭 exec 通道（停止 / 断开）时 Windows OpenSSH 会结束远端 `dsh`，已在 PC 上核实端口释放。
- Android `org.json` 的 `optString` 会把 JSON `null` 变成字符串 `"null"`：新增主机的 `"id": null` 必须按缺省处理（真机首版报
  `unknown host id`，已修并加单测）。
- 停止核查：会话仍在时经转发请求失败即视为远端 DSH 已不响应；无会话时如实报告「未知」。

## 后果

- 测试主机为本机 Windows PC：需用户手动启用 Windows OpenSSH Server、安装 Node + DSH，并把手机公钥加入
  `authorized_keys`（管理员账户为 `C:\ProgramData\ssh\administrators_authorized_keys`）。开发时以 `adb reverse` 让手机
  回环端口直达 PC 的 sshd，正式使用填写真实主机地址。
- Windows 上关闭 exec 通道不保证结束远端 `dsh` 进程，停止结果须如实核查并记录，不宣称已停止。
- 新增依赖需进入许可证白名单与 UPSTREAMS 登记。
