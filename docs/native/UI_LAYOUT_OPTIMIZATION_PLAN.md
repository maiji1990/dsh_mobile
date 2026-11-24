# DSH Mobile 原生 Android 客户端 UI 全面重构方案（全 XML 化 + 重新布局）

> **审阅基线**：`artifacts/screenshots/pr09-2026-11-24/` 全量 34 张真机截图（PR-09 Release，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37）及 `src/DshMobile.Android`、`src/DshMobile.Agent` 全量 UI 代码扫描。
> **核心目标**：
> 1. **全面声明式 XML 化**：彻底淘汰 C# 代码动态 `new View(...)` 与 `Rows.Add/Card/Header` 命令式拼装界面，建立可复用、可维护、直观清晰的 XML 布局与组件体系；
> 2. **面向手机端单手高频动线重新布局**：不再在旧的「验收面板式」布局上小修小补，而是重新规划信息层级、操作热区与页面结构，解决当前使用繁琐、折返成本高、空间分配倒置的问题。
>
> **合规红线**：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12 与 [ADR-0021](../ADR/0021-product-ui-material3.md)、[ADR-0033](../ADR/0033-native-agent-client.md)——保持 C#-first、Material 3 组件（`Xamarin.Google.Android.Material`）、零业务 WebView、安全审批与密钥约束零降级、不引入 Jetpack Compose 或新 NuGet 包，分独立 UI 切片落地并附真机截图级验收。
> **修订日期**：2026-11-24

---

## 1. 现状问题总览：为什么必须「全 XML 化 + 重新布局」

结合 34 张真机截图与全仓源码扫描，当前 UI 存在三大根源性问题：

1. **大量页面仍在用 C# 代码手写拼装界面（9 个核心文件、上百行命令式布局代码）**
   - `MoreFragment`、`RuntimeActivity`、`AutomationActivity`、`DeveloperActivity`、`StartupExperimentActivity` 全部共享一个空壳 `fragment_list.xml`，在 C# 里靠 `Rows.Add/Header/Card/MonoCard` + `RemoveAllViews()` 运行期硬拼视图；
   - `ApprovalCardView`、`RelayApprovalConfirmActivity` 甚至直接手写 `new LinearLayout / TextView / MaterialButton` 并手动乘以 `density` 算像素边距；
   - `FilesFragment` 在弹窗（编辑、重命名）、面包屑，甚至 `RecyclerView.OnBindViewHolder` 滑动绑定中都在 `new View(...)`。
   - **直接后果**：界面结构分散在 C# 逻辑里难以预览与复用；每次刷新整页 `RemoveAllViews()` 破坏焦点与过渡动画；被迫所有页面都套用同一种 `item_row.xml`，导致“只读状态 / 开关 / 页面跳转 / 立即动作”长得完全一样。

2. **当前布局以「功能平铺」而非「用户高频动线」组织，手机端使用极不方便**
   - **高频动作藏得太深、路径过长**：切模型要够屏幕最上方，看状态/停止 DSH 要翻到「更多 → 运行环境」或右上角溢出菜单，连远程主机要切 3 层页面且主「会话」页看不到远程状态；
   - **核心视野被挤压、次要控件占大头**：聊天页双层顶栏（108dp）+ 双行输入区（含功能重复的按钮与无交互死文案），键盘弹起时消息正文只剩不到 2 张卡；工作区点一个 Markdown 文件不能直接阅读，要过 7 项无图标弹窗再进 420dp 小弹窗里看代码；
   - **单手操作不友好**：工作区的导入、搜索、排序、新建全部挤在顶部右侧 `⋮`；会话页每条卡片常驻 3 个大按钮，一屏只装 4 条且右下角 `+` 按钮遮挡卡片里的「打开」；运行环境页拉长到 5 屏滚动。

3. **存在多处明确的视觉与交互 Bug（P0 缺陷）**
   - 模型选择弹窗复用不可勾选的 `Assist` Chip，导致当前推理档位完全无选中高亮（`20`）；
   - 工具调用卡片因 `textIsSelectable="true"` 与 `maxLines="4"` 冲突导致文本上下半行裁切（`05`）；
   - `fragment_chat.xml` 存在重复 ID `@+id/chat_header_divider`（L73 & L263）；
   - 凭据页底部按钮误用聊天文案显示为「刷新历史」（`10`）；
   - `fragment_list.xml` 滚动后顶栏下缘无分隔/抬升，出现半截文字残影（`09`, `21`, `31`）；
   - 会话页未启动态空态文案提示「点右下角 + 开启新对话」，但 `+` 按钮处于灰掉禁用态，且全宽「启动 DSH」按钮缺少 `iconGravity="textStart"` 导致图标贴最左、文字居正中（`01`）；
   - 插件目录把上游长中文说明塞进 1 行标题栏截断，真正的短包名沉在底部（`11`）；
   - 标称横屏的 `33`/`34` 截图实际仍为竖屏分辨率，宽屏与横屏 IME 缺真实真机验收。

---

## 2. 问题详查 A：代码构建界面全量清查台账（须全部迁回 XML）

经全仓扫描 `src/DshMobile.Android/**/*.cs`，以下 **9 个文件** 存在命令式代码构建界面行为，必须纳入本次 XML 化改造清零：

| # | 源文件 | 当前代码构建 UI 的具体方式与位置 | 维护与运行隐患 | 目标 XML 替代方案 |
|---|---|---|---|---|
| 1 | [Ui/Rows.cs](../../src/DshMobile.Android/Ui/Rows.cs) | `Rows.Add` / `Rows.Header` / `Rows.Card` / `Rows.MonoCard` 动态 `new ImageView`、`new MaterialCardView`、代码算 `LayoutParams` 与 `Dp()` 边距（L41–111） | 迫使 5 个页面共用空壳 `fragment_list.xml` 且语义趋同；修改样式需改 C# 代码重新编译逻辑 | **废弃所有动态拼装方法**（仅保留 `Fmt` 等纯格式化辅助工具）；改为标准 XML `<include>` 模板 + 专属页面 XML |
| 2 | [More/MoreFragment.cs](../../src/DshMobile.Android/More/MoreFragment.cs) | L46–69 全页靠 `Rows.Header/Add` + `new MaterialSwitch` 构建且 `OnResume` 时 `RemoveAllViews()` 重建；L117 开源许可弹窗手写 `new ScrollView` + `SetPadding` | 无法定制顶部运行态卡片与分组容器；`warmup` 行漏绑点击事件；重绘闪烁 | 新建 `fragment_more.xml` + `dialog_scrollable_text.xml`，代码仅做数据绑定与显隐控制 |
| 3 | [RuntimeUi/RuntimeActivity.cs](../../src/DshMobile.Android/RuntimeUi/RuntimeActivity.cs) | L89–259 整页 5 屏全靠 `_content.RemoveAllViews()` + 20 余次 `Rows.Header/Add` 拼装 | 每次进度回调都销毁并重建整屏 View 树，丢失滚动位置；布尔开关误渲染成文本行；无法做仪表盘网格与进度条 | 新建 `activity_runtime.xml` + `item_runtime_update_offer.xml`，状态刷新仅更新对应视图文本/进度/可见性 |
| 4 | [Automation/AutomationActivity.cs](../../src/DshMobile.Android/Automation/AutomationActivity.cs) | L54–106 手写 `new FrameLayout`、`new MaterialSwitch`、`Rows.Header/Add/MonoCard`；L152–171 `IntroCard` 手写 `new LinearLayout + TextView + MaterialButton + Rows.Card`；L198 `new ImageView` | 说明卡按钮边距错位；无法给权限项加状态徽章与分区卡片；结构完全隐藏在 C# 构造函数里 | 新建 `activity_automation.xml`（保留页顶 `automation_approval_slot` 与 `FLAG_SECURE`） |
| 5 | [Automation/Relay/ApprovalCardView.cs](../../src/DshMobile.Android/Automation/Relay/ApprovalCardView.cs) | L52–100、L146–172 整张审批卡全手写 `new MaterialCardView + LinearLayout + 6 TextView + 3 MaterialButton` + 手动 `Dp()` | 样式调整与深浅色/字号适配成本高，难以复用 | 新建 `view_relay_approval_card.xml`（XML 直接声明 `android:filterTouchesWhenObscured="true"` 与等权按钮行），C# 仅负责 Inflate、倒计时与回调 |
| 6 | [Automation/RelayApprovalConfirmActivity.cs](../../src/DshMobile.Android/Automation/RelayApprovalConfirmActivity.cs) | L60–92 整页安全确认 Activity 全手写 `new LinearLayout + 3 TextView + 2 MaterialButton` + `Dp()` | 缺少顶部间距/安全视觉层次，与全应用 M3 风格脱节 | 新建 `activity_relay_approval_confirm.xml`（保留 `FLAG_SECURE`、`filterTouchesWhenObscured="true"` 与关闭后 500ms 延迟广播逻辑） |
| 7 | [Developer/DeveloperActivity.cs](../../src/DshMobile.Android/Developer/DeveloperActivity.cs) | L52–79 全靠 `Rows.Header/Add` + 手写 `new MaterialCardView` + `LinearLayout.LayoutParams` 拼日志卡 | 日志卡无限增高把底部关闭按钮推走，操作按钮被迫孤悬顶栏 | 新建 `activity_developer.xml`（工具卡片 + 带就近操作栏的限高日志卡 + 关闭区段） |
| 8 | [Developer/StartupExperimentActivity.cs](../../src/DshMobile.Android/Developer/StartupExperimentActivity.cs) | L39–58 全靠 `Rows.Header/Add` + 手写 `LayoutParams` 拼实验列表与输出框 | 同上，诊断页与主框架割裂 | 新建 `activity_startup_experiment.xml`（保留 `#if DSH_DIAGNOSTICS` 条件编译） |
| 9 | [Files/FilesFragment.cs](../../src/DshMobile.Android/Files/FilesFragment.cs) | 4 处代码建 View：① L787–798 `BuildCrumbs` 手写 `new ImageView + MaterialButton`；② L1179–1196 `ShowEditor` 手写 `new EditText + FrameLayout`；③ L1288–1293 `AskName` 手写 `new EditText + FrameLayout`；④ L1470 `EntryAdapter.OnBindViewHolder` **每次绑定都 `new ImageView(context)`** | 列表滑动期间频繁创建 `ImageView` 造成 GC 抖动；编辑器被困在 420dp 手写弹窗中；面包屑禁用态暗色看不清 | ① 新建 `item_file_crumb.xml` 模板 Inflate；② 升级为全屏 `activity_file_editor.xml`；③ 复用统一 `dialog_text_input.xml`；④ 新建专属 `item_workspace_entry.xml`（内置静态图标位与 `⋮` 按钮，零运行时 `new View`） |

### 声明式 XML 化硬性规范（后续全仓执行）

- **原则 1（页面与弹窗 100% XML）**：每个 `Activity`、`Fragment`、`BottomSheetDialogFragment`、`AlertDialog` 自定义内容区、悬浮/嵌入组件都必须有独立的 XML 布局文件。
- **原则 2（动态内容走 Inflate 或 RecyclerView）**：确实依赖运行时数据长度动态生成的控件（如 Markdown 段落/表格、斜杠命令、面包屑、模型档位 Chip、更新条目），**必须通过 `RecyclerView.Adapter` 或 `LayoutInflater.Inflate(Resource.Layout.item_*, parent, false)` 加载 XML 模板**，严禁在 C# 中直接 `new LinearLayout / FrameLayout / ScrollView / TextView / EditText / ImageView / MaterialButton / MaterialSwitch / MaterialCardView`。
- **原则 3（样式与间距进资源文件）**：所有颜色引用 `?attr/color*` 或 `@color/dshm_*`，圆角/间距/字号/最大宽度写在 `values/themes.xml` 与 `values/dimens.xml`（含 `values-w600dp/dimens.xml`），消除 C# 里的硬编码 `Dp(16)`、`SetPadding(...)` 与 `new LayoutParams(...)`。
- **原则 4（固化防回退门禁）**：建议在 `tests/DshMobile.Agent.Tests/Quality/` 下新增 `XmlFirstUiGateTests.cs`（仿照 `NoBusinessWebViewGateTests`），扫描 `src/DshMobile.Android/**/*.cs`，禁止新增命令式 `new LinearLayout/TextView/EditText/MaterialButton/...` 与 `Rows.Add/Card/Header` 调用。

---

## 3. 问题详查 B：当前布局「用户使用不方便」的核心症结

| 痛点维度 | 当前现状（截图证据） | 为什么让用户觉得难用 |
|---|---|---|
| **1. 空间分配严重倒置** | 聊天页顶栏占 108dp（静态 `DSH` + 副栏）、输入区占两行 88dp（`03`, `04`）；工具卡吐原始 JSON 单张占 1/5 屏（`04b`, `05`, `18`）；文件编辑器塞在 420dp 弹窗（`FilesFragment.cs:1196`） | **最需要大视野的“看对话 / 看代码”被挤成窄缝**，而“列表项 / 空状态 / 设置行”却松散占满屏幕（会话卡一屏只装 4 条、运行环境滚 5 屏） |
| **2. 单手操作热区错配** | 聊天页切模型、刷新、新建会话全在屏幕最顶端，而底部最顺手的拇指区浪费一整行放重复按钮和静态死字 `Agent · Standard`（`03`, `04`）；工作区新建/搜索/排序全藏右上角 `⋮`（`06`） | 手机单手握持时高频操作够不到顶栏，不得不频繁换手或双手操作 |
| **3. 高频链路来回折返** | 启动/停止 DSH 与本机/远程切换分散在「会话右上角菜单」「更多 → 运行环境」「更多 → 远程主机」三处；远程主机连上后在主「会话」Tab 毫无感知；点击文件先弹 7 项菜单再点编辑（`25`）；DSH 运行中点导入只靠小字写「请先停止 DSH」（`14`） | 完成一次常见动作要多跳 2–3 步，心智负担高、打断思路 |
| **4. 控件语义与反馈失真** | 运行环境「启动时自动检查更新」是开关却没 `MaterialSwitch`（`16`）；模型弹窗 `Off/Low/High/Max` 全一个样看不出当前档位（`20`）；工作区切 Git 顶栏突然变高、按钮消失、出现左空右孤的「刷新」灰条（`07`）；未启动态空态叫用户点已禁用的 `+`（`01`）；凭据页按钮叫「刷新历史」（`10`） | 界面传达的视觉信号与真实功能不一致，用户不断猜“这个能不能点、现在是什么状态” |
| **5. 信息扫描效率低** | 插件页标题全是被截断的中文长句，包名沉底（`11`）；工具卡直接摊开 JSON 且无展开箭头，看不出执行了什么命令、能不能看输出（`04b`, `05`）；更多页和手机操控页不透出各模块当前开关/就绪状态（`08`, `12`） | 用户必须逐项点进去或逐字读 JSON 才能获取关键信息 |
| **6. 视觉细节瑕疵与适配缺口** | 滚动后顶栏下缘露出半截文字（`09`, `31`）；工具卡文字上下裁切（`05`）；底部按钮栏切半列表末项（`10`, `11`）；三套搜索框风格（`02` vs `06` vs `03`）；暗色面包屑过暗、卡片与底色融合（`28`, `30`）；`33`/`34` 横屏截图实际为竖屏 | 细节粗糙削弱产品质感，宽屏/横屏实际体验未获真机保障 |

---

## 4. 统一 XML 组件库与设计规范（复用底座）

在重排各页面前，先在 `src/DshMobile.Android/Resources/` 建立统一的 Material 3 公共 XML 组件与资源规范，保证全 App 视觉一致、一改全改：

### 4.1 主题与尺寸资源（`values/themes.xml`、`values/dimens.xml`、`values-w600dp/dimens.xml`）
- **`Widget.DshMobile.AppBar`** & **`Widget.DshMobile.Toolbar`**：所有页面顶栏统一嵌套在 `AppBarLayout(app:liftOnScroll="true")` 中，滚动时自动产生 M3 `surfaceContainer` 抬升色调隔离内容，彻底消除 `09`/`31` 顶部文字裁切残影。
- **`Widget.DshMobile.SearchField`**：统一胶囊搜索框（圆角 `24dp`、`colorSurfaceContainerLow` 填充、前置 `ic_search`、尾置 `clear_text` 清除键），全应用（会话、工作区、插件）只用这一套搜索框。
- **`Widget.DshMobile.Card.Group`**：设置与功能分组卡片（圆角 `16dp`、`colorSurfaceContainerLow` 填充；深色模式补 `1dp` `colorOutlineVariant` 轻描边提升 OLED 层次感）。
- **`Widget.DshMobile.Badge.*`**：统一状态胶囊标签样式（`Success` 绿底绿字、`Primary` 主题容器底、`Warning` 第三容器底、`Error` 错误容器底、`Neutral` 表面高容器底）。
- **宽屏响应式尺寸（`values-w600dp/dimens.xml`）**：定义 `dshm_content_max_width="720dp"` 与宽屏页边距，确保平板与横屏手机下卡片、表单、气泡不过度拉宽。

### 4.2 可复用公共 XML 片段（彻底取代 `Rows.cs` 动态拼装）

| 公共 XML 文件 | 用途与结构 | 复用页面 |
|---|---|---|
| `view_appbar.xml` | `AppBarLayout(liftOnScroll=true) + MaterialToolbar + LinearProgressIndicator` | 全部二级页与 3 个主 Tab |
| `view_search_pill.xml` | 统一 M3 胶囊搜索框（支持可选右侧扩展操作位，如排序/筛选图标） | 会话、工作区、插件 |
| `view_setting_nav_row.xml` | 图标 + 标题 + 副标题 + 右侧可选状态标签 + `ic_chevron_right`（整行波纹可点） | 更多、运行环境、手机操控、开发者 |
| `view_setting_switch_row.xml` | 图标 + 标题 + 副标题 + `MaterialSwitch`（整行点击联动切换开关） | 更多、运行环境、手机操控 |
| `view_setting_action_row.xml` | 图标 + 标题 + 副标题 + 右侧可选状态/提示标签（不带箭头，明确区分“触发动作”与“跳转页面”） | 运行环境、开发者、手机操控 |
| `view_metric_tile.xml` | 紧凑只读指标格（小图标 + 标签 + 粗体值，用于 2×2 仪表盘网格） | 运行环境状态卡、系统治理卡 |
| `view_status_pill.xml` | 紧凑圆角状态胶囊（小圆点/图标 + 短文本，用于顶栏与卡片状态透出） | 会话、更多、运行环境、远程、操控 |
| `dialog_text_input.xml` | 统一 M3 `OutlinedBox` 单行输入弹窗内容（取代 `FilesFragment.AskName` 手写 `EditText`） | 重命名、新建文件/文件夹、递归搜索 |
| `dialog_scrollable_text.xml` | 统一可滚动等宽/正文弹窗（取代 `MoreFragment.ShowLicenses` 手写 `ScrollView`） | 开源许可、诊断/盘点详情 |

---

## 5. 全应用重新布局方案（按用户高频动线逐页重设计）

### 5.1 全局导航壳层（`activity_main.xml`）

- 维持三栏结构（**会话 | 工作区 | 更多**），窄屏用 `BottomNavigationView`，宽屏（≥ 600dp）用左侧 `NavigationRailView`（支持顶部悬浮新建按钮 `headerLayout`）。
- 跨会话待审批横幅 `main_inbox_banner` 保持顶部高优先级，增加进入/退出平滑显隐，点击打开 `InboxBottomSheet`（`sheet_inbox.xml` 增加拖拽指示条 `BottomSheetDragHandleView`）。
- **真机横屏修复**：确保 `MainActivity` 与 `ChatActivity` 在真实横屏旋转下流畅交接 Insets，且 `values-w600dp` 限制内容区最大宽度 `720dp`。

---

### 5.2 「会话」首页重新布局（`fragment_chats.xml` + `item_session_row.xml` + `sheet_session_actions.xml`）

**核心改造思路**：把「会话」真正做成用户的**主控台 + 快速续聊入口**——一进门就知道 DSH 状态、一步能启停、一屏能扫 6–7 条对话、点一下就开聊，彻底消灭三按钮臃肿卡与 FAB 遮挡。

```text
┌──────────────────────────────────────────────────────────┐
│ 会话                          [● 运行中 ▾]   [↻]   [⋮]  │  ← 顶栏直接展示宿主状态药丸（点它可一步启停/看运行环境）
├──────────────────────────────────────────────────────────┤
│  🔍  搜索会话标题、工作区...                             │  ← 统一 M3 胶囊搜索框（仅就绪或有缓存会话时显示）
├──────────────────────────────────────────────────────────┤
│  [全部]  [进行中]  [待审批 (1)]                          │  ← 可选轻量筛选芯片（仅当存在运行中/待审批时自动出现）
├──────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────┐ │
│ │ Slash Command Explanation and Exam...  [当前]    [⋮] │ │  ← 紧凑卡片（~76dp 高）：点整卡直入对话
│ │ 11-24 13:42 · /root/projects            ● 运行中 停止 │ │     仅运行中/待审批在右下角显露高频胶囊动作
│ └──────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────┐ │
│ │ PR09 OK reply                                    [⋮] │ │  ← 重命名 / Fork 收口进右上角 [⋮] 或长按底部抽屉
│ │ 11-24 13:03 · /root/projects                         │ │
│ └──────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────┐ │
│ │ 未命名会话 · #bcce5e                             [⋮] │ │  ← UUID 会话做友好短标题展示
│ │ 11-23 15:30 · /root/projects                         │ │
│ └──────────────────────────────────────────────────────┘ │
│                                        [ ＋ 新对话 ]     │  ← ExtendedFAB（滚动自动缩为 +，未就绪时隐藏）
├──────────────────────────────────────────────────────────┤
│       [ 会话 ]           [ 工作区 ]           [ 更多 ]   │
└──────────────────────────────────────────────────────────┘
```

#### 关键改动要点
1. **顶栏集成「宿主状态药丸」**：
   - 未启动 / 启动中 / 就绪 / 失败均在顶栏右侧以紧凑胶囊呈现（如绿点 `运行中`、灰点 `未启动`）；已连接远程主机时额外透出 `远程已连` 快捷入口。
   - 点击状态药丸弹出轻量 `ModalBottomSheet`（`sheet_host_quick_control.xml`）：直接提供「启动 / 停止 DSH」「切换/打开远程主机」「查看运行环境」，**日常启停无需再钻「更多 → 运行环境」或翻溢出菜单**。
2. **修复未启动态矛盾与按钮对齐（P0）**：
   - `chats_host_action` / `chats_host_secondary` 全部加上 `app:iconGravity="textStart"`；
   - DSH 未启动时：居中/置顶展示清晰的宿主启动引导卡（含一键启动 / 在线初始化 / 导入），**自动隐藏无用的搜索框与灰掉的 FAB**，不再在下方渲染「暂无会话，点击右下角 + 开启新对话」的矛盾空态（若 `KeptTranscriptStore` 有离线缓存会话，则在宿主卡下方直接展示「离线缓存会话（只读）」供回看）。
3. **高密度紧凑会话卡片（`item_session_row.xml` 重写）**：
   - 删除底部常驻的 `Fork 分支`、`重命名`、`打开` 三按钮栏（卡片高度从 ~145dp 压缩至 ~76dp，一屏显示量从 4 条增至 6–7 条，且彻底消除 FAB 遮挡问题）；
   - 右上角提供 `36dp` `⋮` 按钮（同步支持长按卡片），拉起全 XML 的 `sheet_session_actions.xml`（包含「打开」「重命名」「Fork 分支」「停止当前轮次」）；
   - 状态标签（`当前` / `运行中` / `待审批 N`）统一改为 `view_status_pill.xml` 样式胶囊；
   - 拆分独立菜单 `Resources/menu/chats_toolbar.xml`，不再复用聊天页菜单后代码隐藏 3 项。

---

### 5.3 「原生聊天」沉浸式重新布局（`fragment_chat.xml` + `item_chat_tool_call.xml` + `sheet_model_picker.xml`）

**核心改造思路**：把顶部和底部的冗余厚度削减掉 ~85dp 全部还给消息流；把**最高频的模型切换、选图、斜杠命令全部下沉到单手拇指区**；把刷屏的原始 JSON 工具卡改造成人类可读的结构化折叠卡片。

```text
┌──────────────────────────────────────────────────────────┐
│ ←  Slash Command Explanation...                [↻]  [⋮]  │  ← 单层 56dp 顶栏（主标题=会话名，副标题=工作区/远程标识）
│    /root/projects                                        │     释放原 52dp 独立副栏高度！
├──────────────────────────────────────────────────────────┤
│                           ┌────────────────────────────┐ │
│                           │ 帮我查一下有哪些斜杠命令？ │ │  ← 用户气泡（保持清晰柔和对比）
│                           └────────────────────────────┘ │
│                                                          │
│ ┌──────────────────────────────────────────────────────┐ │
│ │ >_ bash · List user guide docs         已完成 ✓  ⌄  │ │  ← 缩窄左缩进至 16dp；结构化摘要代替原始 JSON
│ │ $ cd /opt/dshapp/... && ls user/guide                │ │  ← 无 textIsSelectable 冲突，彻底解决裁切
│ └──────────────────────────────────────────────────────┘ │
│ ┌──────────────────────────────────────────────────────┐ │
│ │ >_ read · docs/subsystems/commands.md  已完成 ✓  ⌄  │ │  ← 折叠态高度压减 45%，整卡可点展开看输出与工作区跳转
│ └──────────────────────────────────────────────────────┘ │
│                                                          │
│ 🤖 以下是 DSH 当前注册的斜杠命令清单...                  │  ← 对话正文拥有充裕阅读视野
│                                                          │
├──────────────────────────────────────────────────────────┤
│ ┌──────────────────────────────────────────────────────┐ │  ← 统一双层融合胶囊输入面板（全在拇指热区！）
│ │ 向 DSH 发送消息...                                   │ │
│ │ [ 🖼 ]  [ / 命令 ]   [ DeepSeek-Flash · High ▾ ]  [↑]│ │  ← 去重！删除重复 ic_tune 与静态 Agent·Standard
│ └──────────────────────────────────────────────────────┘ │     模型档位胶囊放进拇指区，单手一键切换！
└──────────────────────────────────────────────────────────┘
```

#### 关键改动要点
1. **单层顶栏融合（释放 ~52dp）**：
   - 删除 `chat_session_header` 横条；将会话标题直接放入 `MaterialToolbar` 主标题，工作区简名（或「远程 · WSL-DSH」）作为副标题；
   - 修复 `fragment_chat.xml` 重复 ID（底部改为 `chat_composer_divider`）；
   - 工具栏菜单 `chat_toolbar.xml` 精简为「新建会话」「刷新历史」「模型与凭据配置」「停止当前轮次（仅运行中显示）」。
2. **输入面板彻底重排（拇指区集成高频操作，消除重复按钮）**：
   - 改为现代 AI 原生客户端通用的**一体化圆角输入容器**（上行：自适应 1–5 行 `EditText`；下行左侧：`ic_image` 附件、`ic_code`/`/` 斜杠命令按钮；下行右侧：**可点击的模型与推理档位胶囊** `Flash · High ▾` + 圆形发送/停止按钮）；
   - **彻底移除**：与 `+` 功能完全重复的 `ic_tune` 按钮、无交互的静态死字 `Agent · Standard`；
   - 键盘弹起且横屏时（F6），自动隐藏下方快捷操作行与斜杠/附件条，保留单行输入框+发送键，确保横屏打字时仍能看到完整对话上下文。
3. **工具调用卡片结构化 + 根治裁切（`item_chat_tool_call.xml`）**：
   - **彻底解决文字裁切（P0）**：折叠态摘要 `TextView` 禁用 `textIsSelectable`（仅展开态输出框允许选中复制），整张卡片响应点击展开/收起；
   - **结构化人类可读摘要**：解析 `ArgumentsJson` 提取 `description` / `command` / `file_path`（例如 `bash` 显示描述 + 单行 `$ command`，`read/edit/write` 直接显示文件名），原始 JSON 与完整输出 `chat_tool_output` 默认折叠，点击展开查看；
   - **显式折叠箭头 + 收窄左缩进**：标题行最右侧增加旋转 `ic_expand_more` 图标；卡片 `paddingStart` 从 `48dp` 缩窄为 `16dp`（或 `20dp`），大幅减少代码/命令无谓折行。
4. **斜杠命令面板升级（`view_chat_slash_panel.xml` + `item_chat_slash_row.xml`）**：
   - 点击 `/` 按钮或在输入框键入 `/` 时，在输入面板正上方展开圆角浮层列表（限高 `220dp`），每项显示「`/command` + 说明摘要 + 右侧动作标识（立即执行 / 需补参数）」，支持实时关键字过滤。
5. **模型与推理档位选择器升级为 `ModalBottomSheet`（`sheet_model_picker.xml` + `item_model_effort_chip.xml`）**：
   - **修复 P0 Bug**：新增 `item_model_effort_chip.xml`（采用 `Widget.Material3.Chip.Filter`，`checkable="true"`），配合 `ChipGroup(singleSelection="true")`，让当前生效的 `Off / Low / High / Max` 档位清晰高亮并带勾选标记；
   - 由窄 `AlertDialog` 改为全宽 `BottomSheetDialogFragment`（含拖拽条、按 Provider 分组、当前选中模型主色描边高亮、右上角直达「凭据配置」入口）。

---

### 5.4 「工作区」重新布局（`fragment_files.xml` + `item_workspace_entry.xml` + `activity_file_editor.xml` + `sheet_file_actions.xml`）

**核心改造思路**：让工作区从“文件菜单弹窗集合”变成真正顺手的**手机端代码查看与项目管理工作台**——顶栏切换零跳动、单点文件直接阅读、常用新建/导入单手可达、非 Git 目录能主动发现子仓库。

```text
┌──────────────────────────────────────────────────────────┐
│ 工作区                                         [↻]  [⋮]  │  ← 文件/Git 切换时顶栏始终单行不跳动！刷新位固定！
├──────────────────────────────────────────────────────────┤
│  [          文件          |          Git           ]     │  ← 紧凑 40dp SegmentedButton
├──────────────────────────────────────────────────────────┤
│  🔍 筛选或搜索工作区...                        [⇅ 排序] │  ← 统一胶囊搜索框 + 内联排序切换（回车支持递归搜索）
│  📁 /root/projects  ›  pr06-repo                         │  ← XML 面包屑项，末级高对比度加粗，深色清晰可读
├──────────────────────────────────────────────────────────┤
│  📁  .dsh                                           🔒  │  ← 专属 item_workspace_entry.xml（零运行期 new View）
│  📁  pr06-repo                                     [⋮]  │  ← 点文件夹进入；点 [⋮] 或长按弹底部操作抽屉
│  📄  print-statements-demo.md                      [⋮]  │  ← 单击文件直接全屏阅读/编辑（不再先弹 7 项菜单！）
│      8.2 KB · 2026-11-23 10:51                           │
│                                              [ ＋ 新建 ] │  ← 单手 FAB：新建文件 / 新建文件夹 / 导入文件
├──────────────────────────────────────────────────────────┤
│       [ 会话 ]           [ 工作区 ]           [ 更多 ]   │
└──────────────────────────────────────────────────────────┘
```

#### 关键改动要点
1. **消除「文件 ↔ Git」切换跳动**：
   - `MaterialToolbar` 在两种模式下固定为单行「工作区」+ 右上角「刷新」与上下文菜单；删除 Git 面板里冗余的 `git_refresh_btn`；
   - **Git 非仓库智能引导**：当前目录无 `.git` 时隐藏 `git_header_bar`（彻底消灭 `07` 中的大片空白灰条），在空态中自动扫描一层子目录中的 `.git` 仓库（如 `pr06-repo`），渲染为可一键点击切换的「发现以下子仓库」快捷芯片，极大降低手动先切回文件找目录再切回 Git 的折返成本。
2. **文件列表与面包屑全 XML 化 + 性能修复**：
   - 新增 `item_workspace_entry.xml`（静态声明左侧类型图标、标题、副标题、多选勾选态、受保护锁图标与行尾 `⋮` 按钮），彻底消除 `FilesFragment.cs:1470` 在 `OnBindViewHolder` 里不断 `new ImageView` 的 GC 隐患；
   - 新增 `item_file_crumb.xml`，末级目录不再使用 `Enabled=false` 置灰，改为 `Clickable=false` + `?attr/colorOnSurface` 加粗样式，解决深色模式 `30` 几乎不可读的问题；
   - 将裸 `<EditText>` 替换为 `view_search_pill.xml` 胶囊搜索栏 + 右侧内联排序图标按钮。
3. **阅读优先 + 单手高频操作抽屉**：
   - **单击文本/代码/Markdown 文件直接进入全屏查看/编辑页**（新建 `activity_file_editor.xml` + `WorkspaceEditorActivity.cs`，顶栏含文件名、只读/编辑切换开关、保存按钮与冲突检测逻辑，彻底取代 420dp 的手写 `AlertDialog` 编辑器）；
   - **文件/目录管理动作**：点击行尾 `⋮` 或长按唤起 `sheet_file_actions.xml`（带图标、将「删除」用 `colorError` 标红），不再弹出 7 项无图标纯文字弹窗；
   - **单手新建/导入 FAB**：文件视图右下角增加轻量 `+` FAB，点击弹出 `sheet_workspace_create.xml`（新建文件 / 新建文件夹 / 从系统导入文件），把原本藏在右上角 `⋮` 里的高频动作拉回拇指区；输入名称弹窗复用 `dialog_text_input.xml`。

---

### 5.5 「更多」与 6 个二级页全面 XML 重构

#### 5.5.1 更多页（`fragment_more.xml`）
- **结构重排**：顶部增加一张「运行与连接概览卡」（左侧显示本机 DSH 状态/版本 + 远程连接状态，右侧快捷「启/停」按钮），让用户进入「更多」就能直接掌控运行态；
- 下方用 3 张 `Widget.DshMobile.Card.Group` 分组圆角卡片（全 XML `<include>` `view_setting_nav_row` / `view_setting_switch_row`）：
  1. **AI 能力与扩展**：模型与凭据配置（右侧显示 `已配置 N 项`）、插件目录、手机操控（右侧显示 `已开启/已关闭`）、远程主机（右侧显示 `已连接/未连接`）；
  2. **运行环境与性能**：运行环境（右侧显示版本号）、后台预热 DSH（精简副标题至 1–2 行，整行点击联动 `MaterialSwitch`）；
  3. **应用与支持**：关于 DSH Mobile（保留连点 7 次开启开发者选项）、开源许可（复用 `dialog_scrollable_text.xml`）、开发者选项（开启后可见）。
- 彻底废弃 `MoreFragment.cs` 中的 `Rows.Add/Header` 动态构建与 `RemoveAllViews()`。

#### 5.5.2 模型与凭据配置页（`fragment_credentials.xml` + `item_credential_entry.xml`）
- **修复 P0 Bug**：删除底部截切列表的双按钮栏，将刷新动作移至顶栏右上角图标位，文案修正为「刷新凭据状态」（`credentials_action_refresh`）；「新增自定义引用」改为右下角 `ExtendedFloatingActionButton`。
- **双密度卡片布局**：
  - 已配置项：显示名称 + 绿色 `已配置` 胶囊 + Ref/来源 + 柔和灰底掩码框 + 右下角「清除（`colorError`）/ 更新」；
  - 未配置项：自动隐藏掩码框与下方独立按钮行，压缩为**紧凑双行左信息 + 右「配置」Tonal 按钮**，一屏即可看全所有内置 Provider。
- 保留顶部安全说明提示（收紧为 2 行轻量带盾牌图标横条），绝不削弱不回显明文原则。

#### 5.5.3 插件目录页（`fragment_plugins.xml` + `item_plugin_entry.xml`）
- **纠正标题与描述倒置**：
  - 主标题（1 行粗体）：展示精简可读短包名/模块名（如 `dsh-hmr`、`cordis-plugin-timer`、`dsh-plugin-manager`）；
  - 副描述（最多 2 行正文）：承接上游 `DisplayName` 返回的中文功能说明（当其为说明句时）；
  - 元信息行：去重 `ModuleName · PackageName`（不再重复拼接前缀相同的包名），状态（「运行中 / 已禁用 / 待重启」）与「核心保护」使用不同语义色胶囊标签。
- **操作效率升级**：搜索栏复用 `view_search_pill.xml`；搜索栏下方增加 XML 声明的横向状态过滤 Chip 组（`全部` / `可管理` / `已启用` / `核心保护`），让用户一键筛出自己装的或可启停的插件，不被内置核心模块淹没；刷新上移顶栏，安装改为 `ExtendedFAB`，消除底部截切。

#### 5.5.4 运行环境页（`activity_runtime.xml` + `item_runtime_update_offer.xml`）
- **彻底告别 5 屏平铺与 `RemoveAllViews()`**，用专属 `activity_runtime.xml` 组织为 **4 张结构化功能卡**（总滚动高度减半至 ~2.2 屏）：
  1. **运行状态仪表盘卡**：头部展示运行状态大徽章 + DSH 版本 + 右侧直接「启动 / 停止 DSH」按钮；下方 2×2 `view_metric_tile` 网格（运行层状态、链接兼容层、可用空间、上次操作状态），不再用 5 个大行占满一整屏；
  2. **离线维护与回滚卡**：统一离线导入图标为 `ic_folder`（在线下载用 `ic_download`），包含导入运行环境包、导入 DSH 层、回滚到上一版（全带右箭头）；若 DSH 正在运行，点击导入时弹窗引导「需先停止 DSH 才能导入，是否立即停止并选择文件？」，避免手动折返；
  3. **在线更新卡**：更新通道（右显当前通道胶囊 + 箭头）、**启动时自动检查更新（升级为真实 `MaterialSwitch` 行）**、卡片底部左「复制诊断」右「检查更新」按钮；检查结果与更新计划通过固定容器 + `item_runtime_update_offer.xml` 局部更新，不再整页重建；
  4. **系统治理与进程健康卡**：子进程预算 `5 / 32` 配备可视化 `LinearProgressIndicator`（接近 32 变 `colorError`），并将「前往开发者选项检查限制」内嵌为该区块关联按钮；温控阶梯、活跃工作租约、上次退出死因以紧凑键值行呈现（异常高亮错误色）。

#### 5.5.5 手机操控页（`activity_automation.xml` + `view_relay_approval_card.xml` + `activity_relay_approval_confirm.xml`）
- **全 XML 化（含安全审批组件）**：
  - 将 `AutomationActivity`、`ApprovalCardView`、`RelayApprovalConfirmActivity` 三处全部迁至专属 XML 布局；
  - 严格保留页顶 `automation_approval_slot`、`FLAG_SECURE`、`PageCardShown` 生命周期、XML 上显式声明 `android:filterTouchesWhenObscured="true"` 及等权审批按钮视觉原则（不暗示默认点允许）。
- **布局易用性改进**：
  - 顶部说明卡修复「了解审批与权限」左对齐边距；
  - 「手机操控中继」主开关独立置顶强调，下方「前置权限检查清单」分组卡片为无障碍、悬浮审批卡、局域网访问、Shizuku、DSH 工具提供清晰彩色状态胶囊（`已就绪` 绿 / `未开启` 灰 / `需重启` 橙），用户一眼看出还缺哪项授权；
  - 自检操作与结果输出收口进独立「连接自检」卡片，输出区限高可滚动。

#### 5.5.6 远程主机页（`activity_remote.xml` 重新编排）
- **按真实使用顺序重组（严守 ADR-0006/0012/0013 安全边界）**：
  1. **紧凑连接状态卡**：未连接时图标+状态标题与右侧「连接」按钮排在同一行（砍掉 40dp 空洞）；已连接时主操作「打开远程对话」置为全宽突出按钮，下方一行左右排布「断开」与「启/停远端 DSH」；
  2. **紧凑主机配置卡**：表单从 4 行压缩为 3 行（`名称` 独占 1 行；`主机地址(2.2) + SSH 端口(1)` 同行；`用户名(1.5) + 远端 DSH 端口(1)` 同行），全部采用 `Dense` OutlinedBox 样式，释放垂直空间；
  3. **本机公钥卡前置可见**：表单压缩后，首屏底端即可直接看到「本机公钥」与「复制公钥」按钮，新用户首次配置无需盲目下拉寻找；
  - 指纹逐字确认弹窗与 Keystore 不可导出私钥机制完全保持不变。

#### 5.5.7 开发者选项与启动实验页（`activity_developer.xml` + `activity_startup_experiment.xml`）
- 全部改为静态 XML 布局：顶部「诊断工具」分组卡 + 中部「操作日志」卡片（**将「复制」「清空」按钮就近放进日志卡片标题栏右侧**，日志正文限高 `320dp` 且支持点击全屏查看）+ 底部「关闭开发者选项」独立危险色操作卡，保证底部按钮永不被超长日志顶出视野。

---

## 6. 分阶段实施切片与验收门禁

遵循 `AGENTS.md` 规则 3（最小垂直切片）与规则 12（独立 UI 切片附真机截图验收），按以下 **5 个独立切片** 顺序推进：

> **进度（2026-11-24）**：**UI 全面重构 5 个切片已全线完成并通过 Android 17 真机验证**——证据分见 [ACCEPTANCE_UI_SLICE1.md](ACCEPTANCE_UI_SLICE1.md)（`1D127DB6…4FD5DE`）、[ACCEPTANCE_UI_SLICE2.md](ACCEPTANCE_UI_SLICE2.md)（`5134B0AA…1FE1C154E`）、[ACCEPTANCE_UI_SLICE3.md](ACCEPTANCE_UI_SLICE3.md)（`7C73C973…C57A`）、[ACCEPTANCE_UI_SLICE4.md](ACCEPTANCE_UI_SLICE4.md)（`04DBF481…CC97`）与 [ACCEPTANCE_UI_SLICE5.md](ACCEPTANCE_UI_SLICE5.md)（`B7EC7272…7FA36`，含 `artifacts/screenshots/slice5-2026-11-24/`）。

| 切片 | 主题 | 核心交付（全 XML 化 + 布局重构） | 验收标准 |
|---|---|---|---|
| **Slice 1**（✅ 已完成，2026-11-24 真机通过） | **公共 XML 底座 + P0 缺陷清零** | 1. 新建 `view_appbar.xml`、`view_search_pill.xml`、`view_setting_*_row.xml`、`view_metric_tile.xml`、`view_status_pill.xml`、`dialog_text_input.xml`、`dialog_scrollable_text.xml` 与 `values/dimens.xml` / `values-night` / `values-w600dp`<br>2. 修复 `fragment_chat.xml` 重复 ID、新增 `item_model_effort_chip.xml`（Filter Chip）修复档位高亮与 F28、修复凭据页「刷新凭据状态」文案、修复工具卡 `textIsSelectable` 裁切、修复会话未启动空态矛盾与 `iconGravity="textStart"`；`fragment_list.xml` 改为 `AppBarLayout(liftOnScroll)` | 6 项 P0 缺陷全部真机归零；单测全绿（详见 [ACCEPTANCE_UI_SLICE1.md](ACCEPTANCE_UI_SLICE1.md)） |
| **Slice 2**（✅ 已完成，2026-11-24 真机通过） | **会话与原生聊天主路径重新布局** | 1. 重写 `fragment_chats.xml` + `item_session_row.xml` + `sheet_session_actions.xml` + `sheet_host_quick_control.xml` + `chats_toolbar.xml`（顶栏宿主状态药丸、紧凑卡片一屏 7+ 条、`session-<uuid>` 短标题、离线缓存回看、`ExtendedFAB`）<br>2. 重写 `fragment_chat.xml` + `item_chat_tool_call.xml` + `view_chat_slash_panel.xml` + `item_chat_slash_row.xml` + `sheet_model_picker.xml`（单层顶栏、拇指区融合输入与模型胶囊、结构化工具卡、`/` 自动过滤斜杠命令浮层、`BottomSheetDialog` 模型选择器，清理旧 `dialog_*`/`item_chat_slash_chip`） | 单层顶栏释放 ~52dp；工具卡无裁切且折叠态压减高度；模型与档位单手直达；全套单测与真机 16 张截图通过（详见 [ACCEPTANCE_UI_SLICE2.md](ACCEPTANCE_UI_SLICE2.md)） |
| **Slice 3**（✅ 已完成，2026-11-24 真机通过） | **工作区重新布局与 XML 化** | 1. 重写 `fragment_files.xml` + 新建 `item_workspace_entry.xml`、`item_file_crumb.xml`、`item_git_subrepo_chip.xml`、`sheet_file_actions.xml`、`sheet_workspace_create.xml`、`activity_file_editor.xml` 与静态菜单 `files_toolbar.xml` / `files_selection_toolbar.xml` / `file_editor_toolbar.xml`<br>2. 统一文件/Git 单行顶栏零漂移、`FileListing.DiscoverGitRepositories` 非仓库自动发现子仓库快捷芯片、单击文件直进全屏 `WorkspaceEditorActivity`、单手 `+` FAB 新建/导入、彻底消除 `FilesFragment` 中全部命令式 `new View`，修复面包屑 `FullScroll` 抢焦 | 文件/Git 切换顶栏零跳动；单击 `.md`/`.txt` 直开全屏阅读；搜索框连续键入回车递归搜索直出命中；暗色面包屑清晰可读；全套单测与真机 16 张截图通过（详见 [ACCEPTANCE_UI_SLICE3.md](ACCEPTANCE_UI_SLICE3.md)） |
| **Slice 4**（✅ 已完成，2026-11-24 真机通过） | **更多页与 6 大二级页全面 XML 化（清零代码拼 UI）** | 1. 新建 `fragment_more.xml`、`activity_runtime.xml`、`item_runtime_update_offer.xml`、`activity_automation.xml`、`view_relay_approval_card.xml`、`activity_relay_approval_confirm.xml`、`activity_developer.xml`、`activity_startup_experiment.xml` 与工具菜单 `credentials_toolbar.xml` / `plugins_toolbar.xml`<br>2. 重排 `fragment_credentials.xml` / `item_credential_entry.xml`、`fragment_plugins.xml` / `item_plugin_entry.xml`（接入 `PluginDisplayFormatter` 短标题 + FilterChip）、`activity_remote.xml`（首屏露出本机公钥）<br>3. 移除 `Rows.cs` 中所有命令式视图构建方法与废弃的 `fragment_list.xml`/`item_row.xml`/`item_section_header.xml`，新增 `XmlFirstUiGateTests.cs` 静态防回退门禁 | 全仓命令式代码拼 UI 归零（除 `ChatMarkdownBinder`）；运行环境缩至 ~2.2 屏配真 `MaterialSwitch` 与 `32` 进度条；插件短标题 + 筛选通过；`XmlFirstUiGateTests` 锁定审批安全属性；真机 18 张截图与全套门禁通过（详见 [ACCEPTANCE_UI_SLICE4.md](ACCEPTANCE_UI_SLICE4.md)；实时审批触发态待专项补充） |
| **Slice 5**（✅ 已完成，2026-11-24 真机通过） | **暗色精修、大屏限宽与真机横屏补验** | 1. `Edges.ClampMaxWidth` / `ClampMaxWidthByMargin` 与 `DshActivity` 统一接入 `values-w600dp/dimens.xml` 的 `720dp` 居中限宽<br>2. 放宽 `1.3x` 高字号下安全说明条/凭据徽标/指标格/`DSH 端口`与按钮高度裁切，补充 `fragment_chat`/`view_search_pill`/`activity_file_editor` `flagNoExtractUi` 与横屏 IME 紧凑态及门禁<br>3. 真机 `2340×1080` 横屏（`NavigationRailView` 三栏 + 限宽 + 横屏 IME + 暗色 OLED）与 `1.3x` 字体 16 张截图验收归档 | `Agent` 109/109、全套单测与 APK 门禁通过；1.3x 字体、真机横屏与暗色 16 张截图通过（详见 [ACCEPTANCE_UI_SLICE5.md](ACCEPTANCE_UI_SLICE5.md)） |

### 强制回归门禁（每切片合入前必跑）
- `dotnet test tests/DshMobile.Core.Tests`
- `dotnet test tests/DshMobile.Runtime.Tests`
- `dotnet test tests/DshMobile.Application.Tests`
- `dotnet test tests/DshMobile.Agent.Tests -c Release`（含 `NoBusinessWebViewGateTests` 及 Slice 4 新增的 `XmlFirstUiGateTests`）
- `./tooling/build-clean-release-apk.ps1`（净构建 + APK 零业务 WebView 产物门禁）
- **安全红线逐项复验**：`AutomationActivity` / `ApprovalCardView` / `RelayApprovalConfirmActivity` 的 `FLAG_SECURE` + `filterTouchesWhenObscured`；`RemoteActivity` 指纹固定与 Keystore 不可导出；`CredentialsFragment` 零明文回显；外链确认后走系统浏览器。
