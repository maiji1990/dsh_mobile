# 安全基线（SECURITY）

完整威胁模型与控制表见方案第 12 节。本文件列出**已在代码中落实**与**每个 PR 必须遵守**的规则。

## 每个 PR 必须遵守

- 不提交密钥、Keystore、Token、测试私钥（`.gitignore` 已排除 `*.keystore` / `*.jks` / `*.p12`）。
- 日志不得记录密码、Token、Cookie、SSH 私钥、完整屏幕内容；Bridge 日志只记录 `method` / `code` / 耗时，不记录 payload 原文中的敏感字段。
- 新增 Bridge 方法必须登记到 [PROVIDER_CONTRACTS.md](PROVIDER_CONTRACTS.md) 并说明权限等级（R0–R4；手机操控用 Relay 自己的档位 / 上限 / 逐次与本会话审批，见 ADR-0008）。
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
| `dsh.probe` 只接受白名单 profile 名，运行 APK 内固定脚本；不发起模型调用 | `RuntimeController.PROBE_PROFILES`、`guest-assets/dshmobile/dsh-probe.mjs` |
| 不打包 DSHBox 的手机操控插件（mobile-pilot 等），避免出现第二套自动化工具 | `native/native-bridge/build.gradle.kts` `stageDshboxShimAssets` |
| DSH launch token 不进入控制面 JSON / 日志，仅暴露 `hasLaunchToken` | `RuntimeController.status` |
| WebUI：token 只在原生 `WebUiController` 内拼进 `loadUrl`，回调只给状态与路径；C# 永不持有 token（真机日志核查 0 泄漏） | `WebUiController.kt` |
| WebUI：仅允许回环 DSH 源（DSHBox `DshOriginPolicy`）；外部 http(s) 交给系统浏览器，其它 scheme 拦截；无 `addJavascriptInterface`；禁用 file/content 访问；混合内容禁止；第三方 Cookie 关闭 | `WebUiController.kt` |
| WebUI：401 恢复每个 token 修订仅一次，避免循环 | `WebUiController.Client.onReceivedHttpError` |
| 工作区文件：拒绝绝对路径 / `..` / NUL；任何路径分量是符号链接（含悬空链接）即拒绝，链接只列出不跟随（guest 链接在宿主侧会指向宿主路径） | `src/DshMobile.Core/Files/WorkspaceFileSystem.cs` |
| 工作区文件：`.dsh/`（DSH_HOME，含 `.credentials.yaml`）只列出，不可预览 / 导出 / 写入（真机验证） | 同上 |
| 工作区导入：不覆盖已有文件；先写临时文件再改名，失败不留半成品；文件名净化为单一路径分量 | 同上 |
| 文件进出只经系统文件选择器（SAF）按次授权，无存储权限 | `src/DshMobile.Android/Files/` |
| 终端：PTY 数据只在原生 emulator 与 View 之间流动，不进入 C# 与 JSON 控制面；会话列表回调只含 id/标题/状态 | `TerminalController.kt`、`TerminalListener` |
| 终端：「停止」沙箱前先结束全部终端会话，避免 guest 进程脱离沙箱生命周期 | `RuntimeController`（`sandbox.stop`） |
| 终端：Ctrl 键栏为一次性锁存，粘贴走 emulator.paste（遵守 bracketed paste、剥离控制字节） | `TerminalController.kt` |
| 插件：spec / name 只接受 npm registry 包名（可选 @scope 与版本），拒绝路径、URL、git、file:、选项注入与 shell 元字符；profile 白名单（单测覆盖） | `RuntimeController.PACKAGE_SPEC` / `PACKAGE_NAME`、`PluginSpecTest` |
| 插件：安装前用户确认，明确告知为第三方代码、在沙箱内以 DSH 权限运行、PRoot 非安全隔离；结果显示真实退出码与输出，不伪造成功 | `PluginsActivity` |
| 插件：pnpm 版本钉在随包 11.1.2（离线、可复现），导入方式 copy；不注入 registry | `GuestToolchain.kt` |
| 手机操控默认关闭；只在用户点「开启中继」后运行，「关闭中继」/ 停止沙箱时停止并注销 `ui_*` 工具 | `RuntimeController`（`relay.*` / `sandbox.stop`）、`UiToolsRegistration` |
| Relay 注册上游全部 39 项能力（ADR-0008），每次调用仍过未修改的闸门：档位上限、逐次 / 本会话审批、系统态、执行配额；`sys.shell` 上限 ASK_ONLY、默认 DENIED，改系统的动词还需逐条开关（本 App 不提供该界面） | `MobileAutomation`（`RelayCapabilities.BuiltIn`） |
| Relay 库清单的权限与组件原样合并；每一项（运行时权限、通知监听、录屏、Shizuku）都由用户在系统 / Shizuku 界面授予，App 不代为授予；合并结果以 `aapt2 dump` 核对 | `src/DshMobile.Android/AndroidManifest.xml` |
| Shizuku 只由用户在 Shizuku 自己的授权框里允许；App 只发起请求（请求码 41），不启动 Shizuku、不重复弹框、被永久拒绝时如实提示 | `ShizukuClient` / `ShizukuConnection` |
| 无障碍服务只由用户在系统设置中开启，App 只提供跳转 | `AutomationActivity` |
| 每次 Relay 调用经通知栏审批（允许一次 / 本次会话允许 / 拒绝）；receiver 不导出、PendingIntent 不可变；通知被关时失败关闭（`E_GATE_NO_FOREGROUND`），无自动放行 | `RelayApproval.kt`、`RelayApprovalNotifications` |
| 审批回答只认卡片自己的 128 位一次性令牌（guest 与 App 同 UID，能 `am broadcast`）：常数时间比较、用一次作废、取消即吊销 | `ApprovalTokens`（Kotlin / C#，`RelayApprovalParityTests`） |
| 「允许」只能在本 App 的确认页上按下（`RelayApprovalConfirmActivity`：不导出、`FLAG_SECURE`、过滤被遮挡的触摸）；确认页在前台时 Relay 拒绝一切写操作（`selfWindowVisible` → `E_TASK_SUSPENDED_BY_HOST`），Agent 点不到；卡片与确认页显示目标与参数 | ADR-0008 |
| `ui_*` 写工具动作前用 `ui.node` 实时回读并校验身份；旧快照的 ref 本地即返回 `STALE_ELEMENT`，不发底层请求；任何写操作之后当前快照的 ref 作废；写操作只在「尚未作答」时重发，超时不重发；无 `ui_confirm_approval` | `guest-assets/dshmobile/ui-facade.mjs` |
| `ui_*` 错误保守映射：未知 Relay 码一律 `OBSERVATION_INCOMPLETE` 并附原码，从不把失败报成成功；拒绝后不重试 | 同上（`ui-facade.test.mjs` 覆盖） |
| 注册 `ui_*` 只写宿主专属 `--patch` 层且带标记，不碰用户 profile；不覆盖、不删除他人内容 | `UiToolsRegistration`（`UiToolsRegistrationTest`） |
| 浏览器运行在独立 `:browser` 进程，WebView 数据目录后缀 `browser`：Cookie / 存储与 DSH WebUI（主进程）分离 | `DshMobileApplication`、ADR-0005 |
| 浏览器命令服务与内部广播受 `signature` 级 `BROWSER_INTERNAL` 权限保护；guest 通道为私有 LocalSocket + 每次生命周期一次性 token（DSHBox broker 原样） | `src/DshMobile.Android/AndroidManifest.xml`、`BrowserCliBroker` |
| 沙箱停止即停止 broker 并广播撤销 AI 控制（同 DSHBox） | `BrowserTools.onSandboxState` |
| `browser_*` 工具默认不安装；安装前确认框说明第三方代码与权限；只运行 DSHBox 原版安装 / 卸载脚本，路径固定、无调用方输入 | `BrowserToolsActivity`、`BrowserTools.adapterCommand`（`BrowserToolsTest`） |
| AI 控制浏览器只能由用户在浏览器内批准；宿主不提供任何授予控制的入口；只读自检不请求、不使用控制权 | `browser-runtime`（上游）、`browser-check.mjs` |
| 仅打包 DSHBox 的 `browser-use` 插件；`mobile-pilot` / `mobile-adapt` 不打包 | `native/native-bridge/build.gradle.kts` `stageDshboxShimAssets` |
| 读 DSH 日志只回传点名 `@local/browser-use` 的行（≤5 行，每行 ≤300 字符），不回传其余日志 | `BrowserTools.adapterLogLines` |
| 远程主机：SSH 私钥为 Android Keystore 中不可导出的 EC P-256 密钥，经自定义 JSch `Identity` 签名，私钥从不进入进程内存或文件；不支持密码登录 | `SshKeys.kt` |
| 远程主机：主机指纹只能由用户确认固定，且只能固定本次连接刚出示的那一个；未确认不建立会话，指纹变化一律拒绝（`HOST_KEY_CHANGED`） | `RemoteHosts.trust`、`PinnedHostKeys`（`RemoteHostsTest`） |
| 远程主机：只建立本地转发（手机 → 远端回环），不建立远程转发；远端 DSH 无法访问手机的 `ui_*` / `browser_*` | `RemoteHosts.startDsh` |
| 远程主机：远端 launch token 只在原生内存，控制面只暴露 `hasLaunchToken`；断线 / 停止即作废；错误信息中的 token 被替换为 `***` | `RemoteHosts.tokenFrom` / `redact`（真机日志核查 0 泄漏） |
| 远程主机：主机名 / 用户名白名单校验（拒绝选项与 shell 语法），远端只执行固定命令 | `RemoteHostProfile.fromRequest` |
| 运行层下载按固定 SHA-256 校验；android-side 再与随包 sidecar 交叉校验 | `runtime-bundle/fetch-runtime.ps1` |
| Maven 运行时依赖白名单，出现未分类依赖即构建失败（强制许可审计） | `native/native-bridge/build.gradle.kts` `exportRuntimeJars` |
| guest 命令由 argv 构建、逐 token POSIX 单引号；插件 spec 中合法的 `>=` / `<` / `*` 不再被 shell 解释（PR-00，修复 `pkg@>=1.0.0` 重定向）；新旧实现共享向量，源码扫描禁止回退到字符串拼接 | `RuntimeController.pluginCommand/shellJoin`、`DshMobile.Runtime.Guest.{PluginCommand, GuestCommandLine}`、`tests/TestVectors/plugin-commands.json` |
| 可执行文件只允许 `nativeLibraryDir` 内的裸 `lib*.so` 名（Android 10+ W^X）；写在 `filesDir` 的 ELF 永不直接 exec | `DshMobile.Native.NativeExecutablePolicy` |
| 运行时状态变化先写 durable journal 再生效；进程中断后不冒充“仍在运行”，恢复为 `Failed(RUNTIME_INTERRUPTED)` | `DshMobile.Runtime.RuntimeStateMachine`、`FileRuntimeJournal` |
| 启动失败统一清理、停止失败不释放前台服务（避免无监督的 PRoot 孤儿） | `DshMobile.Application.Runtime.RuntimeCoordinator` |
| C# 运行时私有状态（journal、影子报告）放 `files/dshmobile-runtime/`，不绑定进 guest、不在用户工作区 | `RuntimeLayout.HostStateDir` |
| C# 层解包与旧实现同等约束：拒绝 `user-data` / `.dsh` 路径、规范路径越界、链接目标越界；所有路径决定在内存模型上完成，目标写入层只接受已证明在根内的相对路径；磁盘目标要求空目录；影子比对只干跑不写盘 | `DshMobile.Runtime.Layers.LayerExtractor`、`tests/TestVectors/layer-extract.json` |

## 待实现（按里程碑）

- M2：Runtime 层**签名**清单校验（当前仅固定哈希）、回滚 UI；PRoot 不是安全隔离，guest 内代码按不可信处理。
- M3：WebUI 已落实 loopback-only / token 轮换 / origin 校验（见上）；待做：DSHBox 完整导航鉴权状态机、下载与上传审批（TASK-007）。
- M5：R0–R4 审批、审计日志、单写者设备租约。
- 手机操控写操作（ADR-0008）：确认页的保护依赖无障碍后端的 `selfWindowVisible`；C# 接管执行层时必须原样保留，并把
  「确认页在前台时写操作被拒」「伪造广播被丢弃」列入真机验收。批准 shell 类能力等于交出 shell 身份的能力，卡片须显示命令。
- 隐私：`ui_snapshot` 结果（屏幕文本）会进入模型上下文；文档与界面需提示用户在敏感页面（密码、支付、聊天）不要批准读取。
- M8：SBOM、密钥扫描、品牌资源扫描进入 Release CI。

## 漏洞报告

在仓库公开前补充私密报告渠道。
