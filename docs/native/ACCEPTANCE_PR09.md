# PR-09 默认入口切换与彻底清理 WebView 验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（`N-09-01` ～ `N-09-04`）
> 分支：`feat/native-pr09-default-entry-remove-webview`（基于 `main` @ `77e443a`，已含 PR #83）
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 无真机证据不勾 `[x]`、规则 11 外链与安全约束不降级）。
> 真机状态：**已真机验证（2026-11-24，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37）**——从 PR-08 旧 APK 覆盖安装 PR-09 Release（净构建 `AC41C634…15DB1`，与设备 `base.apk` 哈希一致）：首屏直接进入「会话 | 工作区 | 更多」三栏、无 Fragment 崩溃，旧任务栈 `savedInstanceState` 中的 `home` 被丢弃；工作区（`/root/projects`）、凭据与 5 个可恢复会话均保留；停止/启动、新建与恢复会话、SAF 选择器、外链二次确认并交系统浏览器、会话页返回键退出均通过。**产物核查发现增量构建残留**（F27）：12:48 的增量 APK 仍含 `DshWebActivity` / `CSharpWebUiController` 的 Java 调用包装字符串，全量净构建后归零。T01–T12 总表已归档 [ACCEPTANCE_MATRIX.md](ACCEPTANCE_MATRIX.md)。

---

## 1. 本轮交付清单（`N-09-01` ～ `N-09-04`）

### 1.1 `N-09-01`｜切换默认主入口至原生会话

- **`MainActivity` 与三栏导航（`会话 | 工作区 | 更多` + 600dp `NavigationRailView`）**：
  - [bottom_nav.xml](../../src/DshMobile.Android/Resources/menu/bottom_nav.xml)：移除 `nav_home`，主导航收口为 `nav_chats`（「会话」）、`nav_files`（「工作区」）、`nav_more`（「更多」）。
  - [MainActivity.cs](../../src/DshMobile.Android/MainActivity.cs)：默认选定 `Resource.Id.nav_chats`（`ChatsFragment`），回退键返回 `nav_chats` 后退出应用；保留 `CreateWorkspaceIntent` 从聊天跳转 `nav_files` 的深度导航。
  - **覆盖安装与恢复态兼容**：沿用 `TabKey`（`"chats"` / `"files"` / `"more"`）与 `FragmentManager` tag 机制；当旧版 `savedInstanceState` 含有已删除的 `"home"` 时，`SavedTab()` 返回 `null` 并丢弃陈旧 Fragment 栈，同时在 `Show()` 中显式清理可能残留的 `"home"` tag，确保不会触发旧 Fragment 重建崩溃。
- **合并原首页生命周期能力进 `ChatsFragment` 与 `RuntimeActivity`**：
  - [HomeStatus.cs](../../src/DshMobile.Application/Ui/HomeStatus.cs)：移除 `HomePhase.WaitingForWebUi` 与 `HomeAction.OpenDsh`，替换为 `HomePhase.WaitingForDsh` 与 `HomeAction.OpenChat`。
  - [fragment_chats.xml](../../src/DshMobile.Android/Resources/layout/fragment_chats.xml) 与 [ChatsFragment.cs](../../src/DshMobile.Android/Chats/ChatsFragment.cs)：保留已就绪时简洁的会话列表体验；在未安装 / 停止 / 启动 / 失败 / 处理中时展开 Material 3 宿主卡片，支持 `OnlineInstall`（签名在线下载与确认弹窗）、`ImportRuntime` / `ImportDsh`（SAF 选择与回传处理）、`Start`（含 Android 17 `LocalNetworkAccess` 门禁提示）、`Stop`（并在就绪态工具栏提供 `停止 DSH` 菜单）、`CancelUpdate`，以及 `chats_failure_card` 与 `chats_update_banner_card`。
  - [RuntimeActivity.cs](../../src/DshMobile.Android/RuntimeUi/RuntimeActivity.cs)：同步切换 `HomePhase.WaitingForDsh`，并在「维护」区补充直接启停 DSH 入口。

### 1.2 `N-09-02`｜彻底删除业务 WebView 栈

- **已删除源码、布局与陈旧测试**：
  - `src/DshMobile.Android/DshWebActivity.cs`
  - `src/DshMobile.Android/WebUi/CSharpWebUiController.cs`（连同 `WebUi/` 目录）
  - `src/DshMobile.Android/Resources/layout/activity_web.xml`
  - `src/DshMobile.Android/Home/HomeFragment.cs`（连同 `Home/` 目录）
  - `src/DshMobile.Android/Resources/layout/fragment_home.xml`
  - `src/DshMobile.Core/WebUi/DshOriginPolicy.cs`、`src/DshMobile.Core/WebUi/WebUiPolicy.cs`（连同 `Core/WebUi/` 目录）
  - `tests/DshMobile.Core.Tests/WebViewPolicyParityTests.cs`
- **保留与重构**：
  - 将仍被 `DshHost`（`IDshTokenSource.TokenRevision`）使用的 `DshTokenRevisionTracker` 迁入 [DshTokenRevisionTracker.cs](../../src/DshMobile.Runtime/Hosting/DshTokenRevisionTracker.cs)，并新增 [TokenRevisionTrackerTests.cs](../../tests/DshMobile.Runtime.Tests/TokenRevisionTrackerTests.cs) 继续按 `tests/TestVectors/webview-policy.json#tokenTracker` 锁定代次追踪契约。
- **清单与资源清理**：
  - [strings.xml](../../src/DshMobile.Android/Resources/values/strings.xml) 清除 `dsh_title`、`web_*`、`nav_home`、`home_action_open`、`home_action_open_native_chat`、`home_shortcut_*`、`home_title_waiting`、`home_detail_waiting` 等废止文案。
  - [AndroidManifest.xml](../../src/DshMobile.Android/AndroidManifest.xml) 与 [DshMobile.Android.csproj](../../src/DshMobile.Android/DshMobile.Android.csproj) 核对无冗余权限及 WebKit 依赖；外部 `http(s)` 链接由 [ChatMarkdownBinder.cs](../../src/DshMobile.Android/Chat/ChatMarkdownBinder.cs) 弹出 `MaterialAlertDialogBuilder` 二次确认后交由受控系统浏览器打开。

### 1.3 `N-09-03`｜启用 `NoBusinessWebViewGateTests` 永久 CI 门禁与发布前净构建产物核查

- 新增 [NoBusinessWebViewGateTests.cs](../../tests/DshMobile.Agent.Tests/Quality/NoBusinessWebViewGateTests.cs)：
  - 扫描 `src/**/*.cs`（剥离注释），断言零 `Android.Webkit.WebView`、`WebViewClient`、`WebChromeClient`、`WebView`、`DshWebActivity`、`CSharpWebUiController`、`activity_web`、`WebUiPolicy`、`DshOriginPolicy`、`WaitingForWebUi`、`HomeAction.OpenDsh`、`Xamarin.AndroidX.WebKit`；对引入 `Android.Webkit` 的文件（`AndroidDirectPlatform.cs` / `DocumentPicker.cs`）强制限定仅允许使用 `MimeTypeMap`，禁止 `CookieManager` / `WebStorage` / `WebSettings`。
  - 扫描全仓 `src/**/*.csproj`、`AndroidManifest.xml` 与 `Resources/**/*.xml`（剥离注释），断言零 `<WebView` / `WebView` / `WebKit` / `nav_home` / `home_open_native_chat` / `activity_web`，并断言已删除文件不再复现。
  - **APK 二进制产物核查（F27 固化防线）**：`FindApkViolations` 直接读取 APK ZIP 中的 `AndroidManifest.xml`、`resources.arsc`（同时扫描 UTF-8/ASCII 与 UTF-16LE）、ZIP 路径以及每个 `classes*.dex` 的 DEX `string_ids` 表：拦截业务/旧版符号及未在 `Mono.Android` 平台绑定/`MimeTypeMap` 白名单内的任何 `WebView`/`WebKit` 字样；内置合成 DEX/APK 单测（`ApkArtifactVerifier_AcceptsCleanPlatformBindings_AndRejectsStaleOrBusinessWebView`）并通过环境变量 `DSHM_VERIFY_APK_PATH` 对真实 Release APK 执行门禁。
- 固化发布前净构建脚本与 CI 工作流：
  - 新增 [build-clean-release-apk.ps1](../../tooling/build-clean-release-apk.ps1)：先清理 `src/DshMobile.Android` 的 `obj/<Config>` 与 `bin/<Config>`（消除增量 DEX 包装残留），再构建签名 APK 并注入 `DSHM_VERIFY_APK_PATH` 执行 `NoBusinessWebViewGateTests`。
  - [.github/workflows/android-dotnet.yml](../../.github/workflows/android-dotnet.yml) 的「Clean-build app and verify APK has zero business WebView」步骤改为直接调用 `./tooling/build-clean-release-apk.ps1 -NoBuildTests`，在上传 APK 制品前强制通过净构建与零业务 WebView 产物门禁。

### 1.4 `N-09-04`｜仓库文档同步

- 已同步更新：[ARCHITECTURE.md](../ARCHITECTURE.md)、[FEATURES.md](../FEATURES.md)、[SECURITY.md](../SECURITY.md)、[COMPATIBILITY.md](../COMPATIBILITY.md)、[README.md](../../README.md)、[MIGRATION.md](../MIGRATION.md) 及 [DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)。

---

## 2. 桌面测试与 Release 构建基线（已实测通过）

> 以下结果均于最后一次源码/测试定稿后执行并观察通过：

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests --no-restore` | **95 通过 / 0 失败 / 0 跳过（总计 95）**（已按 `N-09-02` 移除废弃 `WebViewPolicyParityTests` 14 项） |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests --no-restore` | **880 通过 / 0 失败 / 2 跳过（总计 882）**（含新增 `TokenRevisionTrackerTests`，2 项为既有 Windows 平台跳过） |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests --no-restore` | **151 通过 / 0 失败 / 0 跳过（总计 151）**（含更新后的 `HomeStatusTests`） |
| Agent 单测（含 7 组 Quality/Perf 门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **100 通过 / 0 失败 / 0 跳过（总计 100）**（含 `NoBusinessWebViewGateTests` 4 项：源码、XML/Manifest、合成 APK 与 `DSHM_VERIFY_APK_PATH` 门禁） |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败（退出码 0）** |
| Release 工具链 Node 测试 | `node tooling/run-node-tests.mjs tests/tooling` | **91 通过 / 0 失败（退出码 0）** |
| 发布前净构建 + APK 门禁 | `./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 73 警告**（清理 `obj/Release`、`bin/Release` 后净构建 43 685 610 B 签名 APK，并自动通过 `NoBusinessWebViewGateTests` 4 项） |

---

## 3. 真机终验清单（总表已归档 [ACCEPTANCE_MATRIX.md](ACCEPTANCE_MATRIX.md)）

| 编号 | 真机验收要点 | 当前状态 |
|---|---|---|
| `PR09-D01` | 从含旧版 `nav_home` 状态的 APK 覆盖安装新 Release APK：首屏直接进入「会话 | 工作区 | 更多」三栏且无 Fragment 崩溃，工作区、凭据与可恢复会话无丢失（T10） | **通过（2026-11-24）**：旧包（PR-08，四栏 `首页/对话/文件/更多`）在「首页」标签下后台后被覆盖安装；新版冷启首屏为「会话」，三栏导航、无崩溃；启动后 5 个历史会话、`/root/projects` 文件与 API 凭据均在，发送 `PR09_OK` 回执成功 |
| `PR09-D02` | 新首屏「会话」页冷启动 / 未安装 / 已停止状态下触发在线安装、SAF 导入、LAN 权限门禁与启动/停止 DSH、新建与恢复原生对话（T01–T02） | **部分通过（2026-11-24）**：冷启动直达「会话」；未启动态显示宿主卡片并可「启动 DSH」（11.8 s 就绪）；工具栏「停止 DSH」→ 卡片回到未启动 → 再启动成功；「新建会话」发消息与历史会话恢复均通过；工作区「导入」拉起 `DocumentsUI` SAF 选择器。**未覆盖**：在线安装（GitHub 清单不可达）、运行层未安装态、LAN 权限首次门禁（此前已授权） |
| `PR09-D03` | 最终 Release APK 产物（`classes.dex` / 清单 / 资源）零业务 `WebView` 核查，外链点击确认打开系统浏览器，与 T01–T12 总验收归档（`ACCEPTANCE_MATRIX.md`） | **通过（2026-11-24）**：净构建 APK 的 `classes*.dex` 中 `DshWebActivity` / `CSharpWebUiController` / `activity_web` / `WebUiPolicy` / `DshOriginPolicy` 命中均为 0，Manifest 与资源零 `WebView`/`WebKit`；残留 14 处 `WebView` 字样均属 `Mono.Android` 平台绑定（`Android.Webkit.WebView` 接口实现类）。外链点击 → 「在浏览器中打开外部链接？」→ 打开 `org.lineageos.jelly`，返回聊天页 |

---

## 4. Android 17 真机验收（2026-11-24 12:56–13:10）

- 设备：Xiaomi M2007J1SC（LineageOS 24.0，Android 17 / API 37，arm64，非 root）。
- 旧包：PR-08 Release `C0A35590B3EF5B9D266BEFE891C955C767AA8BE49996953C714EE07532B430E8`（四栏导航，安装于 09:16）。
- 新包：PR-09 净构建 Release `AC41C634CD78EB23C1223D575DE286F8AD66ED95956263208C15CC405FF15DB1`（43 685 610 B，13:03），设备 `base.apk` 拉回后 SHA-256 与新包一致。
- 证据：`artifacts/pr09/`（升级前后截图、`manifest-tree.txt`、`resources.txt`）。

### 4.1 覆盖安装与默认入口（PR09-D01，通过）

1. 旧包启动 → 首页「DSH 已就绪」→ 记录 5 个会话 → 切到旧「首页」标签 → 按 Home 让任务停下（ActivityRecord 保存 `tab=home`）→ `adb install -r` 覆盖安装新包（旧进程被系统杀死、任务 t53 保留）。
2. 新包启动：`topResumedActivity` 为 `MainActivity`，首屏标题「会话」，底部仅「会话 / 工作区 / 更多」三栏，无 Fragment 崩溃、无 `AndroidRuntime` 异常；旧 `home` 标签被 `SavedTab()` 丢弃。
3. 数据保留（T10）：点「启动 DSH」11.8 s 就绪后会话列表仍为 5 条（含 09:42 / 09:50 创建的会话），打开历史会话可见完整正文；新建会话发送 `Reply with exactly PR09_OK` 收到 `PR09_OK`，说明 Keystore 凭据与 API Key 均在；工作区 `/root/projects` 下 `pr06-repo`、`print-statements-demo.md` 与受保护的 `.dsh` 仍在。

### 4.2 新首屏能力（PR09-D02，部分通过）

- 冷启动 / 已停止：会话页宿主卡片显示「DSH 未启动 / DSH 尚未启动，点击下方按钮启动或安装运行环境后再创建实时会话」+「启动 DSH」。
- 停止：工具栏「更多选项 → 停止 DSH」→ 卡片回到未启动态（截图 `artifacts/pr09/chats-stopped.png`）；再启动回到就绪。
- 新建与恢复：FAB「新建会话」发消息成功；历史会话「打开」恢复全文。
- SAF：工作区「导入」拉起 `com.android.documentsui/.picker.PickActivity`，返回后工作区不变。
- 返回键：会话页按 Back 直接回到 Launcher（不再停在旧首页）。
- 未覆盖：在线安装（GitHub 清单不可达，与 PR-08 D04 同因）、运行层未安装态、LAN 权限首次门禁（ADR-0032 一次性提示此前已授权，无法重复触发）。

### 4.3 APK 产物核查（PR09-D03，通过；含 F27）

| 检查 | 12:48 增量构建 `D6992D82…E28D6` | 13:03 净构建 `AC41C634…15DB1` |
|---|---|---|
| `classes*.dex` 中 `DshWebActivity` | 6 | **0** |
| `classes*.dex` 中 `CSharpWebUiController` | 9 | **0** |
| `activity_web` / `WebUiPolicy` / `DshOriginPolicy` | 0 | **0** |
| Manifest / `resources.arsc` 中 `WebView` / `WebKit` | 0 | **0** |
| 残留 `WebView` 字样 | 29（含业务类） | 14，均为 `Mono.Android` 的 `Android.Webkit.WebView` 绑定与接口实现类 |

**F27（构建卫生，已固化防线）**：增量构建沿用了被删除类型的 Java 调用包装/类型映射，导致「已删除的 WebView 页面」仍出现在 `classes.dex`；`dotnet build` 前清理 `obj/Release`、`bin/Release` 的净构建后归零。现已通过 [tooling/build-clean-release-apk.ps1](../../tooling/build-clean-release-apk.ps1)、[android-dotnet.yml](../../.github/workflows/android-dotnet.yml) 与 [NoBusinessWebViewGateTests.cs](../../tests/DshMobile.Agent.Tests/Quality/NoBusinessWebViewGateTests.cs)（`DSHM_VERIFY_APK_PATH` DEX/Manifest/资源扫描）将净构建 + APK 产物核查固化为 CI 与发布前强制门禁。

### 4.4 外链（PR09-D03）

聊天正文 `https://example.com` 点击后弹出 `在浏览器中打开外部链接？` / `https://example.com/` 与「取消 / 打开」；点「打开」后 `topResumedActivity` 变为 `org.lineageos.jelly/.MainActivity`，Back 返回 `ChatActivity` 且对话内容不变——符合「二次确认 + 受控系统浏览器」边界，未引入 WebView。

### 4.5 本轮限制

- 未做全新安装（T01 首次引导）与运行层未安装态：会清空设备数据，超出本轮范围。
- 在线更新链路仍受 GitHub 不可达限制（PR-08 D04 同因），`T12` 的「在线更新回滚」部分继续待环境。
- 旧版「首页」标签的 `savedInstanceState` 兼容路径是通过「旧包停在首页 → 覆盖安装 → 冷启」复现的；未覆盖系统在低内存下回收任务后重建的极端组合。

最后更新：2026-11-24

