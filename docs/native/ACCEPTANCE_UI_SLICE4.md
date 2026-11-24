# UI Slice 4 验收记录：更多页与 6 大二级页全面 XML 化

> 对应方案：[UI_LAYOUT_OPTIMIZATION_PLAN.md](UI_LAYOUT_OPTIMIZATION_PLAN.md) §5.5（更多与 6 个二级页全面 XML 重构）与 §6 Slice 4。
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12；全 XML 化构建（清零除 `ChatMarkdownBinder` 外的所有命令式界面代码）、不引入 Compose / 新 NuGet 包、严格保留 `FLAG_SECURE` / `filterTouchesWhenObscured` / SSH 指纹逐字核对 / Keystore 不可导出私钥与 API Key 零明文回显边界。
> 真机状态：**已真机验证（2026-11-24，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——净构建 Release `04DBF481…CC97`（43 987 104 B）覆装，设备 `base.apk` SHA-256 一致。

---

## 1. 本轮交付清单

### 1.1 纯托管格式化器与静态门禁（`src/DshMobile.Agent/` & `tests/DshMobile.Agent.Tests/`）
- 新增 [PluginDisplayFormatter.cs](../../src/DshMobile.Agent/Presentation/PluginDisplayFormatter.cs)：解决插件卡片「长中文描述占满主标题、包名重复拼接」问题——优先展示可读短模块名为主标题，将说明型 `DisplayName` 下沉至副描述（最多 2 行），并对 `ModuleName · PackageName` 去重与规范化状态标签。
- 新增 [PluginDisplayFormatterTests.cs](../../tests/DshMobile.Agent.Tests/Presentation/PluginDisplayFormatterTests.cs)（2 项单测验证短标题、非冗余描述与元信息去重）。
- 新增 [XmlFirstUiGateTests.cs](../../tests/DshMobile.Agent.Tests/Quality/XmlFirstUiGateTests.cs)（2 项永久静态回退门禁）：
  - 扫描 `src/DshMobile.Android/*.cs`，强制除 [ChatMarkdownBinder.cs](../../src/DshMobile.Android/Chats/ChatMarkdownBinder.cs) 外零命令式控件/容器构造函数（`new TextView` / `MaterialButton` / `MaterialSwitch` / `LinearLayout` / `FrameLayout` 等）；
  - 禁止残留旧版动态行构造方法及废弃布局文件名（`fragment_list`、`item_row`、`item_section_header`）；
  - 强制锁定 [view_relay_approval_card.xml](../../src/DshMobile.Android/Resources/layout/view_relay_approval_card.xml) 与 [activity_relay_approval_confirm.xml](../../src/DshMobile.Android/Resources/layout/activity_relay_approval_confirm.xml) 中 `android:filterTouchesWhenObscured="true"` 防遮挡点击安全属性。

### 1.2 「更多」页、6 大二级页与共享底座全面 XML 化（`src/DshMobile.Android/`）
- 清理废弃通用容器：删除 [fragment_list.xml](../../src/DshMobile.Android/Resources/layout/fragment_list.xml)、`item_row.xml`、`item_section_header.xml`；重构 [Rows.cs](../../src/DshMobile.Android/Ui/Rows.cs)，彻底移除 `Add`/`Header`/`Card`/`MonoCard`/`Row` 等命令式拼图方法，仅保留 XML 绑定辅助（`BindActionRow`/`BindNavRow`/`BindSwitchRow`/`BindMetricTile`/`SetBadge`/`ShowScrollableTextDialog`）。
- **更多页**：新建 [fragment_more.xml](../../src/DshMobile.Android/Resources/layout/fragment_more.xml) 并重构 [MoreFragment.cs](../../src/DshMobile.Android/More/MoreFragment.cs)——顶部加入实时「运行与连接概览卡」（状态徽章 + 版本 + 远程状态 + 一键「启动 DSH / 停止」），下方分「功能 / 运行环境 / 应用」3 张 Material 3 卡片组与真实 `MaterialSwitch`。
- **模型与凭据配置**：重写 [fragment_credentials.xml](../../src/DshMobile.Android/Resources/layout/fragment_credentials.xml)、[item_credential_entry.xml](../../src/DshMobile.Android/Resources/layout/item_credential_entry.xml) 并新建 [credentials_toolbar.xml](../../src/DshMobile.Android/Resources/menu/credentials_toolbar.xml)，重构 [CredentialsFragment.cs](../../src/DshMobile.Android/Credentials/CredentialsFragment.cs) 与 [CredentialsAdapter.cs](../../src/DshMobile.Android/Credentials/CredentialsAdapter.cs)——刷新操作移入顶栏，底部移除双栏遮挡并改用右下角 `ExtendedFloatingActionButton`，已配置项显示脱敏掩码卡 + 清除/更新、未配置项折叠为单行紧凑配置卡。
- **插件目录**：重写 [fragment_plugins.xml](../../src/DshMobile.Android/Resources/layout/fragment_plugins.xml)、[item_plugin_entry.xml](../../src/DshMobile.Android/Resources/layout/item_plugin_entry.xml) 并新建 [plugins_toolbar.xml](../../src/DshMobile.Android/Resources/menu/plugins_toolbar.xml)，重构 [PluginsFragment.cs](../../src/DshMobile.Android/Plugins/PluginsFragment.cs) 与 [PluginsAdapter.cs](../../src/DshMobile.Android/Plugins/PluginsAdapter.cs)——接入 `view_search_pill` 与 4 枚状态筛选 `FilterChip`（全部/可管理/已启用/核心保护），核心保护模块隐藏开关、刷新上移顶栏、安装改为 `ExtendedFAB`。
- **运行环境**：新建 [activity_runtime.xml](../../src/DshMobile.Android/Resources/layout/activity_runtime.xml) 与 [item_runtime_update_offer.xml](../../src/DshMobile.Android/Resources/layout/item_runtime_update_offer.xml)，重构 [RuntimeActivity.cs](../../src/DshMobile.Android/RuntimeUi/RuntimeActivity.cs)——压缩原 5 屏平铺为 4 张结构化卡（2×2 指标仪表盘 + 离线维护与运行态导入停止拦截弹窗 + 真实自动检查开关的在线更新卡 + 32 槽位 `LinearProgressIndicator` 系统治理卡）。
- **手机操控与安全审批**：新建 [activity_automation.xml](../../src/DshMobile.Android/Resources/layout/activity_automation.xml)、[view_relay_approval_card.xml](../../src/DshMobile.Android/Resources/layout/view_relay_approval_card.xml)、[activity_relay_approval_confirm.xml](../../src/DshMobile.Android/Resources/layout/activity_relay_approval_confirm.xml)，重构 [AutomationActivity.cs](../../src/DshMobile.Android/Automation/AutomationActivity.cs)、[ApprovalCardView.cs](../../src/DshMobile.Android/Automation/Relay/ApprovalCardView.cs)、[RelayApprovalConfirmActivity.cs](../../src/DshMobile.Android/Automation/RelayApprovalConfirmActivity.cs)——可折叠说明卡、前置权限彩色状态胶囊清单及限高可滚动自检日志；审批卡全 XML 化并锁定安全属性。
- **远程主机（SSH）**：重写 [activity_remote.xml](../../src/DshMobile.Android/Resources/layout/activity_remote.xml) 并重构 [RemoteActivity.cs](../../src/DshMobile.Android/Remote/RemoteActivity.cs)——连接状态行紧贴右侧单行按钮，表单压缩为 3 行 `TextInputLayout`（名称、地址+SSH端口、用户名+远端端口），首屏底部直接露出 `Keystore` 不可导出公钥及复制按钮。
- **开发者选项与启动实验**：新建 [activity_developer.xml](../../src/DshMobile.Android/Resources/layout/activity_developer.xml) 与 [activity_startup_experiment.xml](../../src/DshMobile.Android/Resources/layout/activity_startup_experiment.xml)，重构 [DeveloperActivity.cs](../../src/DshMobile.Android/Developer/DeveloperActivity.cs) 与 [StartupExperimentActivity.cs](../../src/DshMobile.Android/Developer/StartupExperimentActivity.cs)——顶部诊断工具卡 + 内嵌 `320dp` 等宽日志预览（全屏/复制/清空按钮就近置于卡片头部）+ 底部独立危险色关闭开关卡。
- 新增本地化文案于 [strings.xml](../../src/DshMobile.Android/Resources/values/strings.xml)。

---

## 2. 桌面与构建基线（最后一次源码定稿后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **96 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **151 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | 并行首轮出现 1 例时序抖动（`RelayControlParityTests` `duplicate-ask` 超时）；独立重跑 **880 通过 / 0 失败 / 2 跳过（总计 882）** |
| Agent 单测（含 XML / 安全门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **108 通过 / 0 失败 / 0 跳过**（含 `XmlFirstUiGateTests` 2/2、`PluginDisplayFormatterTests` 2/2） |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `pwsh ./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 110 警告**，净构建 43 987 104 B 签名 APK 并通过 `NoBusinessWebViewGateTests` 4/4 |

**APK**：`io.github.maiji1990.dshmobile` Release，SHA-256 `04DBF481F50DF7A34523DAE3ABF46D340315010E81653124EF463FBBA1A4CC97`；设备 `/data/app/…/base.apk` 实测哈希完全一致。

---

## 3. 真机验收（2026-11-24，Android 17）

证据目录：`artifacts/screenshots/slice4-2026-11-24/`（18 张 + `index.html` + `manifest.tsv`）。

### 3.1 验收步骤与观察结论
1. **更多页静/运行态、开源许可弹窗与开发者工具**：覆装启动进入「更多」，核验未启动概览卡与分组卡片（`01`）、底部预热真实开关与开发者选项入口（`02`）、开源许可 M3 弹窗（`03`）、开发者工具诊断卡片与等宽日志预览（`09`）及全屏日志弹窗（`10`）。点击「启动 DSH」约 18s 后实时转为绿色「运行中 + 停止」概览态（`11`）。
2. **运行环境 4 卡重构（~2.2 屏）**：进入「运行环境」核验 2×2 指标仪表盘（运行层已安装 / 链接兼容层正常 / 102.7 GB / 上次操作暂无）与统一图标后的离线维护组（`04`）；下滑核验「稳定版」通道胶囊、`MaterialSwitch` 自动检查开关与 `0 / 32` 进程配额进度条（`05`）。
3. **手机操控（实验）与远程主机**：打开「手机操控」核验顶部可折叠实验说明卡、Relay 总开关（`06`）与无障碍/悬浮窗（未开启/未允许）/局域网（已允许）彩色权限胶囊及只读自检、执行面偏好（`07`）；打开「远程主机」核验连接行与 3 行双栏 `TextInputLayout` 表单，首屏直接展示 Keystore 不可导出本机公钥及复制按钮（`08`）。
4. **模型与凭据配置（实时 DSH 状态）**：进入「Provider 与凭据」，核验安全说明横幅、已配置 `DEEPSEEK_API_KEY` 脱敏掩码框（`sk-210d8…db82`，绝不回显明文）与红色清除/更新按钮，以及未配置项紧凑行和右下角 `+ 新增自定义引用` FAB（`12`）；返回更多页后摘要徽章自动同步为 `已配置 1 项`（见 `15`）。
5. **插件目录（实时列表 + 筛选）**：进入「插件目录」，核验短模块名为主标题、中文说明居副行、去重包名与「运行中 / 核心保护」双胶囊，以及仅可管理插件呈现开关与 `+ 安装插件` FAB（`13`）；点击「可管理」Chip 后正确隐藏核心保护模块（`14`）。
6. **深色模式全面回归**：切换系统 Dark Mode，逐一核查更多页（`15`）、凭据配置（`16`）、插件目录（`17`）与运行环境运行时卡片（`18`），OLED 描边、胶囊色阶、输入框与按钮对比度均清晰无误。

### 3.2 逐项结论

| 验收项 | 结论 | 对应截图 / 依据 |
|---|---|---|
| 命令式 UI 构图清零 + `XmlFirstUiGateTests` 静态门禁 | **通过** | `Agent.Tests` 108/108（含 `XmlFirstUiGateTests` 2/2、`PluginDisplayFormatterTests` 2/2） |
| 更多页顶部运行概览卡、分组卡片与动态状态刷新 | **通过** | `01`、`02`、`03`、`11`、`15` |
| 凭据配置双密度卡片、顶栏刷新、FAB 与零明文脱敏 | **通过** | `12`、`16` |
| 插件目录短标题纠偏、中文说明、搜索与「可管理」筛选 | **通过** | `13`、`14`、`17` |
| 运行环境 2×2 仪表盘、`MaterialSwitch` 与 32 进程配额条 | **通过** | `04`、`05`、`18` |
| 手机操控权限状态胶囊与折叠说明 / 远程主机首屏露出公钥 | **通过** | `06`、`07`、`08` |
| 开发者选项全 XML 化、头部快捷操作按钮与全屏日志弹窗 | **通过** | `09`、`10` |

---

## 4. 偏差与未覆盖项（如实标注）

1. **轻量界面细节微瑕（无功能阻塞，并入 Slice 5 统一打磨）**：
   - `fragment_credentials.xml` 安全说明横幅因 `maxLines="2"` 在末尾截断两字（`…绝不回显明文或落…`），将在 Slice 5 高字号适配时放开行数约束；
   - `activity_remote.xml` 右侧 `远端 DSH 端口...` 浮标 hint 略有省略号截断，值展示与输入正常；
   - `MoreFragment` 行尾 `已配置 N 项` 摘要会在打开过凭据页或首次拉取缓存后显示（见 `11` 与 `15` 对比）。
2. **未真机触发的动态分支**：
   - `ApprovalCardView` 与 `RelayApprovalConfirmActivity` 真实审批弹窗弹出瞬间（未主动构造需要授权审批的 `ui_*` 请求；其全 XML 结构与 `filterTouchesWhenObscured="true"` / `FLAG_SECURE` / 等权双按钮由 `XmlFirstUiGateTests` 与源码静态锁死）；
   - 运行态导入「需先停止 DSH」引导弹窗、真实第三方插件启停/npm 装卸（防破坏测试机环境）与 `RemoteActivity` 真机已连接态；
   - `StartupExperimentActivity`（仅 `#if DSH_DIAGNOSTICS` 构建可见）；
   - 横屏、1.3x 字体缩放与 `w600dp` 平板宽屏专测（按计划归入 **Slice 5**）。
