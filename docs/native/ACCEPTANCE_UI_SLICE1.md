# UI Slice 1 验收记录：公共 XML 底座 + P0 缺陷清零

> 对应方案：[UI_LAYOUT_OPTIMIZATION_PLAN.md](UI_LAYOUT_OPTIMIZATION_PLAN.md) §4（统一 XML 组件库）与 §6 Slice 1。
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（规则 5 不伪造成功、规则 9 真机证据、规则 12 Material 3 + 独立 UI 切片截图级验收）；不引入 Jetpack Compose、不新增 NuGet 包、零业务 WebView。
> 真机状态：**已真机验证（2026-11-24 16:29–17:02，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——净构建 Release `1D127DB6…4FD5DE`（43 711 131 B）覆盖安装，设备 `base.apk` 拉回后 SHA-256 一致；6 项 P0 全部真机归零，公共底座资源全部参与 AAPT 链接与真机渲染（共用 `fragment_list` 的更多 / 运行环境 / 手机操控三页真机回归通过）。

---

## 1. 本轮交付清单

### 1.1 公共 XML 底座（`src/DshMobile.Android/Resources/`）

| 文件 | 内容 | 本轮真机消费情况 |
|---|---|---|
| [view_appbar.xml](../../src/DshMobile.Android/Resources/layout/view_appbar.xml) | `AppBarLayout`（`Widget.DshMobile.AppBar`）+ `MaterialToolbar`（`Toolbar.InAppBar`）+ `LinearProgressIndicator` | 由 `fragment_list.xml` 以同结构内联使用（更多 / 运行环境 / 手机操控三页真机回归通过；开发者 / 启动实验共用同一布局，本轮未单独打开）；`<include>` 接入留待 Slice 2–4 |
| [view_search_pill.xml](../../src/DshMobile.Android/Resources/layout/view_search_pill.xml) | 统一胶囊搜索框（`Widget.DshMobile.SearchField`：24dp 圆角、`colorSurfaceContainerLow` 填充、前 `ic_search`、`clear_text` 尾键） | 样式已在会话页搜索框真机生效；模板本身待工作区 / 插件页接入 |
| `view_setting_nav_row.xml` / `view_setting_switch_row.xml` / `view_setting_action_row.xml` | 设置三级行（跳转 / 开关 / 立即动作），整行波纹 + 右侧状态位 | 待 Slice 4 接线（本轮仅参与资源链接） |
| [view_metric_tile.xml](../../src/DshMobile.Android/Resources/layout/view_metric_tile.xml) | 2×2 仪表盘只读指标格 | 待 Slice 4 接线 |
| [view_status_pill.xml](../../src/DshMobile.Android/Resources/layout/view_status_pill.xml) | 紧凑状态胶囊（+ `bg_status_pill.xml`、`Widget.DshMobile.Badge.*`） | 待 Slice 2/4 接线 |
| [dialog_text_input.xml](../../src/DshMobile.Android/Resources/layout/dialog_text_input.xml) | 统一 M3 单行输入弹窗内容 | 待 Slice 3 接线 |
| [dialog_scrollable_text.xml](../../src/DshMobile.Android/Resources/layout/dialog_scrollable_text.xml) | 统一可滚动文本弹窗 | 待 Slice 4 接线 |
| [item_model_effort_chip.xml](../../src/DshMobile.Android/Resources/layout/item_model_effort_chip.xml) | `Widget.Material3.Chip.Filter` 推理档位芯片（`checkable` + 勾选图标 + 收窄内边距） | **已真机消费**（模型与档位弹窗） |

### 1.2 资源规范

- [values/dimens.xml](../../src/DshMobile.Android/Resources/values/dimens.xml)：`dshm_page_padding_horizontal` / `dshm_card_corner_radius`(16dp) / `dshm_search_corner_radius`(24dp) / `dshm_card_stroke_width`(0dp)。
- [values-night/dimens.xml](../../src/DshMobile.Android/Resources/values-night/dimens.xml)：深色下卡片描边 1dp（OLED 层次）；[values-w600dp/dimens.xml](../../src/DshMobile.Android/Resources/values-w600dp/dimens.xml)：宽屏页边距 24dp + `dshm_content_max_width`(720dp)。**本轮仅定义，尚未被布局引用**（限宽在 Slice 5 生效）。
- [values/themes.xml](../../src/DshMobile.Android/Resources/values/themes.xml) 新增 `Widget.DshMobile.AppBar`、`Widget.DshMobile.Toolbar.InAppBar`、`Widget.DshMobile.SearchField`、`Widget.DshMobile.Mono.Static`、`Widget.DshMobile.Card.Group`、`Widget.DshMobile.Badge.{Neutral,Primary,Success,Warning,Error}`；新增 `drawable/ic_search.xml`、`drawable/bg_status_pill.xml`（`UPSTREAMS.md` 图标数 32 → 33）。

### 1.3 P0 缺陷清零（源码改动）

| # | 原缺陷（方案 §1.3） | 改动 |
|---|---|---|
| P0-1 | `fragment_chat.xml` 重复 ID `chat_header_divider`（L73 与 L263） | 底部改为 `@+id/chat_composer_divider`；全仓布局重复 ID 扫描为 0 |
| P0-2 | 模型档位复用不可勾选 `Assist` Chip，当前档位无高亮（截图 20） | 新增 `item_model_effort_chip.xml`（Filter Chip，`singleSelection` + `selectionRequired`）；`ModelPickerDialog` 按 `currentEffort ?? DefaultReasoningEffort` 预选，选中卡片加主色描边 |
| P0-3 | 凭据页底部按钮误用聊天文案「刷新历史」（截图 10） | 新增 `credentials_action_refresh`「刷新凭据状态」，底部栏加分隔线与 `colorSurfaceContainer` |
| P0-4 | 工具卡 `textIsSelectable` + `maxLines=4` 冲突导致上下半行裁切（截图 05） | 新增 `Widget.DshMobile.Mono.Static`（不可选中）+ `ellipsize=end`；折叠/展开切换时同步 `Ellipsize`；点参数字体也可展开 |
| P0-5 | 未启动态空态矛盾（叫用户点已禁用的 `+`）、搜索框与灰 FAB 常驻（截图 01） | 未就绪：隐藏搜索框与 FAB，空态文案仅在「就绪」或「有搜索词」时显示（新增 `chats_empty_search`）；FAB 就绪才 `Show()` |
| P0-6 | 全宽「启动 DSH」缺 `iconGravity="textStart"`，图标贴最左（截图 01） | `chats_host_action` / `chats_host_secondary` 均加 `app:iconGravity="textStart"` |

另：`fragment_list.xml` 由 `LinearLayout + view_toolbar` 改为 `CoordinatorLayout + AppBarLayout(liftOnScroll) + NestedScrollView`（滚动后 `surfaceContainer` 抬升，修截图 09/21/31 的顶栏残影），五个使用方（更多 / 运行环境 / 手机操控 / 开发者 / 启动实验）共用 `toolbar` / `list_content` / `list_progress` ID 不变。

---

## 2. 桌面与构建基线（最后一次源码定稿后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests --no-restore` | **95 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests --no-restore` | **880 通过 / 0 失败 / 2 跳过（总计 882）**；首轮为 879/880（1 项失败，终端输出未保留用例名），立即重跑 880/880 全绿、未复现；`DshMobile.Runtime` 本轮零改动 |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests --no-restore` | **151 通过 / 0 失败 / 0 跳过** |
| Agent 单测（含 5 组质量门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **100 通过 / 0 失败 / 0 跳过**（含 `NoBusinessWebViewGateTests` 4 项） |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 73 警告**（与 PR-09 基线一致；Slice 1 触及文件零新增告警），净构建 43 711 131 B 签名 APK 并通过 APK 产物门禁 4/4 |

**APK**：`io.github.maiji1990.dshmobile` Release，SHA-256 `1D127DB640B36BBFF1983D3C87D7164851235C5F3C21DDB13EB54290DF4FD5DE`；设备 `/data/app/…/base.apk` 拉回哈希一致。

> 构建告警口径：`dotnet build` 汇总行为 73（仅 `DshMobile.Android.csproj`，与 PR-09 相同）；日志中同一条警告会因设计时构建重复打印，按汇总行为准。

---

## 3. 真机验收（2026-11-24，Android 17）

证据目录：`artifacts/screenshots/slice1-2026-11-24/`（17 张正式截图 + `index.html` + `manifest.tsv`；目录内另有 `09c-licenses-crop.png` 与 `15-landscape-chat-divider.png` 两张过程截屏未入清单）。

### 3.1 验收步骤（覆盖安装与逐项走查）

1. **构建与覆盖安装**：净构建 Release APK（`1D127DB6…4FD5DE`）覆盖安装到保留历史会话与凭据的 Android 17 真机，核对设备 `base.apk` SHA-256 与桌面构建产物一致。
2. **未启动态与就绪态（`UI-S1-D01` / `D02`）**：冷启动停在「会话」未启动页，截图并在 UIAutomator 树核查不存在 `chats_search_layout`、`chats_new_fab` 及矛盾空态文本，检查 `chats_host_action` 图标与文字同行居中；点击启动待 DSH 就绪后复查胶囊搜索框与新建 FAB 正常出现。
3. **对话工具卡与模型档位（`UI-S1-D03` / `D04`）**：进入含工具调用的历史会话，核对工具卡折叠态四行显示且上下不裁切，点击参数字体展开 JSON 与输出并再次点击折叠；随后打开模型选择弹窗，切换思维档位至 `Max` 后确认会话副标题更新为 `deepseek-official/deepseek-flash · max · /root/projects`，重新打开弹窗验证 `Max` 仍保持勾选（初轮发现 F28 换行后已收窄 Chip 内边距并重建覆装复测通过）。
4. **凭据页与共享列表 AppBar（`UI-S1-D05` / `D06` / `D07`）**：切换至「更多 → 模型与凭据配置」，核查底部固定操作栏分隔线与「刷新凭据状态」文案并滑到底部查看全部凭据条目；回到「更多」及二级页「运行环境」「手机操控」，对比顶栏初始态与下滚后色调抬升且无正文透字残影。
5. **横屏 IME 与深色模式回归（`UI-S1-D08` / `D09`）**：切换系统深色模式复查「更多」顶栏、「会话」胶囊搜索框/卡片及模型档位 Chip 对比度；切回会话详情横屏并弹出软键盘，核对仅隐藏 `chat_session_header` 与 `chat_header_divider`，`chat_composer_divider` 仍保留在输入区上方。

### 3.2 逐项结果

| 编号 | 验收要点 | 结果 |
|---|---|---|
| UI-S1-D01 | P0-5 / P0-6：DSH 未启动时仅显示宿主卡，无搜索框、无灰 FAB、无矛盾空态；启动按钮图标与文字同行 | **通过**：`01-chats-stopped.png`；uiautomator 树中无 `chats_search_layout` / `chats_new_fab`，`chats_host_action` 存在且图标贴文字 |
| UI-S1-D02 | 就绪后搜索框与 FAB 回归可见（胶囊搜索框样式） | **通过**：`02-chats-ready.png` |
| UI-S1-D03 | P0-4：工具卡折叠态四行完整不裁切，点参数字体可展开 JSON 与输出，展开可逆 | **通过**：`03-chat-tool-cards.png`、`04-chat-tool-expanded.png`（对照 PR-09 `04b` / `05` 的上下半行裁切） |
| UI-S1-D04 | P0-2：档位 Filter Chip 明确高亮且可切换；重开弹窗保持选中 | **通过**：`05-model-picker.png`（Max 勾选 + 容器填充 + 卡片主色描边，四档同行）；点 Max 后会话副标题变为 `deepseek-official/deepseek-flash · max · /root/projects`，重开弹窗仍选中 Max |
| UI-S1-D05 | P0-3：凭据页底部按钮文案为「刷新凭据状态」，固定操作栏有分隔线，滚动到底可见全部条目 | **通过**：`06-credentials-refresh-label.png`、`07-credentials-bottom.png` |
| UI-S1-D06 | `fragment_list` 新 AppBar 在滚动后抬升，标题区不透出正文残影（截图 09/21/31） | **通过**：`08-more-top.png` ↔ `09-more-scrolled-appbar.png`；顶栏区域像素 `rgb(233,239,240)`（抬升色调）与内容区 `rgb(247,250,251)` 可分（`09b-appbar-crop.png`） |
| UI-S1-D07 | 共用 `fragment_list` 的二级页回归无崩溃、无布局错位 | **通过**：运行环境 `13-runtime-shared-list-appbar.png`、手机操控 `14-automation-shared-list-appbar.png` |
| UI-S1-D08 | P0-1：重复 ID 修复的可见行为——横屏 IME 紧凑形态下隐藏会话头与其分隔线，输入区上方分隔线保留 | **通过**：`15-landscape-chat-compact.png`（会话头 + 两条分隔线均可见）→ `16-landscape-ime-compact.png`（`chat_session_header` / `chat_header_divider` 消失，`chat_composer_divider` 仍在） |
| UI-S1-D09 | 深色模式回归：搜索胶囊、会话卡、档位 Chip 选中态对比度 | **通过**：`10-dark-more-appbar.png`、`11-dark-chats.png`、`12-dark-model-picker.png` |

### 3.3 本轮真机发现并修复（F28，已关闭）

- **F28（低，视觉回归）**：`Widget.Material3.Chip.Filter` 选中后勾选图标使该芯片变宽，窄弹窗内四档被挤成 3+1 两行，选中卡片比其它卡片更高。
  - 修复：`item_model_effort_chip.xml` 收窄 `chipStart/EndPadding`、`textStart/EndPadding` 至 4dp，`checkedIconSize` 16dp、`checkedIconMargin` 2dp；复测四档同行（`05-model-picker.png`、`12-dark-model-picker.png`）。
  - 关闭依据：同一设备、同一弹窗，浅色与深色两次复测均为单行。

---

## 4. 与方案的偏差与说明

1. **底座组件多数尚未被页面消费**：`view_appbar` / `view_search_pill` / `view_setting_*` / `view_metric_tile` / `view_status_pill` / `dialog_*` 本轮只完成声明与资源链接，真机渲染验证随 Slice 2–4 接线进行；这是切片边界，不是未完成项。
2. **`values-night/dimens.xml` 与 `values-w600dp/dimens.xml`** 已定义但暂未被引用（`dshm_content_max_width` 在 Slice 5 生效）；宽屏 `NavigationRailView` 下的内容限宽仍未做（`15-landscape-chat-compact.png` 可见横屏内容仍贴左、未限宽）。
3. **横屏 + 输入法的输入区高度分配**（F6，PR-04 遗留）不在本切片范围：`16-landscape-ime-compact.png` 仍可见输入区占据上半屏，Slice 5 处理。
4. **会话卡三按钮与 FAB 遮挡最后一条**（方案 §5.2）由 Slice 2 重排；**凭据页固定操作栏会遮住未滚到底的最后一张卡**（方案 §5.5.2）由 Slice 4 重排——两者均为方案内已排期项，不另登记缺陷。

---

## 5. 真机未覆盖 / 限制

- 未做全新安装（清除数据）路径；本轮为覆盖安装，设备数据（5 个历史会话、`/root/projects`、已配置凭据与自定义引用）全部保留。
- 在线安装 / 在线更新链路仍未验证（GitHub 清单不可达，与 PR-08 D04 同因）。
- 平板 / `w600dp` 分档、大字号（1.3x）与多语言截断未验证（Slice 5）。
- `ApprovalCardView` 与 `RelayApprovalConfirmActivity` 在本切片未改动，未复测 `FLAG_SECURE` / `filterTouchesWhenObscured`（Slice 4 迁移时按原矩阵复验）。

最后更新：2026-11-24
