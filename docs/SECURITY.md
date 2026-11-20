# 安全基线（SECURITY）

完整威胁模型与控制表见方案第 12 节。本文件列出**已在代码中落实**与**每个 PR 必须遵守**的规则。

## 每个 PR 必须遵守

- 不提交密钥、Keystore、Token、测试私钥（`.gitignore` 已排除 `*.keystore` / `*.jks` / `*.p12`）。
- 日志不得记录密码、Token、Cookie、SSH 私钥、完整屏幕内容；运行时日志只记录操作 / 错误码 / 耗时，DSH 输出经 `LogRedactor` 脱敏。
- 新增对 Agent 暴露的设备能力必须走 `ui_*` 与 Relay 的档位 / 上限 / 逐次与本会话审批（ADR-0008），并说明权限等级（R0–R4）。
- 不向 WebView 暴露任意 Native 调用或 shell 接口。
- Root / ADB 只在 `optional.developer` 中存在，默认关闭，Agent 默认无权使用。

## 已落实

| 控制 | 位置 |
|---|---|
| 无 JSON 控制面 / Java Facade（ADR-0018 删除）；运行时操作在协调器内串行，失败返回稳定错误码（`RuntimeErrorCodes` 等），不伪装成功 | `DshMobile.Application.Runtime.RuntimeCoordinator` |
| `android:allowBackup="false"`（避免用户数据 / 未来凭据经 adb backup 外泄） | `src/DshMobile.Android/AndroidManifest.xml` |
| 明文 HTTP 仅允许 127.0.0.1 / localhost（DSH 回环） | `Resources/xml/network_security_config.xml` |
| 运行环境包只经系统文件选择器（SAF）选取，复制进 App 缓存后导入、导入后删除副本；无存储权限 | `MainActivity.PickRuntimeBundle`、`LayerInstaller` |
| 诊断只运行固定只读命令；不提供任意 guest shell | `GuestDiagnostics.Commands` |
| DSH 探测只接受白名单 profile 名，运行 APK 内固定脚本（argv 逐 token 引号）；不发起模型调用 | `DshProbe`、`DshProfiles.All`、`src/DshMobile.Runtime/Guest/Assets/dsh-probe.mjs` |
| 不打包 DSHBox 的手机操控插件（mobile-pilot 等）与任何 DSHBox 插件，避免出现第二套自动化工具；APK 资产只有登记的 4 个上游文件 | `src/DshMobile.Android/Assets/README.md`、`UpstreamPinnedValuesTests` |
| DSH launch token 只在 `DshHost` 内存，状态只暴露有无 token；DSH 日志经 `LogRedactor` 脱敏 | `DshLaunchToken`、`LogRedactor` |
| WebUI：token 只在 `CSharpWebUiController` 内拼进 `loadUrl`，状态条只给状态与路径，不入日志 | `CSharpWebUiController` |
| WebUI：仅允许回环 DSH 源（`DshOriginPolicy`，不用 `System.Uri`）；外部 http(s) 交给系统浏览器，其它 scheme 拦截；无 `addJavascriptInterface`；禁用 file/content 访问；混合内容禁止；第三方 Cookie 关闭 | `CSharpWebUiController`、`WebUiPolicy` |
| WebUI：401 恢复每个 token 修订仅一次，避免循环 | `WebUiPolicy`、`DshTokenRevisionTracker` |
| 工作区文件：拒绝绝对路径 / `..` / NUL；任何路径分量是符号链接（含悬空链接）即拒绝，链接只列出不跟随（guest 链接在宿主侧会指向宿主路径） | `src/DshMobile.Core/Files/WorkspaceFileSystem.cs` |
| 工作区文件：`.dsh/`（DSH_HOME，含 `.credentials.yaml`）只列出，不可预览 / 导出 / 写入（真机验证） | 同上 |
| 工作区导入：不覆盖已有文件；先写临时文件再改名，失败不留半成品；文件名净化为单一路径分量 | 同上 |
| 文件进出只经系统文件选择器（SAF）按次授权，无存储权限 | `src/DshMobile.Android/Files/` |
| 文件工作台（vNext PR-09，[ADR-0026](ADR/0026-file-providers-trash-conflicts-archives.md)）：`.dsh` 双向不可触（不读 / 改 / 删 / 改名，也不能被写入 / 移入 / 复制入 / 解压入）；链接只改链接本身、复制时跳过、递归删除不下行；能力按条目真实权限给出，越权为 `NOT_SUPPORTED` | `src/DshMobile.Core/Files/WorkspaceProvider.cs` |
| 编辑保存带打开时版本（长度 + mtime + SHA-256），不符即 `CONFLICT`，不静默覆盖；原子替换并保留 Unix 权限位；非 UTF-8 只读不重编码 | `WorkspaceProvider.WriteAsync`、`TextDocument` |
| 删除进工作区外的回收站（DSH 不可见）并可撤销；无回收站时明示永久删除 | `WorkspaceProvider.Delete / Restore` |
| 压缩包不可信：staging 解包、全部通过才就位；条目名规范化 + 逐段校验；不创建链接 / 设备；重名中止；条目数与实际写入字节上限 | `WorkspaceProvider.Archive.cs` |
| DSH 内部的插件管理使用随包的 pnpm 11.1.2。归档按 SHA-256 校验，使用 copy 模式和 guest 的 registry 配置 | `GuestToolchain` |
| 手机操控默认关闭；只在用户点「开启中继」后运行，「关闭中继」/ 停止沙箱时停止并注销 `ui_*` 工具 | `CsRelay`、`RuntimeCoordinator.SandboxStopping`、`UiToolsRegistration` |
| Relay 注册上游全部 39 项能力（ADR-0008），每次调用仍过未修改的闸门：档位上限、逐次 / 本会话审批、系统态、执行配额；`sys.shell` 上限 ASK_ONLY、默认 DENIED，改系统的动词还需逐条开关（本 App 不提供该界面） | `DshMobile.Runtime.Automation.Relay`（`RelayCapabilityListing`、`RelayGate`） |
| Relay 所需权限与组件由 App 清单声明（PR-10 前来自 Relay 库清单合并，删除后逐项比对保留）；每一项（运行时权限、通知监听、录屏、Shizuku）都由用户在系统 / Shizuku 界面授予，App 不代为授予 | `src/DshMobile.Android/AndroidManifest.xml` |
| Shizuku 只由用户在 Shizuku 自己的授权框里允许；App 只发起请求（请求码 41），不启动 Shizuku、不重复弹框、被永久拒绝时如实提示 | `ShizukuClient` / `ShizukuConnection` |
| 无障碍服务只由用户在系统设置中开启，App 只提供跳转 | `AutomationActivity` |
| 每次 Relay 调用经通知栏审批（允许一次 / 本次会话允许 / 拒绝）；receiver 不导出、PendingIntent 不可变；通知被关时失败关闭（`E_GATE_NO_FOREGROUND`），无自动放行 | `RelayApprovalNotifications`、`ApprovalNotifications` |
| 审批回答只认卡片自己的 128 位一次性令牌（guest 与 App 同 UID，能 `am broadcast`）：常数时间比较、用一次作废、取消即吊销 | `ApprovalTokens`（`RelayApprovalParityTests`） |
| 「允许」只能在本 App 的确认页上按下（`RelayApprovalConfirmActivity`：不导出、`FLAG_SECURE`、过滤被遮挡的触摸）；确认页在前台时 Relay 拒绝一切写操作（`selfWindowVisible` → `E_TASK_SUSPENDED_BY_HOST`），Agent 点不到；卡片与确认页显示目标与参数 | ADR-0008 |
| `ui_*` 写工具动作前用 `ui.node` 实时回读并校验身份；旧快照的 ref 本地即返回 `STALE_ELEMENT`，不发底层请求；任何写操作之后当前快照的 ref 作废；写操作只在「尚未作答」时重发，超时不重发；无 `ui_confirm_approval` | `src/DshMobile.Runtime/Automation/Assets/ui-facade.mjs` |
| `ui_*` 错误保守映射：未知 Relay 码一律 `OBSERVATION_INCOMPLETE` 并附原码，从不把失败报成成功；拒绝后不重试 | 同上（`ui-facade.test.mjs` 覆盖） |
| 注册 `ui_*` 只写宿主专属 `--patch` 层且带标记，不碰用户 profile；不覆盖、不删除他人内容 | `UiToolsRegistration`（`ui-tools-registration.json` 向量） |
| 浏览器 `browser_*` 已删除（ADR-0018）：无 `:browser` 进程、无 `BROWSER_INTERNAL` 组件、不打包 browser-use 插件 | `AndroidManifest.xml` |
| 远程主机：SSH 私钥为 Android Keystore 中不可导出的 EC P-256 密钥，在 Keystore 内签名，私钥从不进入进程内存或文件；不支持密码登录 | `AndroidSshKeySource`、`KeystoreHostAlgorithm` |
| 远程主机：主机指纹只能由用户确认固定，且只能固定本次连接刚出示的那一个；未确认不建立会话，指纹变化一律拒绝（`HOST_KEY_CHANGED`） | `HostKeyPin`、`CsRemoteHosts.Trust`（`RemoteHostStoreTests`、`remote-ssh.json`） |
| 远程主机（C# 连接，ADR-0012）：同一把 Keystore 密钥经自定义 SSH.NET `HostAlgorithm` 在 Keystore 内签名，C# 不导出、不创建密钥；`HostKeyReceived` 只在指纹与固定值相同时放行，未知 / 不符在密钥交换阶段（认证之前）断开；C# 连接从不自行写入固定指纹；主机密钥算法列表与旧 JSch 实现相同（不含 SHA-1 `ssh-rsa`） | `RemoteSshConnector`、`KeystoreHostAlgorithm`（`RemoteSshParityTests`、`RemoteSshOpenSshTests`） |
| 远程主机：只建立本地转发（手机 → 远端回环），不建立远程转发；远端 DSH 无法访问手机的 `ui_*` | `RemoteSshSession` |
| 远程主机（C# 启停，ADR-0013）：远端命令固定为 `dsh --profile web --port <已校验端口>`；launch token 只在 `RemoteSshSession` 私有字段，不进状态 / 日志（日志尾经 `token=***` 脱敏）；转发只绑 `127.0.0.1`；存活核查只发不带 token 的 `GET /` | `RemoteSshSession`、`RemoteDshRules`（`RemoteSshParityTests`、`RemoteSshOpenSshTests`） |
| 远程主机：远端 launch token 只在进程内存，状态只暴露有无 token；断线 / 停止即作废；错误信息中的 token 被替换为 `***` | `RemoteLaunchToken`（`remote-ssh.json`） |
| 远程主机：主机名 / 用户名白名单校验（拒绝选项与 shell 语法），远端只执行固定命令 | `RemoteHostProfile`、`RemoteConnectRules` |
| 运行层下载按固定 SHA-256 校验；android-side 再与随包 sidecar 交叉校验 | `runtime-bundle/fetch-runtime.ps1` |
| 无 Gradle / Maven 传递依赖（ADR-0018）；Maven 依赖只有 Shizuku 13.1.5（`AndroidMavenLibrary` 精确版本），NuGet 精确版本 | `DshMobile.Android.csproj`、`docs/UPSTREAMS.md` |
| guest 命令由 argv 构建，每个 token 使用 POSIX 单引号转义 | `DshMobile.Runtime.Guest.GuestCommandLine`、`tests/TestVectors/guest-command-line.json` |
| 可执行文件只允许 `nativeLibraryDir` 内的裸 `lib*.so` 名（Android 10+ W^X）；写在 `filesDir` 的 ELF 永不直接 exec | `DshMobile.Native.NativeExecutablePolicy` |
| 运行时状态变化先写 durable journal 再生效；进程中断后不冒充“仍在运行”，恢复为 `Failed(RUNTIME_INTERRUPTED)` | `DshMobile.Runtime.RuntimeStateMachine`、`FileRuntimeJournal` |
| 启动失败统一清理、停止失败不释放前台服务（避免无监督的 PRoot 孤儿） | `DshMobile.Application.Runtime.RuntimeCoordinator` |
| C# 运行时私有状态（journal、装后核对报告）放 `files/dshmobile-runtime/`，不绑定进 guest、不在用户工作区 | `RuntimeLayout.HostStateDir` |
| 发布清单 Ed25519 分离签名（域分离前缀 + 原始字节，keyId 由公钥派生、可多钥匙轮换），先验签后严格解析（https-only、无凭据 URL、纯文件名、64 位小写 SHA-256、DSH 层必须钉源码 tag / commit、拒绝重复 JSON 键）；通道不符 / 过期 / 未来时间 / 比上次接受的旧均拒绝；未内置公钥时不联网、返回 `UPDATE_NO_TRUSTED_KEYS`；只读，不下载不安装（ADR-0022） | `DshMobile.Runtime.Updates.{ReleaseManifestVerifier, ReleaseManifestParser, ReleaseCatalog, HttpReleaseSource}`、`ReleaseManifestTests` |
| 防重放下限落盘（每通道只升不降，损坏即拒绝、不当作空）；发布地址固定在代码里（`ReleaseEndpoints`）；App 与发布工作流共用一份可信公钥文件（`release-trusted-keys.txt`，格式错误即信任为空）；签名私钥只在受环境约束的签名步骤中以 secret 出现（`preview` → `release-preview`，仅 `main` 无必审；`stable` → `release-signing`，必需审批人 + 仅 `main`），发布后由无密钥的 job 匿名读回核对（ADR-0022 修订、ADR-0029） | `ReleaseAcceptanceStore`、`UpdateChecker`、`.github/workflows/release-publish.yml`、`ReleaseStateTests`、`UpdateCheckerTests`、`tests/tooling` |
| 制品下载只接受签名清单中的项（`VerifiedRelease` 只能由验证器生成）；存于 `files/updates`（不碰 `runtime-current` / `user-data`），名称由摘要与已校验文件名构成；续传仅限起点与总长都对上的 206，完成后整文件重算 SHA-256 与大小，不符即删；跳转离开 https 即失败；下载前检查空间，缓存有上限（ADR-0022 修订，PR-04） | `ArtifactDownloader`、`ArtifactStore`、`ArtifactDownloaderTests` |
| DSH 层与 Runtime 基础包在线更新：换层前重新校验整文件，均经 `probing` 阶段暂存旧层，健康检查（装后树核对 + READY + HTTP 401 + 版本）通过后提交，否则逆序恢复旧层。进程中断后恢复到旧层或已提交的新层。停止运行中的 DSH 须用户确认；启动自动检查仅只读拉取清单，绝不后台静默停机换层，保留 `user-data`（ADR-0024 / ADR-0029） | `LayerInstaller` / `DshLayerInstaller`（probing）、`RuntimeCoordinator.InstallRuntimeUpdateAsync / InstallDshUpdateAsync`、`UpdateCoordinator.InstallPlanAsync`、`UpdateCoordinatorTests`、`RuntimeBodyFaultInjectionTests`、`DshLayerProbeHoldTests` |
| DSH 层换层为日志化事务：旧层先移到同级 aside，新层就位后才退役，放置失败改回，进程中途死亡由 `Recover()` 完成或撤销（每次安装前、启动前）；不会因备份失败删除唯一可运行的层 | `DshLayerInstaller`、`DshLayerFaultInjectionTests` |
| C# 层解包与旧实现同等约束：拒绝 `user-data` / `.dsh` 路径、规范路径越界、链接目标越界；所有路径决定在内存模型上完成，目标写入层只接受已证明在根内的相对路径；磁盘目标要求空目录；装后核对只干跑不写盘 | `DshMobile.Runtime.Layers.LayerExtractor`、`tests/TestVectors/layer-extract.json` |

## 待实现（按里程碑）

- M2：Runtime 层**签名**清单校验——验证器与只读发现已实现（ADR-0022，见上表），**尚未内置发布公钥、未接 UI**，离线导入仍只校验固定哈希；回滚 UI；PRoot 不是安全隔离，guest 内代码按不可信处理。
- M3：WebUI 已落实 loopback-only / token 轮换 / origin 校验（见上）；待做：DSHBox 完整导航鉴权状态机、下载与上传审批（TASK-007）。
- M5：R0–R4 审批、审计日志、单写者设备租约。
- 手机操控写操作（ADR-0008）：确认页的保护依赖无障碍后端的 `selfWindowVisible`；C# 接管执行层时必须原样保留，并把
  「确认页在前台时写操作被拒」「伪造广播被丢弃」列入真机验收。批准 shell 类能力等于交出 shell 身份的能力，卡片须显示命令。
- 隐私：`ui_snapshot` 结果（屏幕文本）会进入模型上下文；文档与界面需提示用户在敏感页面（密码、支付、聊天）不要批准读取。
- M8：SBOM、密钥扫描、品牌资源扫描进入 Release CI。

## 漏洞报告

在仓库公开前补充私密报告渠道。
