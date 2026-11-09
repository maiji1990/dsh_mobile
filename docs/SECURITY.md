# 安全基线（SECURITY）

完整威胁模型与控制表见方案第 12 节。本文件列出**已在代码中落实**与**每个 PR 必须遵守**的规则。

## 每个 PR 必须遵守

- 不提交密钥、Keystore、Token、测试私钥（`.gitignore` 已排除 `*.keystore` / `*.jks` / `*.p12`）。
- 日志不得记录密码、Token、Cookie、SSH 私钥、完整屏幕内容；Bridge 日志只记录 `method` / `code` / 耗时，不记录 payload 原文中的敏感字段。
- 新增 Bridge 方法必须登记到 [PROVIDER_CONTRACTS.md](PROVIDER_CONTRACTS.md) 并说明权限等级（R0–R4，TASK-010 统一定义）。
- 不向 WebView 暴露任意 Native 调用或 shell 接口。
- Root / ADB 只在 `optional.developer` 中存在，默认关闭，Agent 默认无权使用。

## 已落实

| 控制 | 位置 |
|---|---|
| Bridge 单写者串行执行、在途 requestId 去重 | `native/native-bridge/.../DshNativeFacade.java` |
| Bridge 稳定错误码、拒绝未知 method（不伪装成功） | 同上 + `src/DshMobile.Core/Bridge/BridgeErrorCodes.cs` |
| 关闭后拒绝执行（`CLOSED`） | 同上 |
| `android:allowBackup="false"`（避免用户数据 / 未来凭据经 adb backup 外泄） | `src/DshMobile.Android/AndroidManifest.xml` |
| 明文 HTTP 仅允许 127.0.0.1 / localhost（DSH 回环） | `Resources/xml/network_security_config.xml` |
| 运行环境包导入路径规范化并限制在 App 自有存储内（防 `..` / 符号链接逃逸） | `RuntimeController.resolveImportFile` |
| 诊断只运行固定只读命令；Bridge 不提供任意 guest shell | `RuntimeController.DIAGNOSTICS` |
| DSH launch token 不进入控制面 JSON / 日志，仅暴露 `hasLaunchToken` | `RuntimeController.status` |
| 运行层下载按固定 SHA-256 校验；android-side 再与随包 sidecar 交叉校验 | `runtime-bundle/fetch-runtime.ps1` |
| Maven 运行时依赖白名单，出现未分类依赖即构建失败（强制许可审计） | `native/native-bridge/build.gradle.kts` `exportRuntimeJars` |

## 待实现（按里程碑）

- M2：Runtime 层**签名**清单校验（当前仅固定哈希）、回滚 UI；PRoot 不是安全隔离，guest 内代码按不可信处理。
- M3：WebUI loopback-only、token 轮换、origin 校验。
- M5：R0–R4 审批、审计日志、单写者设备租约。
- M8：SBOM、密钥扫描、品牌资源扫描进入 Release CI。

## 漏洞报告

在仓库公开前补充私密报告渠道。
