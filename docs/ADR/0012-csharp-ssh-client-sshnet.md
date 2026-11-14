# ADR-0012：C# SSH 客户端——SSH.NET + Keystore 自定义签名算法 + 与 JSch 相同的主机密钥算法列表

- 状态：已接受（2026-11-14，PR-09 第二段；用户确认 PR-09 = 移植远程 SSH，浏览器 `browser_*` 不迁移）
- 关联：[ADR-0006](0006-remote-host-ssh.md)（远程主机的安全与契约约束，继续有效）、[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first）、
  `docs/MIGRATION.md` PR-09 行、向量 `tests/TestVectors/remote-ssh.json`

## 背景

PR-09 第一段已把远程主机里不涉及 SSH 连接的部分（主机配置、指纹判定、本机身份）移植到 C#，SSH 连接仍是过渡实现
`RemoteHosts.kt` 里的 JSch。要把连接本身移到 C#，先得选一个 .NET SSH 客户端库，并且满足 ADR-0006 不可削弱的约束：

- 私钥是 Android Keystore 中不可导出的 EC P-256 密钥，只能在 Keystore 里签名（返回 DER）；
- 只接受用户确认过的主机指纹：未知的拒绝且不建立会话，指纹不符拒绝；
- 只用公钥认证，不支持密码；远端命令固定、各自独立 exec；
- 用户已经在 `authorized_keys` 里放了这把公钥，切换实现后必须继续可用。

## 选项

| 选项 | 结论 |
|---|---|
| **SSH.NET**（`SSH.NET` 2026.0.0，MIT） | 采用。纯托管；`IsTrimmable` / `IsAotCompatible`；公开 `HostAlgorithm` 抽象类与 `IPrivateKeySource`，可接自定义签名；`HostKeyReceived` 事件给出原始主机密钥数据并可拒绝；支持 exec / 本地转发 / keepalive。 |
| 在 guest 里跑 OpenSSH `ssh` | 否决（ADR-0006 已否决）：私钥会以文件形式落在工作区。 |
| 继续用 JSch（经 Java 互操作） | 否决：违背 ADR-0007（不给 Kotlin / Java 过渡层加新需求）。 |
| 自己实现 SSH 传输层 | 否决：密码学协议自写风险高，没有收益。 |
| 其他 .NET 库（如 Rebex，商业） | 否决：许可证与 GPL-3.0 分发不兼容或需付费。 |

## 决策

1. **库与版本**：`DshMobile.Runtime` 引用 `SSH.NET` **2026.0.0**（精确版本）。它在 .NET 11 / Android 上唯一的额外运行时依赖是
   `BouncyCastle.Cryptography` **2.7.0**（MIT，按 NuGet 最低适用版本解析，固定）；`Microsoft.Extensions.Logging.Abstractions`
   在 .NET 11 共享框架（含 `android-arm64` 运行时包）中自带，还原时被裁掉，不随包分发。两者都登记到 `docs/UPSTREAMS.md`。
   MIT 与本项目 GPL-3.0（ADR-0003）兼容。
2. **Keystore 签名**：自定义 `KeystoreHostAlgorithm : HostAlgorithm`，名称 `ecdsa-sha2-nistp256`，`Data` 为第一段的
   `SshWire.EcdsaPublicBlob`，`Sign(data)` = `SshWire.DerToSshSignature(ISshKeySource.SignDer(data))`。已对照 SSH.NET 2026.0.0
   源码核实：`PrivateKeyAuthenticationMethod` 发送 `(Name, Data)`，签名字段直接取 `Sign` 的返回值，内置 `KeyHostAlgorithm.Sign`
   返回的也是完整的 `string(alg) || string(sig)`，所以自定义实现只需返回 SSH 签名数据。私钥从不进入进程内存，C# 也不创建密钥
   （缺失时返回 C# 专用码 `IDENTITY_MISSING`，创建仍由旧实现负责，直到本切片成为默认）。
3. **主机指纹固定**：在 `HostKeyReceived` 中用第一段的 `HostKeyPin.Check(pinned, e.HostKey)` 判定，只有 `Ok` 时 `CanTrust = true`；
   未知 / 不符时 SSH.NET 在密钥交换阶段断开，**认证之前**，不建立会话。指纹由我们自己按 `SshWire.Fingerprint` 计算
   （不用库自带的格式），与旧实现逐字节一致。C# 连接从不写入固定指纹，固定仍只经旧实现的 `remote.trust`（只接受上次出示的指纹）。
4. **主机密钥算法列表与 JSch 一致**：服务器（例如 Windows OpenSSH）通常同时有 ed25519 / ecdsa / rsa 主机密钥，协商出哪一把取决于
   客户端的偏好顺序；顺序不同，同一台主机就会出示另一把密钥，用户固定过的指纹会被误判为 `HOST_KEY_CHANGED`。JSch 2.28.8 默认
   `server_host_key` 与 SSH.NET 2026.0.0 默认列表顺序相同，只是 SSH.NET 末尾多出 `ssh-rsa-cert-v01@openssh.com` 与 `ssh-rsa`
   （SHA-1）。C# 删除这两项，得到与 JSch **完全相同**的列表（由向量 `hostKeyAlgorithms` 锁定：旧 Kotlin 测试比对
   `JSch.getConfig("server_host_key")`，C# 测试比对实际的 `ConnectionInfo.HostKeyAlgorithms`）。去掉 `ssh-rsa` 同时去掉了 SHA-1 签名。
5. **结果分类沿用 ADR-0006 的码**：指纹判定优先（与旧 `connect` 的 `when` 顺序相同）：无固定且看到密钥 → `HOST_KEY_UNKNOWN`
   （附指纹与密钥类型）；有固定且看到不同密钥 → `HOST_KEY_CHANGED`（附指纹）；之后才按失败种类：`SshAuthenticationException` →
   `AUTH_FAILED`，超时（`SshOperationTimeoutException`、`SocketError.TimedOut`）→ `TIMEOUT`，其余 → `UNREACHABLE`。旧实现按 JSch
   异常文案（`Auth fail` / `timeout`）区分；两者的“失败种类 → 码”规则同为一张表（向量 `connectOutcome`）。
   `UNREACHABLE` / `TIMEOUT` 的说明文字来自各自的库，不比对。
6. **能力探测**：与旧实现相同的两条固定命令 `node --version`（20 s）、`dsh --version`（60 s），各自独立 exec；结果规则相同：
   退出码为 0 才算存在，版本取 stdout + stderr 的第一行非空行、Kotlin `trim`、前 80 个字符（向量 `probe`）。`dsh` 不存在时
   `CONNECTED` + `DSH_MISSING`。已知差别：命令超时时旧实现保留已读到的部分输出，C# 只记“超时、无输出”（两边 `node` / `dsh`
   都是 false）；影子对 C# 超时记为 Inconclusive。
7. **心跳**：`KeepAliveInterval` 15 s（旧实现 `serverAliveInterval` 15 s × 3）；连接 / 通道超时 15 s，与旧实现相同。
8. **可选上线 + 影子**：
   - `MigrationOptions.RemoteConnect`，默认 `LegacyWithShadow`：用户点「连接」仍由 JSch 决策；之后 C# 用连接前读到的同一主机配置
     **另开一条连接**，跑同样的探测后立即断开，比对结果码、指纹、密钥类型与探测（切片 `remote.connect`）。影子连接只读：不固定指纹、
     不启动远端 DSH、不建立转发。网络类失败（`UNREACHABLE` ↔ `TIMEOUT`）互换记为 Inconclusive。
   - 长按「连接」= 用 C# 连接（先断开旧连接，同一时刻只连一个远端）。C# 连接本段只做到连接 + 探测：启动远端 DSH / 转发仍是旧实现，
     C# 会话期间「启动 DSH」如实提示不支持（不伪造成功）；看到未知指纹时只显示指纹并提示用短按「连接」确认。

## 后果

- APK 增加 SSH.NET 与 BouncyCastle 两个程序集（裁剪后体积在真机构建中记录）。
- 桌面 spike（2026-11-14，本机 Windows OpenSSH 9.5 以普通用户进程运行于 `127.0.0.1` 高端口，不改系统服务）：只返回 DER 的签名器
  认证通过；无固定指纹时在密钥交换阶段被拒；`node --version` / `dsh --version` / 退出码与 PC 一致。
- 下一段：远端 `dsh web` 启动、launch token 解析（第一段已移植解析规则）、本地转发与 401 核查、停止核查移植到 C#，之后 C# 连接
  才能成为默认；那之后才删除 JSch 与 `RemoteHosts.kt`（与其他 DSHBox 依赖一样，等所有 PR 完成后统一移除）。
