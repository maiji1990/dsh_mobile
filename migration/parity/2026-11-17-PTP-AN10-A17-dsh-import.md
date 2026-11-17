# ADR-0027 真机验收 · 2026-11-17

- 设备为 Honor PTP-AN10，Android 17，`arm64-v8a`。
- 原应用是 `io.github.maiji1990.dshmobile` 0.1.0，已安装 DSH `0.2.1-rc.5`。设备运行环境为 `0.2.1`，Node 为 `26.13.1`。
- 用含 .NET 程序集的 Release APK 覆盖安装。安装后 `lastUpdateTime` 为 `2026-11-17 13:16:59`，`firstInstallTime` 仍为 `2026-11-16 18:10:19`。
- Release APK 的运行时资产为 `assets/runtime/android-side.tar.zst` 及其 SHA-256 sidecar，包内无 `assets/dsh/`。
- 用户在真机确认离线导入锁定发布包 `0.2.2-rc.3.tar.zst` 并成功运行。随后从 ADB 临时转发对 `127.0.0.1:3080` 发起未认证 `GET /`，返回 HTTP 401，检查结束移除转发。

直接通过 `adb install` 安装未嵌入托管程序集的 Debug APK 时，真机曾报 `Failed to load System.Private.CoreLib.dll (0x80070002)`。上述验收采用 `-c Release -p:EmbedAssembliesIntoApk=true` 构建的 APK。

真机待测项包括全新安装、同版或旧版保留、改名包拒绝、运行中拒绝导入、健康检查失败回滚和强杀恢复。
