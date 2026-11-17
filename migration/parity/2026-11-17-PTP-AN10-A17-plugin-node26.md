# Node 26 插件列表真机复测 · 2026-11-17

- 设备为 Honor PTP-AN10，Android 17，`arm64-v8a`。运行环境 `0.2.1`，Node `26.13.1`，DSH `0.2.2-rc.3`。
- 修复前，手机中继已开启，DSH 回环返回 HTTP 401。插件页显示 `list: FAILED exit=127 GUEST_CMD_FAILED` 和 `/usr/local/bin/corepack: not found`。
- 用 `-c Release -p:EmbedAssembliesIntoApk=true` 构建并覆盖安装。APK SHA-256 为 `19fe1c6e597141b84f684e4d809e83bbfcf92b27c9f748d93958d54f523ffa5c`，ADB 返回 `Success`。安装后 `lastUpdateTime` 为 `2026-11-17 14:01:03`，`firstInstallTime` 仍为 `2026-11-16 18:10:19`。
- 恢复中继后，手机操控页显示「已开启 · 信箱运行中」。启动 DSH 后，首页显示 `0.2.2-rc.3`，未认证回环请求返回 HTTP 401。
- 打开插件页，输出 `list: OK`，并显示「没有已安装的插件」。本次复测仅执行查询，插件安装与卸载待真机复测。
