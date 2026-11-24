# UI Slice 5 验收记录：暗色精修、1.3x 高字号、720dp 大屏限宽与真机横屏补验

> 对应方案：[UI_LAYOUT_OPTIMIZATION_PLAN.md](UI_LAYOUT_OPTIMIZATION_PLAN.md) §4.1（宽屏响应式 `720dp`）、§5.1 / §5.3（横屏 `NavigationRailView` + 紧凑 IME）、§6 Slice 5 及 [ACCEPTANCE_UI_SLICE4.md](ACCEPTANCE_UI_SLICE4.md) 第 4 节微瑕闭环。
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12；延续全声明式 XML 与 `Edges` 无新增 `View` 约束，不引入 Compose / 新 NuGet 包。
> 真机状态：**已真机验证（2026-11-24，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——净构建 Release `B7EC7272…7FA36`（44 044 448 B）覆装，设备 `base.apk` SHA-256 一致。

---

## 1. 本轮交付清单

### 1.1 让 `values-w600dp/dimens.xml` 真正生效：全应用 `720dp` 内容居中限宽
- 修改 [values/dimens.xml](../../src/DshMobile.Android/Resources/values/dimens.xml)：声明默认值 `<dimen name="dshm_content_max_width">0dp</dimen>`（窄屏不限宽，`values-w600dp/dimens.xml` 保持 `720dp`）。
- 扩展 [Edges.cs](../../src/DshMobile.Android/Ui/Edges.cs)：新增零新增层级的 `ClampMaxWidth`（水平对称 padding 限宽，滚动条保留在屏幕边缘）与 `ClampMaxWidthByMargin`（针对带圆角描边横幅/悬浮面板，严格依据父容器宽度对称调整 Margin 避免重排震荡）。
- 全仓页面接入限宽：
  - [DshActivity.cs](../../src/DshMobile.Android/Ui/DshActivity.cs)：`SetScreen` 自动检索首个 `NestedScrollView` 内容容器应用 `ClampMaxWidth`，使全部 5 个二级设置页（`RuntimeActivity`、`AutomationActivity`、`RemoteActivity`、`DeveloperActivity`、`StartupExperimentActivity`）零额外代码统一生效；
  - [ChatsFragment.cs](../../src/DshMobile.Android/Chats/ChatsFragment.cs)（& [fragment_chats.xml](../../src/DshMobile.Android/Resources/layout/fragment_chats.xml) `chats_filter_bar` `clipToPadding="false"`）；
  - [ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs)；
  - [FilesFragment.cs](../../src/DshMobile.Android/Files/FilesFragment.cs)（& [fragment_files.xml](../../src/DshMobile.Android/Resources/layout/fragment_files.xml) `@+id/files_search_row`）与 [WorkspaceEditorActivity.cs](../../src/DshMobile.Android/Files/WorkspaceEditorActivity.cs)；
  - [MoreFragment.cs](../../src/DshMobile.Android/More/MoreFragment.cs)、[CredentialsFragment.cs](../../src/DshMobile.Android/Credentials/CredentialsFragment.cs)、[PluginsFragment.cs](../../src/DshMobile.Android/Plugins/PluginsFragment.cs)（& [fragment_plugins.xml](../../src/DshMobile.Android/Resources/layout/fragment_plugins.xml) `@+id/plugins_filter_scroll`）。

### 1.2 横屏 IME 与紧凑输入区精修（闭合 F6 及 Slice 2/3 未覆盖项）
- 在 [fragment_chat.xml](../../src/DshMobile.Android/Resources/layout/fragment_chat.xml)（`chat_input`）、[view_search_pill.xml](../../src/DshMobile.Android/Resources/layout/view_search_pill.xml)（`search_input`）与 [activity_file_editor.xml](../../src/DshMobile.Android/Resources/layout/activity_file_editor.xml)（`editor_input`）显式声明 `flagNoExtractUi`，避免横屏软键盘强制拉起全屏白框遮挡上下文。
- 强化 [ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs) 横屏+键盘紧凑监听器：弹出软键盘时同步隐藏 `_composerActions`、将 `_input` 缩窄为 `maxLines=1` / `minHeight=0` 并收缩容器垂直内边距，收起键盘后自动恢复 5 行多行输入体验。

### 1.3 1.3x 高字号防截断与 Slice 4 微瑕闭环
- 修复 [fragment_credentials.xml](../../src/DshMobile.Android/Resources/layout/fragment_credentials.xml)：安全说明条移除 `maxLines="2"` 与 `ellipsize="end"` 并补充 `@+id/credentials_intro_card`，完整呈现「…绝不回显明文或落日志。」；
- 优化 [item_credential_entry.xml](../../src/DshMobile.Android/Resources/layout/item_credential_entry.xml)：名称使用 `0dp + weight="1"`（最多 2 行）保护右侧 `已配置/未配置` 徽标在 `1.3x` 高字号下不被挤出屏幕，按钮改为 `wrap_content` + `minHeight="40dp"`；
- 修复 [strings.xml](../../src/DshMobile.Android/Resources/values/strings.xml)：将 `remote_dsh_port` 由 `"远端 DSH 端口（1024–65535）"` 精简为 `"DSH 端口"`，与左栏「SSH 端口」对称且在 `1.3x` 字体下零省略号；
- 放宽 [view_metric_tile.xml](../../src/DshMobile.Android/Resources/layout/view_metric_tile.xml) 标签/数值为 `maxLines="2"`；
- 移除固定像素高度可能引起的字体截断（[item_session_row.xml](../../src/DshMobile.Android/Resources/layout/item_session_row.xml) `session_item_cancel`、`fragment_chat.xml` `chat_model_pill`、[activity_remote.xml](../../src/DshMobile.Android/Resources/layout/activity_remote.xml) `remote_connect` 均改为 `wrap_content` + `minHeight`）。

### 1.4 静态门禁扩充（`tests/DshMobile.Agent.Tests/`）
- 在 [XmlFirstUiGateTests.cs](../../tests/DshMobile.Agent.Tests/Quality/XmlFirstUiGateTests.cs) 新增 `LandscapeNoExtractUiAndResponsiveMaxWidth_AreDeclaredAndConsumed`：锁死三处 `flagNoExtractUi` 声明与 `dshm_content_max_width` / `Edges.ClampMaxWidth` 的定义消费关系。

---

## 2. 桌面与构建基线（最后一次源码定稿后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **96 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **151 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | **880 通过 / 0 失败 / 2 跳过（总计 882）** |
| Agent 单测（含 Slice 4–5 XML/横屏/限宽门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **109 通过 / 0 失败 / 0 跳过** |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `pwsh ./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 68 警告**，净构建 44 044 448 B 签名 APK 并通过 `NoBusinessWebViewGateTests` 4/4 |

**APK**：`io.github.maiji1990.dshmobile` Release，SHA-256 `B7EC7272DF77D3B2E93FC2C223BD6F767E5091BFF6BD1C01A7B0CD075167FA36`；设备 `/data/app/…/base.apk` 实测哈希完全一致。

---

## 3. 真机验收（2026-11-24，Android 17）

证据目录：`artifacts/screenshots/slice5-2026-11-24/`（16 张 + `index.html` + `manifest.tsv`）。

### 3.1 验收步骤与实测发现
1. **1.3x 系统字体缩放验收（`settings put system font_scale 1.3`）**：
   - 检查「会话（未启动/运行中）」（`01`、`02`）、「更多」（`03`）、「Provider 与凭据」（`04`）、「远程主机」（`05`）与「运行环境」（`06`）。
   - 凭据页安全说明条已不再 2 行裁切，完整显示 `…绝不回显明文或落日志。`；`Google Gemini API Key` 换行时不挤掉右侧徽标；运行环境 2×2 指标磁贴与精简后的 `DSH 端口` 在 1.3x 字体下**零截断、零按钮文字压扁**。
2. **真实横屏分辨率验收（`2340×1080`，`user_rotation 1`，`851dp × 392dp`）**：
   - 验证 `MainActivity` 在横屏宽屏下自动从底部导航切换为左侧 **`NavigationRailView` 三栏**，同时会话（`07`）、工作区（`08`）、更多（`09`）、运行环境 `DshActivity`（`10`）与原生聊天 `ChatActivity`（`11`）的内容卡片、工具调用卡及胶囊输入条均精确居中收敛于 **`720dp`** 最佳阅读宽度。
   - **横屏 IME（`flagNoExtractUi`）复测**：打开工作区代码编辑器编辑态弹键盘（`13`）及原生聊天弹键盘（`12`），均确认系统**未切入全屏白底 ExtractUI 输入覆层**且 `chat_composer_actions` 自动隐藏释放高度（`IME actions visible: False`）；同时如实记录：`2340×1080`（`392dp` 高）物理矮屏下 5 行软键盘（`~269dp`）+ 状态/顶栏（`~81dp`）后仅余约 `42dp`，当伴随显示 `已展示本地缓存，等待 DSH 就绪` 提示条时，有限垂直物理空间仍会被横条与输入法边缘挤压（平板或更高横屏下无此约束）。
3. **横屏暗色模式回归（Dark Mode + 1dp OLED 描边）**：
   - 切换系统 `night yes` 后检查横屏 `NavigationRailView` + 会话列表（`14`）、工作区（`15`）及原生聊天（`16`），所有 `Card.Group` `1dp` 描边、状态药丸与文本对比度清晰明确。
4. 验收结束后已全部恢复真机默认设定（竖屏 `user_rotation 0`、自动旋转开启、`font_scale 1.0`、亮色模式、`DSH` 已停止）。

### 3.2 逐项结论

| 验收项 | 结论 | 对应截图 / 依据 |
|---|---|---|
| 1.3x 超大字体下主链路/设置/指标卡/凭据说明防截断 | **通过** | `01`–`06` |
| 真实横屏（`2340×1080`）`NavigationRailView` 三栏切换 | **通过** | `07`、`08`、`09`、`14`、`15` |
| 全应用宽屏 `720dp`（`values-w600dp`）居中限宽（Fragment + `DshActivity`） | **通过** | `07`–`11`、`14`–`16` |
| 横屏软键盘禁用全屏 ExtractUI 覆层 + 紧凑收起底栏动作行 | **通过（伴随矮屏物理高度边界说明）** | `12`、`13`、`XmlFirstUiGateTests` |
| 横屏深色 OLED 描边与多对比度回归 | **通过** | `14`、`15`、`16` |

---

## 4. 偏差与未覆盖项（如实标注）

1. **`2340×1080`（`392dp` 高度）竖矮型手机横屏叠加 `chat_status_banner` 与 5 行 IME 时的物理挤压**：当 DSH 未就绪横条额外占据 ~38dp 时，42dp 物理空隙无法同时容纳横条与输入框（无横条就绪态或 `> 500dp` 高度设备不受此限制；若后续用户高频在离线态横屏打字，可在紧凑态临时折叠 `chat_status_banner`）。
2. **w600dp+ 真正大屏平板设备与多语言外文真机切换**：本机验证为 `2340×1080`（`851×392dp`）真机横屏（覆盖 `w600dp` 宽度分支），未覆盖真实大尺寸平板及非简体中文 Locale。
3. **跨切片未触发分支沿用既往如实标注**：实时触发的 `ui_*` 审批弹窗/`ApprovalCardView`、真实 `pm`/npm 装卸、已连远程状态及 `#if DSH_DIAGNOSTICS` 实验页维持各自切片/PR 矩阵记录（已通过静态门禁锁死布局与安全属性）。
