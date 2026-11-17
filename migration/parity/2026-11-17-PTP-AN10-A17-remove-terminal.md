# 移除原生终端真机验收 · 2026-11-17

- 设备为 Honor PTP-AN10，Android 17，`arm64-v8a`，已安装 DSH `0.2.2-rc.3`。
- 覆盖前，首页导航显示「首页 / 终端 / 文件 / 更多」，DSH 处于停止状态。
- 用 `adb install -r` 覆盖安装 Release APK，SHA-256 为 `c8a186eb26ef169e8688a51533f7cd77c59421faf6957e9f992b1ffcde99bc1a`。ADB 返回 `Success`，应用的 `firstInstallTime` 仍为 `2026-11-16 18:10:19`，`lastUpdateTime` 更新为 `2026-11-17 15:56:29`。
- 覆盖后，真机竖屏导航只有「首页 / 文件 / 更多」。依次进入文件页、更多页并返回首页。
- 启动原有 DSH `0.2.2-rc.3`，首页显示「打开 DSH」，未认证回环 `GET /` 返回 HTTP 401。移除检查用的 ADB 转发并停止 DSH，首页恢复「启动 DSH」。

横屏导航、旧版终端 Fragment 的系统状态恢复及开发者选项的 PTY 自检仍待真机复测。
