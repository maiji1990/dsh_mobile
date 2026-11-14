# ADR-0013：C# 远端 DSH 启停 / token / 转发——影子只做独立核查，不启动第二个远端 DSH

- 状态：已接受（2026-11-14，PR-09 第三段）
- 关联：[ADR-0006](0006-remote-host-ssh.md)（远端只跑上游 DSH、token 只在内存、只建本地转发、停止如实核查）、
  [ADR-0012](0012-csharp-ssh-client-sshnet.md)（SSH.NET 连接与 `remote.connect` 影子）、`docs/MIGRATION.md` PR-09 行

## 背景

第三段把过渡实现 `RemoteHosts.kt` 的 `startDsh` / `readTokenLine` / `httpStatus` / `stopDsh` / `refreshLiveness` 移植到 C#。
第二段的影子做法是「旧实现连接之后，C# 再连一次比对」，连接与 `--version` 探测没有副作用，可以重复。启动远端 DSH 不同：

- `dsh --profile web --port <端口>` 占用远端端口，第二个实例在同一端口必然失败；换端口则是在远端多跑一个 DSH，
  它与用户正在用的那个共享远端 DSH 的数据目录，可能互相干扰——影子不能改变远端状态；
- launch token 只存在于旧实现的原生内存（ADR-0006），C# 无从比对 token 本身。

## 决策

1. **C# 实现**（`RemoteSshSession.StartDshAsync` / `StopDshAsync` / `Refresh`，SSH.NET）：与旧实现同一条固定命令
   `dsh --profile web --port <dshPort>`（`dshPort` 经配置校验为 1024–65535 的整数），120 s 内从 stdout / stderr 逐行找
   `dsh web: …token=…`；token 只留在会话对象的私有字段，状态里只有 `hasLaunchToken`。找不到 → `DSH_START_FAILED`
   （附脱敏后的最后 6 行，同旧文案），会话保留。本地转发 `ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", dshPort)`；
   经转发不带 token `GET /` 得 401 才是 `READY`/`OK`。停止：`TERM` 信号后关闭通道，1.5 s 后经转发再请求一次，按旧规则判定
   「远端 DSH 仍在运行」（有回应且 200–499 → 是；会话仍在而请求失败 → 否；无会话 → 未知）。会话掉线 → `UNREACHABLE`，
   远端进程退出 → `DSH_START_FAILED`「remote dsh web exited」，token 随之作废。这些判定规则为纯函数 `RemoteDshRules`，
   由向量锁定（旧 Kotlin 测试执行与 `RemoteHosts.kt` 相同的表达式，源码契约钉住那些表达式）。
2. **与旧实现的差别（不影响结果）**：找到 token 后 C# 继续在后台读取并丢弃远端输出（旧实现停止读取，远端输出多时可能被流控卡住）；
   每条流用 UTF-8 解码器跨块解码（旧实现按块解码，跨块的多字节字符会变成替换字符，只影响失败时的日志尾）。
3. **影子 = 独立核查，不启动第二个 DSH**（切片 `remote.dsh`，`MigrationOptions.RemoteDsh`，默认 `LegacyWithShadow`）：
   - 旧 `remote.start` 得到 `READY` 之后，C# 用同一配置另开一条 SSH.NET 连接（不跑探测），建**自己的**本地转发到同一远端端口，
     不带 token 请求一次，用 C# 规则得出结果码，与旧实现的 HTTP 状态与结果码比对；
   - 旧 `remote.stop` 之后，C# 同样经自己的连接与转发请求一次，用 C# 规则得出「远端 DSH 是否仍在运行」，与旧实现的结论比对；
   - 影子从不启动、停止远端进程，不读取也不持有 token；旧实现未到达转发（`DSH_START_FAILED` 无 URL、`FORWARD_FAILED`）
     或没有结论（`remoteDshStillRunning` 为 null）时不比对。
   token 解析本身由第一段已移植的 `RemoteLaunchToken` 与本段的逐行读取向量覆盖。
4. **C# 可选**：长按「连接」建立的 C# 会话里，「启动远端 DSH」/「停止远端 DSH」/「断开」全部由 C# 完成（替换第二段的
   「暂不支持」提示）。真机验收时同一台主机先走旧实现、再走 C# 实现，两边结果写入证据。

## 后果

- 影子无法证明「C# 解析到的 token 与旧实现相同」；这一点只由向量 + 真机上 C# 会话 `hasLaunchToken` 与 401 / 303 行为证明。
- 桌面 spike（2026-11-14，Windows OpenSSH 普通用户进程）：约 11 s 读到 launch URL；经 C# 转发不带 token 得 401、带 token 得 303；
  `TERM` + 关闭通道后远端端口释放，经转发请求失败（即「已不再响应」）。
- 本段完成后 C# 覆盖远程主机的全部功能；默认翻转与 Material 3 远程页是 PR-09 最后一段，需用户确认。
