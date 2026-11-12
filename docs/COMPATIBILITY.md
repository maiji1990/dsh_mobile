# 兼容性（COMPATIBILITY）

## 设备边界（V1）

- `minSdk 29`（Android 10），与 DSHBox `common` / `sandbox-manager` 的 `minSdk = 29` 一致。
- ABI：`android-arm64` 主目标；`android-x64` 仅用于模拟器开发冒烟，不保证运行层可用。
- 主测试目标：Android 14 / 16 / 17 真机。

## 工具链锁定（2026-11-09）

| 组件 | 版本 | 来源 |
|---|---|---|
| .NET SDK | `11.0.100-rc.2.26504.105` | `global.json`（`rollForward: disable`） |
| .NET workload set | `11.0.100-rc.2.26513.2`（android `37.2.0-rc.2.84`） | `global.json` `workloadVersion` |
| JDK | Temurin 21.0.12 | 本机 `JAVA_HOME` |
| Gradle | 9.5.0 | `native/gradle/wrapper/gradle-wrapper.properties` |
| AGP / Kotlin | 9.3.1 / 2.4.20 | `third_party/dshbox/gradle/libs.versions.toml` |
| Android compileSdk | 36（native-bridge / DSHBox 模块）；.NET App 使用 workload 默认 API 37（targetSdk 37 = Android 17） | |
| Android build-tools | 37.0.0（`native/gradle.properties` `dshmobile.buildToolsVersion`，覆盖 AGP 9.3.1 默认的 36.0.0） | |
| .NET Android 运行时 | CoreCLR（APK 内 `libcoreclr.so` / `libclrjit.so`，已核对） | |
| Xamarin.Kotlin.StdLib | 2.4.0.1 | `src/DshMobile.NativeBinding` |

> 方案文档基线写的是 .NET 11 **RC3**（2026-10-25）；本机当前仅安装 **RC2**。按方案“GA 后做独立升级 PR”的要求，升级到 RC3 / GA 时修改 `global.json` 并重新跑基线，单独提交。

## 真机矩阵

| 设备 / 系统 | Bridge POC | Runtime | DSH profiles | 备注 |
|---|---|---|---|---|
| Android 10 / ARM64 | 待测 | — | — | |
| Android 14 / ARM64 | 待测 | — | — | |
| Android 16 / ARM64 | 待测 | — | — | |
| Android 17 / ARM64 | **通过（见下）**，旋转未测 | **通过**（DSH 0.2.1-rc.5 READY） | web/sdk/sdk-minimal/acp 通过；headless 受限（凭据）；plugin 失败（pnpm） | Honor PTP-AN10，厂商 ROM `11.0.0.117`，4 KB 页，2026-11-09 |
| x86_64 模拟器 | 待测 | 不保证 | 不保证 | |

## 真机记录

### 2026-11-09 · Honor PTP-AN10 · Android 17 (API 37) · arm64-v8a · ROM `PTP-AN10 11.0.0.117(SP2C00E117R102P1)`

构建：Release（full trim，CoreCLR），`org.example.dshmobile` 0.1.0；Bridge 0.1.0，DSHBox `v1.5.1-0-g7c9d33c`。

| TEST_PLAN M1 步骤 | 结果 |
|---|---|
| 1. 启动 + `bridge.info` | 通过：冷启动 287 / 225 / 219 ms，热启动 42 ms；`sdk=37 abis=[arm64-v8a]`；Kotlin 运行时 `2.4.0`（NuGet `Xamarin.Kotlin.StdLib` 提供；编译用 2.4.20、apiVersion 2.2，兼容） |
| 2. DSHBox Kotlin 调用 | 通过：`Versions.compare` → `("0.1.1","0.1.1-rc.2")=1`、`("0.1.0-rc.9","0.1.0-rc.10")=-1`、`("v1.5.1","1.5.1")=0`，与 DSHBox 语义一致 |
| 3. 1000 次压测 | 通过：31 轮全部 `ok=1000 failed=0`，单轮平均约 22 ms（最大 32 ms）；每轮结束 `inFlight C#=0 native=0 orphans=0 malformed=0` |
| 4a. 压测中切后台再回来 | 通过：结果完整，计数一致，无异常 |
| 4b. 压测中旋转屏幕 | **未测**：需手动旋转（未通过修改系统旋转设置来模拟） |
| 5. 后台被杀后重开 | 通过：`am kill` 后进程消失，冷启动 219 ms，Facade 重新初始化并正常响应 |
| 6. 内存（30 轮，每轮强制双端 GC 后测量） | 通过：JNI 全局引用恒为 **37**；Java 存活约 2.6 MB 持平；托管堆 727→810 KiB（与应用内日志缓冲增长一致，上限 400 行）。未强制 GC 时 `dumpsys` 的 Java Heap 会随轮次上涨，属 GC 延迟，不应据此判断泄漏 |
| 回调线程 | 通过：logcat 显示回调在 Bridge 工作线程（非主线程） |

备注：
- 该 ROM 首次 USB 安装需在手机上手动确认“来历不明的应用”提示；覆盖安装未再弹出。
- 该 ROM 的 logcat 环形缓冲很快被冲掉；长压测请把 `adb logcat -s DshMobile:I` 持续写入文件，而不是事后 `logcat -d`。
- 发现并修复：根布局 `fitsSystemWindows` 覆盖了 16dp 内边距（已改为外层包裹）。

### 2026-11-09 · M2 运行时 · 同一设备

APK 110 MB（含 DSH 层 93 MB + android-side 层）；运行环境包 `dshapp-runtime-debian-arm64-0.1.0.zip`（202 MB）经 `adb push` 到 App 专属外部目录后导入。

| 步骤 | 结果 |
|---|---|
| `runtime.prepare`（全新安装） | 通过：APK 无完整分层包（预期），提示需导入 base/node |
| `runtime.importBundle` | 首次失败：`BUNDLE_IMPORT_FAILED … base/var/run: The source file doesn't exist`（上游 `swapRuntimeBody` 在 `runtime-current/` 不存在时的回退路径问题）。调用前创建该目录后通过：**12.0 s**，`runtime=True node=True`，DSH 0.2.1-rc.5 装配 |
| `sandbox.start` | 通过：7 ms |
| `dsh.start` | 通过：**18.5 s / 19.0 s** 到 READY，launch token 已解析，`http://127.0.0.1:3080` |
| `runtime.health` | 通过：process / port / webUi 全 true |
| `runtime.diagnostics`（guest 内真实执行） | `aarch64`、Debian `13.6`、Node `v24.19.0`、`dsh 0.2.1-rc.5` |
| 进程树 | App → 2×`libproot.so` → `bash`(keepalive) / `node`(DSH，RSS ≈ 385 MB)；App 进程 RSS ≈ 350 MB |
| 后台 90 s | 通过：进程全部存活，返回后健康与 `dsh --version` 正常；前台服务 `types=0x40000000`(specialUse) |
| 停止（dsh.stop → sandbox.stop） | 通过：仅剩 App 进程，无孤儿 |
| `am force-stop` | 通过：App UID 下零进程，无孤儿 |

备注：`libproot.so` 非 16 KB 页对齐（构建警告 XA0141），本机为 4 KB 页不受影响。

### 2026-11-09 · TASK-001 DSHBox 原生基线 · 同一设备

构建：DSHBox `v1.5.1`（`7c9d33c`，源码未改）`:app:assembleSystemWebViewRelease`，未配置 release keystore（回退 debug 签名）。
APK SHA-256 `a084dc5fb2559bea84d04caef8050b63f8006f23f7dc8dd639f08c41b9a2a392`，130.6 MB（官方 Release `dshbox-v1.5.1-system-release.apk` 同为 130.6 MB）。
构建前置：SDK `platforms;android-37.2`、`build-tools;36.0.0`（`tooling/install-android-sdk-components.ps1`）；`ndk-build` 不接受含空格的 NDK 路径，需在 clone 的 `local.properties` 设 `ndk.dir` 指向无空格路径（目录联接即可）。
运行环境：与 DSH Mobile 相同的 `dshapp-runtime-debian-arm64-0.1.0.zip`，由用户在 DSHBox「设置 → 离线导入运行环境包」手动导入并手动启动（DSHBox release 构建剥离了自身日志，导入/启动耗时无法从日志取得）。

| 指标 | DSHBox v1.5.1（基线） | DSH Mobile（本项目，M2） | 备注 |
|---|---|---|---|
| APK 大小 | 130.6 MB（含 Compose UI、Browser、Pilot 等） | 110.4 MB（含 DSH 层 93 MB） | 功能范围不同，仅作参考 |
| 冷启动 | 250 ms | 219–315 ms | `am start -W` TotalTime |
| 运行环境导入 | 成功；耗时未测（手动、无日志） | 12.0 s | |
| DSH 启动到就绪 | 成功（「DSH 就绪」）；耗时未测 | 18.5–19.0 s | |
| DSH 端口 | `GET /` → HTTP 401（launch token 鉴权生效） | 健康探测 webUi=true | 同一 DSH 0.2.1-rc.5 |
| 进程树 | App → 2×`libproot.so` → `bash` + `node` | 相同 | |
| 内存（ps RSS） | App 506 MB、node 282 MB（DSH 运行约 9 min） | App 349 MB、node 385 MB（DSH 运行约 1 min） | 采样时刻不同，不可直接比；DSHBox App PSS 161.7 MB（Java 堆 30.7 MB） |

结论：DSHBox 源码在本机可复现构建并在该设备上跑通同一运行层与 DSH 版本，作为 DSH Mobile 的对照基线成立。待补：两者在同一时间点、同一空闲时长下的 PSS 对比；DSHBox 导入/启动的精确计时（需 debug 构建或界面计时）。

### 2026-11-09 · TASK-005 DSH 模式探测 · 同一设备

方法：App 内「DSH 模式探测」→ `dsh.probe`，在 guest 内运行固定脚本 `dsh-probe.mjs`，以与 DSHBox 启动 web 完全相同的方式（`node --expose-internals --import /opt/dshbox/link-shim.mjs bin.js --profile <p>`，`DSH_HOME=/root/projects/.dsh`，cwd `/root/projects`）启动各 profile，做**真实协议交换**；不调用任何模型 API。运行时 web DSH 正在运行（shim=True，READY 21.1 s）。

| profile | 结果 | 检查（耗时） |
|---|---|---|
| web | PASS | dump-config 289 ms（44 627 B）；运行中 DSH `GET /` → 401 |
| headless | LIMITED | dump-config 271 ms；`--help` 7.9 s；一次性任务需模型凭据 |
| sdk | PASS | dump-config 476 ms；`initialize` 11.4 s → `{"serverInfo":{"name":"deepseek-harness-sdk-runtime","version":"0.0.1"}}`；`shutdown` → `{}`；exit 0（68 ms） |
| sdk-minimal | PASS | dump-config 455 ms；`initialize` 5.1 s（同上 serverInfo）；`shutdown` → `{}`；exit 0（29 ms） |
| acp | PASS | dump-config 519 ms；`initialize` 11.6 s → `protocolVersion 1`、`agentInfo deepseek-harness-acp`、`mcpCapabilities.http`、`sessionCapabilities{close,list,resume}`、`authMethods []`；stdin EOF → exit 0（101 ms） |
| plugin | FAIL | `plugin --profile web --help` 249 ms 通过；`plugin --profile web list` → exit 127「pnpm was not found; install pnpm and make it available on PATH」 |

发现并修复：TASK-004 初版未部署 DSHBox 的 Android 硬链接兼容垫片（`link-shim.mjs`、`libdshbox-link.so`，DSHBox 放在 app 资产里），DSH 虽能 READY，但依赖 `link()` 的会话持久化 / 写工具 / 附件 / 插件安装会 EPERM。现从子模块原样打包进 Bridge AAR 并在每次进程启动时部署到 `files/dshbox`（guest `/opt/dshbox`），已验证 DSH 命令行带 `--import /opt/dshbox/link-shim.mjs`。

未覆盖：需模型凭据的会话/提示/子代理/工具调用；`--from-default-profile`、`--patch` 覆盖；ACP `session/*`；pnpm 供给后的插件安装/卸载/回滚。

### 2026-11-09 · TASK-006 DSH WebUI · 同一设备

| 步骤 | 结果 |
|---|---|
| 启动 DSH | READY 15.0 s（shim=True） |
| 打开 DSH（WebView） | `WAITING_TOKEN → LOADING → LOADED`，约 0.5 s；官方 WebUI 首页（「探索未至之境 · 预览版」、工作区与模式选择）正常渲染 |
| launch token 泄漏检查 | App 进程 logcat 271 行中 `token=` 共 7 处：DSHBox 自身日志已脱敏为 `?token=***`，其余为 Android `ActivityRecord token=BinderProxy`；**意外出现 0 处** |
| 返回键（predictive back） | 无 WebView 历史时关闭页面，`DETACHED` |
| 停止 → 启动 DSH（新 token）→ 重开 | READY 18.5 s；重开后 `LOADED` 0.3 s，鉴权正常 |

未测：同一页面内 DSH 重启触发的就地重载、401 自动恢复（一次/每个 token 修订）、外链转系统浏览器、旋转保持、软键盘遮挡、文件选择与下载（当前明确提示不支持，TASK-007）。

### 2026-11-10 · TASK-007 文件 / SAF · 同一设备

| 步骤 | 结果 |
|---|---|
| 文件页：列出 `/root/projects` | 通过：仅 `.dsh`（🔒） |
| 进入 `.dsh` | 通过：`profiles`、`storages`、`.anonymous-user-id`、`.credentials.yaml` 均标记受保护 |
| 点 `.credentials.yaml` | 通过：提示「受保护（DSH_HOME，可能含凭据）：不能在此预览或导出」，无预览 / 导出入口 |
| SAF 导入（用户手动选择文件） | 通过：`apple-touch-icon.png`（14.5 KB）出现在工作区根目录 |
| SAF 导出到 Download（用户手动） | 通过：`/sdcard/Download/apple-touch-icon.png` 14 882 B，与工作区文件大小一致 |
| DSH WebUI 附件上传 | **未验证**：WebUI 打开约 18 s 期间 App 未收到文件选择请求（无 `webui file chooser` 日志）。DSH 首页提示「选择一个工作区开始」，附件入口很可能在选定工作区前不可用；原生侧选择器转交逻辑尚未在真机走到 |
| 运行环境包经 SAF 导入 | 未在真机执行（会替换现有运行环境） |

备注：涉及系统文件选择器的步骤均由用户手动完成（选择器会显示用户个人文件，自动化不介入）；App 内操作使用「前台确认」的受控点按。

### 2026-11-10 · TASK-008 终端 · 同一设备

| 步骤 | 结果 |
|---|---|
| 打开终端 | 首次失败：`TerminalView.setTypeface` 在 `setTextSize` 之前调用导致 NPE（渲染器尚未创建）；调整顺序后通过 |
| 会话 | `终端 1` 运行；提示符 `root@localhost:~#`（PRoot 以 uid 0 运行 guest） |
| 命令（用户手动输入） | `whoami` → `root`；`id` → `uid=0(root) gid=0(root) …`；`uname -m` → `aarch64`；`node --version` → `v24.19.0` |
| ^C（键栏） | 清除输入行通过；`sleep 30` 中断无法从屏幕确认（未见 `^C` 回显） |
| 关闭页面后重开 | 通过：同一会话与其输出仍在 |
| 「+」新建 / 切换 / 「×」关闭 | 通过：两个会话、切换活动会话、关闭后另一会话接管；进程表只剩一个 `libproot.so → bash` |
| 关闭最后一个会话 | 首版残留死会话画面且无标签；已改为隐藏终端并提示「没有打开的会话：点上方 “+” 新建」，「+」后恢复（已真机验证） |
| 首页「停止」 | 通过：先结束终端会话，再停沙箱；App 用户下沙箱进程 0 个 |
| guest passwd | 已写入 `dshmobile` 条目（`GuestUser`）；当前 PRoot 以 root 运行，提示符不依赖它 |

自动化限制：本机拼音输入法会拦截 `adb shell input` 注入的按键（转换为中文、吞掉空格与回车），因此命令输入由用户手动完成；App 内按钮使用前台确认的受控点按。

### 2026-11-10 · TASK-009 插件管理 · 同一设备

| 步骤 | 结果 |
|---|---|
| 部署离线 pnpm | 通过：APK 内 `corepack-pnpm-11.1.2.tar.zst`（DSHBox 随包件，sha256 `d9f9677d…`）校验后解包到 `guest-cache/corepack`；`dsh`、`pnpm` 包装脚本写入 `.dsh/dshbox/bin` |
| `dsh plugin --profile web list` | 首次失败：exit 127「pnpm was not found」——DSHBox sandbox-manager 只给 DSH 进程角色的 PATH 加包装目录，guest 命令角色没有；给插件命令与探测补同样的 PATH 前缀后通过（exit 0，`dsh-profile-web` 无依赖） |
| 安装 `is-number`（用户手动输入并确认警告） | 通过：exit 0（经网络从 npm registry 获取） |
| 卸载 `is-number` | 通过：exit 0；列表恢复为空 |

发现：DSH 0.2.1-rc.5 的 `dsh plugin add` **接受了非插件 npm 包**（`is-number` 无 `dsh.bundle` 声明）——CLI 路径不校验包是否为 DSH 插件，因此 App 侧安装前的第三方代码警告是必要的。

未测：真实 DSH 插件安装后在 DSH 重启时加载、失败回滚、安全模式、镜像源选择、并发与断网。

## TASK-010 手机操控（只读，Interlock Relay）真机记录 — 2026-11-10

设备：Honor PTP-AN10，Android 17 (API 37)，MagicOS。

- 无障碍未开启：`relay call ui.snapshot` → `E_GATE_SYSTEM_MISSING`（exit 4）。
- 用户手动开启「Relay 控制服务」后：doctor `usable 2/2`。
- 首版审批面 `uiInForeground()` 按进程前台判定 → 队列裁决走 PAGE（DSH Mobile 无页内卡），审批无人可见，`E_AWAITING_CONSENT`（surface `foreground`）。修复：`uiInForeground()` 恒为 false（SPI 语义是“页内卡可用”），审批一律走通知栏。
- 修复后 10:13：通知栏「允许一次」→ `ui.snapshot` `state: SUCCEEDED`、`effectState: verified`，67 个节点，`truncated: false`（exit 0）。
- 已知上游边缘：Relay 单次调用内联只等 8 s；在第 8 s 附近点下的「允许」被交给已返回的那一趟，重试会重新提问（第二次点允许后成功）。失败方向是“再问一次”，不会替用户放行；未改上游。
- 隐私提示：`ui.snapshot` 读取当前焦点窗口的全部可见文本（本次快照即为展开的通知栏，含通知内容）。
- 14:00 `ui_*` 门面全链路（同机）：重装后系统关闭了无障碍服务，用户手动重开 → `Relay: RUNNING · 信箱运行中`。
  「只读自检」经 MCP stdio 驱动门面：`initialize`（2025-06-18）→ 4 个工具 → `ui_list_devices` online →
  `ui_snapshot` s1（`com.android.systemui`，40/69 节点，truncated）→ `ui_get_element e6` 实时回读
  （`nativeId: android:id/app_name_text`，`enabled: true`），exit 0。「允许一次」两次、「本次会话允许」两次（按能力各一次）均通过。
- 「启动」DSH 0.2.1-rc.5（20.5 s READY）：命令行含 `--patch /root/projects/.dsh/dshbox/dsh-official-plugin/…`，
  `ps` 可见 `node /opt/dshbox-cache/dshmobile/ui-facade.mjs`（PID 11343）为 DSH 拉起的 MCP 服务。模型调用未验证（无凭据）。
- 安全发现：快照包含通知栏中本 App 的审批按钮（「允许一次」「本次会话允许」）。只读阶段无影响；开放点击前必须处理（ADR-0004 第 8 条）。

## TASK-011 浏览器自动化（DSHBox 浏览器栈，ADR-0005）真机记录 — 2026-11-10

设备同上（Honor PTP-AN10，Android 17）。

- 合并清单：新增唯一权限为 App 自有 `org.example.dshmobile.permission.BROWSER_INTERNAL`（`signature`）；
  `BrowserActivity` / `BrowserCommandService` / 上传与下载 Provider 均在 `:browser` 进程；`${applicationId}` 占位符正确展开。
  首版缺少 `:browser` 进程的 .NET 运行时提供者，加 `BrowserProcessAnchor` 后生成 `MonoRuntimeProvider_1`。
- 14:30 沙箱启动即 `BrowserTools: CLI listener ready generation=1`；「只读自检」→ `status: OK state=CLOSED`
  （`automationEnabled:false, owner:none`），exit 2（无会话，符合预期）。
- 「打开浏览器」→ `BrowserActivity` 获焦，`org.example.dshmobile:browser` 进程启动，无崩溃；返回按钮为「返回 DSH Mobile」（品牌覆盖生效）。
- 14:40 用户在浏览器加载百度并开启「AI 控制」：`status: OK state=IDLE session=1`（`automationEnabled:true, owner:ai`），
  `observe: OK`（`https://www.baidu.com/`，标题「百度一下」，正文 50 字，22 个元素），exit 0；浏览器在后台时也可读取。
- 14:42「安装 browser_* 工具」→ DSHBox `install.sh`：`Browser adapter assembled`，exit 0。停止→启动 DSH（14.6 s READY）后
  `--dump-config` web 中 `@local/browser-use` 出现 3 次，DSH 日志无该插件失败行，界面「已在当前 DSH 中加载」。
  模型实际调用 `browser_*` 未验证（无凭据）。
- 发现：浏览器空闲约 5 分钟自动关闭（`lastCloseReason: idle_timeout`），关闭后 `status` 仍带上一个 `sessionGeneration`；
  自检据此误判为 exit 4，已改为按 `state` 判断（单测覆盖；修正版尚未上真机）。
- 安装新包时 MagicOS 弹出系统安装确认，`adb install` 会一直等待，需人工确认。

## TASK-012 远程主机（SSH，ADR-0006）真机记录 — 2026-11-10

手机 Honor PTP-AN10 / Android 17；测试主机为本机 Windows PC（Windows OpenSSH Server，Node v26.13.0，DSH 0.2.1-rc.5），
经 `adb reverse tcp:2222 tcp:22` 连接。

- Keystore 生成 `ecdsa-sha2-nistp256` 密钥，报告硬件保护；指纹 `SHA256:QS7Vgv…31Lo` 与 PC `ssh-keygen -lf` 一致。
- 首版「保存主机」报 `unknown host id`（`org.json` 把 `"id": null` 读成 `"null"`），已修复并补单测。
- 16:12 首连 `HOST_KEY_UNKNOWN` → 用户核对确认 → `CONNECTED`；探测 node `v26.13.0`、dsh `0.2.1-rc.5`。
- 16:14 启动远端 DSH：`READY`，转发 `127.0.0.1:45747 → 3091`，经转发 `GET /` 401；PC 上 `dsh` 只监听 `127.0.0.1:3091`；
  App 进程日志中 `token=` 仅为 Android `BinderProxy` 记录，无 launch token。
- 停止：PC 3091 无监听（Windows OpenSSH 在通道关闭时结束 `dsh`）。首版停止核查报「未知」，已改为会话在时请求失败即「已不响应」（修正版未上真机）。
- 断线（`adb kill-server` 60 s）：`DISCONNECTED · UNREACHABLE`，token 作废；以固定指纹重连无需确认，再次启动得新转发端口 34137、HTTP 401、新 token。
- 隔离：远端 READY 时启动本机 DSH（20.5 s READY），两者并存；断开远端后 PC 端口释放，本机 DSH 不受影响。

## ADR-0007 迁移 PR-00/01/02 真机验收（C# 层 + 影子比对）— 2026-11-11

同一设备 Honor PTP-AN10 / Android 17（API 37）/ arm64-v8a；Release（full trim，CoreCLR）APK 124,130,141 B，分支
`migration/pr02-layer-extract@8a672b1`；`adb install -r` 覆盖安装（签名证书 SHA-256 `9F:E6:85:50…0C:36:13` 与已装版本一致，数据保留）。
原始日志摘录：[migration/parity/2026-11-11-PTP-AN10-A17-runtime.md](../migration/parity/2026-11-11-PTP-AN10-A17-runtime.md)。

- 冷启动 `am start -W` 三次 293 / 238 / 228 ms（迁移前基线 220–290 ms），PSS 128 MB；启动日志
  `runtime coordinator: impl=legacy-bridge journal=NotInstalled/Stopped gen=0`。
- bridge.info：`native=NATIVE_UNAVAILABLE`（尚未打包 `libdshmobile_native.so`，如实报告），journal 位于 `files/dshmobile-runtime/`。
- 导入锁定包 `dshapp-runtime-debian-arm64-0.1.0.zip`（设备侧 SHA-256 与 lock 一致）：旧实现导入成功，总计 9.2 s；
  影子 `runtime.bundle-verify`：**Agree**（三层 SHA-256 均通过，C# 415 ms）；
  影子 `runtime.layer-extract`：**Agree**（base 10,325 / node 5,782 / android-side 12 项与旧实现装好的目录逐项一致，含哨兵；5,583 ms）。
- 启动（`RuntimeCoordinator` → 旧桥）：sandbox 3 ms、DSH web 21.0 s `Ready`，gen=1，前台服务在、PRoot/node 进程在；
  再次点击启动 → `already running (harness Ready)`，无重复调用。
- 停止：`dsh.stop` → `sandbox.stop` → `Ready`，PRoot/node 进程 0，前台服务已释放。
- 运行中 `am force-stop`：PRoot/node 进程 0（无孤儿）；重启 App 后 journal 恢复为 `Failed/Stopped gen=2 last error RUNTIME_INTERRUPTED`；
  再启动 gen=3 `Ready`，停止后进程 0。
- 未在真机验证：沙箱启动失败 / 停止失败的清理分支（无法安全注入故障，仅单测覆盖）；Android 10；16 KB 页设备。

## ADR-0007 迁移 · C# LayerInstaller 真机验收 — 2026-11-11

同一设备；Release（full trim）APK 124,216,157 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-layer-install.md](../migration/parity/2026-11-11-PTP-AN10-A17-layer-install.md)。

- 长按「导入」选择 C# 导入器（旧导入器不运行）：校验 + 解包 + 换层 5.7 s；装后树校验 `runtime.layer-install` **Agree**（base 10,325 / node 5,782 / android-side 12 项）；
  旧桥 `runtime.prepare` 后状态 `runtime=True`（旧实现的 profile / 哨兵完整性检查接受 C# 安装结果），导入总计 10.9 s。
- 在 C# 安装的运行环境上启动：DSH web 17.0 s `Ready`，健康 `process/port/webUi` 全部 True；guest 内 Debian 13.6、Node v24.19.0、DSH 0.2.1-rc.5；停止后进程 0。
- 冷启动 261 ms；journal 跨进程恢复为 `Ready gen=3`。
- 未验证：全新设备首次安装、低磁盘、导入中途强杀、Android 10。

## ADR-0007 迁移 · C# PRoot guest exec（PR-03 首段）真机记录 — 2026-11-11

同一设备；Release（full trim）APK 125,195,101 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-guest-exec.md](../migration/parity/2026-11-11-PTP-AN10-A17-guest-exec.md)。

- C# 以 .NET `Process` 直接从 nativeLibraryDir 启动 `libproot.so`（W^X 允许的唯一位置）运行 4 条固定诊断命令：两次影子 `runtime.guest-exec` 均 **Agree**（aarch64 / 13.6 / v24.19.0 / 0.2.1-rc.5），0.45–0.49 s。
- 未完成：旧实现 guest 命令 argv 的 `/proc/<pid>/cmdline` 逐字节对照（抓取脚本两次得到 0 行，原因未查明）；
  DSH 运行中的长命进程 cmdline 对照（测试中发现手机前台被切到其它应用，停止了屏幕操作，有一次点击落在该应用上）。

## ADR-0007 迁移 · 进程监管观察 + 健康探测（PR-03 只读）真机记录 — 2026-11-11

同一设备；Release（`-t:Rebuild`）APK 125,502,301 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-supervisor-observe.md](../migration/parity/2026-11-11-PTP-AN10-A17-supervisor-observe.md)。

- 影子 `runtime.supervisor-observe` 停止态 / 运行态 / 停止后均 **Agree**：C# 自读 `/proc` 找到的沙箱 / DSH 根 PID 与 `ps` 一致，自测 WebUI 得 HTTP 401（视为就绪）与旧实现健康一致；两轮（HttpClient 版、socket 版）结果相同。
- **构建坑**：增量 Release 构建在程序集集合变化后产出启动即崩的 APK（`Invalid compressed assembly descriptor index 50`），需 `dotnet build -c Release -t:Rebuild`；CI 干净构建不受影响。
- 运行中长命 PRoot 的完整 argv 已存为 `tests/TestVectors/device-proot-cmdlines.json`。

## ADR-0007 迁移 · 沙箱 / DSH 启动 argv（PR-03 只读）真机记录 — 2026-11-11

同一设备；Release（`-t:Rebuild`）APK 125,518,685 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-launcher-argv.md](../migration/parity/2026-11-11-PTP-AN10-A17-launcher-argv.md)。

- 影子 `runtime.launcher-argv` **Agree**：C# 构建的沙箱 keepalive（14 个参数）与 DSH web（22 个参数，首次启动形态）argv 与旧实现进程的 `/proc/<pid>/cmdline` 逐 token 一致。
- 同轮 `supervisor-observe`、`guest-exec` 均 Agree；停止后无残留进程、无崩溃。

## ADR-0007 里程碑 · C# 宿主启动 → WebUI → 停止（无 DSHBox 代码）真机记录 — 2026-11-11

同一设备；Release（`-t:Rebuild`）APK 126,051,165 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-csharp-host-milestone.md](../migration/parity/2026-11-11-PTP-AN10-A17-csharp-host-milestone.md)。

- 长按「启动 DSH」选 C# 宿主：C# 直接启动沙箱 / DSH PRoot，从 DSH stdout 取 launch token，**10.1 s READY**（旧实现 17–21 s）；C# WebView 加载官方 WebUI（`LOADED /`，截图确认已登录首页，无 401）。
- logcat 全量检查：DSH launch token 0 处泄漏（唯一相关行为 `token=***`；其余 `token=` 均为 Android binder 句柄）。
- 停止：无残留进程、无崩溃；运行中强杀：无孤儿，journal 恢复为 `RUNTIME_INTERRUPTED`；之后默认旧实现启停照常。
- 结合同日 C# LayerInstaller 导入，迁移方案第 12 节「导入 → 启动 PRoot → READY → WebUI → 停止无孤儿」全链路均已有 C# 实现的真机证据（仍为开发者可选路径）。

## ADR-0007 迁移 · 插件列表（PR-05 只读影子）真机记录 — 2026-11-11

同一设备；Release（`-t:Rebuild`）APK 126,092,125 B。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-plugins-list.md](../migration/parity/2026-11-11-PTP-AN10-A17-plugins-list.md)。

- 影子 `plugins.list` **Agree**：旧桥 `plugin.list` 与 C# PRoot 启动器跑的 `dsh plugin list --json --depth 0` 均 exit 0、均解析为 JSON 列表、内容一致（0 个插件；C# 1.0 s）。
- 首次运行发现旧判定会把两边都不可解析误判为两个空列表一致，已改为要求两边都是 JSON 列表后复测。
- 无崩溃、logcat 无 launch token；本机无第三方插件，非空列表只有单测证据。

## ADR-0007 迁移 · C# 插件安装守卫 / 卸载核对（PR-05）真机记录 — 2026-11-11

同一设备；Release（`-t:Rebuild`）APK。原始日志：[migration/parity/2026-11-11-PTP-AN10-A17-plugin-install-guard.md](../migration/parity/2026-11-11-PTP-AN10-A17-plugin-install-guard.md)。

- C# 卸载 `is-number`：1.0 s，核对依赖已移除（`still-present=False`）。
- C# 安装 `is-number`：DSH 0.2.1-rc.5 只警告「declares no dsh.bundle」并退出 0；守卫差集 `[is-number]` → 无 `dsh` 字段 → `INSTALL_VERIFY_FAILED`，`dsh plugin remove` 回收，列表回到装前（旧桥与 C# 影子一致）。
- 停止后无残留进程、无崩溃、无 token 泄漏；第三方输入法会吞 `adb input text` 字符，自动化输入改用按键事件。

## ADR-0007 迁移 · C# 终端界面（PR-06 第二段）真机记录 — 2026-11-12

开发者入口：长按「终端」→「C# 终端」；默认仍为旧 TerminalView。详见 [证据](../migration/parity/2026-11-12-PTP-AN10-A17-csharp-terminal-ui.md)。

| 项 | 结果 |
|---|---|
| 键盘输入 | 首版用 xterm.js 自带 textarea：IME 下吞字 / 重复（`stty size` → `sttttty s ssizz`）；改为原生 `TYPE_NULL` 输入视图后通过 |
| 多会话 | 双会话 `/dev/pts/0`、`/dev/pts/1`；切换重放历史；离开页面会话保留 |
| resize / ^C / 大量输出 | 键盘弹出后 `15 41`；^C → `$?=130`；`seq 1 100000` 0.085 s，之后输入正常 |
| 关闭 / 强杀 | 「关闭」后无 `proot` / `bash`（PRoot 未在 SIGHUP 宽限期退出，升级 SIGKILL）；`force-stop` 无孤儿 |
| 首页「停止」 | 首版在沙箱未启动时漏关 C# 会话（`stop: nothing running`）；修正并加单测后复测通过（2 个会话关闭，无残留） |

## ADR-0007 迁移 · `ui_*` 工具注册与状态（PR-07 第一段，只读影子）真机记录 — 2026-11-12

「手机操控（实验）」页的 `relay.status` / `relay.start` / `relay.stop` 之后，C# 读同一 patch 层与无障碍设置：三次 `automation.ui-tools` **Agree**；开启后层文件与 C# 渲染逐字节一致。无障碍「已开启」与冲突分支只有向量证据。详见 [证据](../migration/parity/2026-11-12-PTP-AN10-A17-ui-tools.md)。
