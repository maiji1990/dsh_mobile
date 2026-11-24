# UI Slice 2 验收记录：会话与原生聊天主路径重新布局

> 对应方案：[UI_LAYOUT_OPTIMIZATION_PLAN.md](UI_LAYOUT_OPTIMIZATION_PLAN.md) §5.2（会话首页）、§5.3（原生聊天）与 §6 Slice 2。
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12；全 XML 化布局与 BottomSheet、不引入 Compose / 新 NuGet 包、零业务 WebView。
> 真机状态：**已真机验证（2026-11-24 17:47–18:18，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——净构建 Release `5134B0AA…1FE1C154E`（43 858 824 B）覆盖安装，设备 `base.apk` SHA-256 一致。

---

## 1. 本轮交付清单

### 1.1 纯托管展示格式化与离线缓存列举（`src/DshMobile.Agent/`）
- 新增 [ChatDisplayFormatter.cs](../../src/DshMobile.Agent/Presentation/ChatDisplayFormatter.cs)：
  - `FormatShortSessionTitle`：将空标题、裸 UUID 或 `session-<uuid>` 前缀会话归一化为友好短标题（如 `未命名会话 · #bcce5e`）。
  - `FormatCompactModelBadge`：剥离冗余 Provider 前缀，生成输入区右侧拇指热区胶囊文案（如 `deepseek-flash · max`）。
  - `FormatToolCallSummary`：安全解析工具参数 JSON（含被历史/流式截断时的正则宽松提取回退），提取 `description` / `command` / `file_path` / `pattern` 生成折叠态可读标题与单行摘要，并输出无多余转义的格式化 JSON 供展开查看。
- 扩展 [KeptTranscriptStore.cs](../../src/DshMobile.Agent/Engines/KeptTranscriptStore.cs) 与 [SessionsViewModel.cs](../../src/DshMobile.Agent/Presentation/SessionsViewModel.cs)（及 [AgentConnectionScope.cs](../../src/DshMobile.Android/Bridge/AgentConnectionScope.cs) 接线）：新增 `ListRecentAsync` 与 `LoadCachedSessionsAsync`，在 DSH 未启动时按时间倒序呈现已脱敏本地缓存会话（只读回看）。

### 1.2 「会话」首页重新布局（`src/DshMobile.Android/`）
- 新建 [chats_toolbar.xml](../../src/DshMobile.Android/Resources/menu/chats_toolbar.xml)：独立菜单，移除此前复用聊天菜单后代码隐藏 3 项的做法。
- 重写 [fragment_chats.xml](../../src/DshMobile.Android/Resources/layout/fragment_chats.xml)：
  - `CoordinatorLayout + AppBarLayout(liftOnScroll)` 顶栏内置点击态宿主状态药丸 `chats_host_pill`（未启动/启动中/运行中/异常）与条件显示的 `chats_remote_pill`（远程已连直达入口）；
  - 通过 `<include layout="@layout/view_search_pill" />` 复用 Slice 1 胶囊搜索框；
  - 新增轻量状态筛选芯片栏 `chats_filter_bar`（`全部` / `进行中` / `待审批 (N)`，仅当存在运行中或待审批会话时自动显现）；
  - 底部改用 `ExtendedFloatingActionButton`（`chats_new_fab`），下滚自动收缩、回到顶部或上滑自动展开、未启动态自动隐藏。
- 重写 [item_session_row.xml](../../src/DshMobile.Android/Resources/layout/item_session_row.xml) 与 [ChatsAdapter.cs](../../src/DshMobile.Android/Chats/ChatsAdapter.cs)：移除底部三按钮常驻栏，卡片高度压减至一屏可扫 7+ 条；仅在运行中/待审批时于右下角显露紧凑角标与「停止」按钮；长按或点击右上角 `⋮` 弹出全 XML 的 [sheet_session_actions.xml](../../src/DshMobile.Android/Resources/layout/sheet_session_actions.xml)（[SessionActionsBottomSheet.cs](../../src/DshMobile.Android/Chats/SessionActionsBottomSheet.cs)）。
- 新增 [sheet_host_quick_control.xml](../../src/DshMobile.Android/Resources/layout/sheet_host_quick_control.xml) + [HostQuickControlBottomSheet.cs](../../src/DshMobile.Android/Chats/HostQuickControlBottomSheet.cs)：复用 Slice 1 的 `view_setting_nav_row.xml` 承接「启动 / 停止 DSH」、远程主机、运行环境与凭据配置入口；会话重命名改为复用 `dialog_text_input.xml`（并移除废弃的 `dialog_session_rename.xml`）。

### 1.3 「原生聊天」沉浸式重排与工具/模型/命令升级
- 重写 [fragment_chat.xml](../../src/DshMobile.Android/Resources/layout/fragment_chat.xml)、[chat_toolbar.xml](../../src/DshMobile.Android/Resources/menu/chat_toolbar.xml) 与 [ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs)：
  - 融合 `chat_session_header` 进单层 `MaterialToolbar`（主标题 = 会话短标题，副标题 = 工作区/远程标识，释放约 52dp 顶部高度）；
  - 底部重排为一体化圆角输入容器 `chat_composer_card`：上行自适应 1–5 行输入框，下行整合附件 (`ic_image`)、斜杠命令 (`ic_code`)、**拇指区可点击模型/档位胶囊 `chat_model_pill`** 与圆形发送/停止按钮；移除重复 `+` 按钮、误用 `ic_tune` 图标与无交互静态死字 `Agent · Standard`；
  - 键入 `/` 自动展开并实时过滤斜杠命令浮层，点 `<>` 也可手动开关。
- 新增 [view_chat_slash_panel.xml](../../src/DshMobile.Android/Resources/layout/view_chat_slash_panel.xml)、[item_chat_slash_row.xml](../../src/DshMobile.Android/Resources/layout/item_chat_slash_row.xml) 与 [SlashCommandAdapter.cs](../../src/DshMobile.Android/Chat/SlashCommandAdapter.cs)（移除旧 `item_chat_slash_chip.xml`）：展示 `/command`、功能说明与 `立即执行` / `需补参数` 徽章，高度随条目自适应并封顶 210dp。
- 重写 [item_chat_tool_call.xml](../../src/DshMobile.Android/Resources/layout/item_chat_tool_call.xml) 与 [ChatTimelineAdapter.cs](../../src/DshMobile.Android/Chat/ChatTimelineAdapter.cs)：左缩进由 48dp 收窄至 16dp，按工具类型切换图标并添加显式旋转折叠箭头 `chat_tool_chevron`，折叠态仅显示结构化标题与 `maxLines=2` 单行摘要（静态非选中字体），点击展开显示格式化 JSON 详情与可复制输出。
- 新增 [sheet_model_picker.xml](../../src/DshMobile.Android/Resources/layout/sheet_model_picker.xml) 并将 [ModelPickerDialog.cs](../../src/DshMobile.Android/Chat/ModelPickerDialog.cs) 升级为全宽 `BottomSheetDialog`（移除旧 `dialog_model_picker.xml`），保留右上角「凭据配置」直达按钮与 Slice 1 修复后的单行 Filter Chip 高亮。

---

## 2. 桌面与构建基线（最后一次源码定稿后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Agent 单测（含新增格式化与缓存列表 4 用例 + 门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **104 通过 / 0 失败 / 0 跳过** |
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **95 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **151 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | **880 通过 / 0 失败 / 2 跳过（总计 882）** |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `pwsh ./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 73 警告**（与 PR-09/Slice 1 基线持平），净构建 43 858 824 B 签名 APK 并通过 `NoBusinessWebViewGateTests` 4/4 |

**APK**：`io.github.maiji1990.dshmobile` Release，SHA-256 `5134B0AAB557B84333C2BA1E6A31078D45FD6A73BF73AE53F8B8DB81FE1C154E`；设备 `/data/app/…/base.apk` 实测哈希完全一致。

---

## 3. 真机验收（2026-11-24，Android 17）

证据目录：`artifacts/screenshots/slice2-2026-11-24/`（16 张 + `index.html` + `manifest.tsv`）。

### 3.1 验收步骤
1. **构建覆装与离线缓存回看**：净构建签名 Release APK 覆装到保留 5 个缓存会话与凭据的 Android 17 真机；冷启动在未启动 DSH 状态下检查顶栏 `未启动 ▾` 药丸、宿主启动卡、胶囊搜索框与「离线缓存会话（只读）」紧凑列表，分别点击顶栏药丸与卡片 `⋮` 打开两个底部抽屉。
2. **启动 DSH 与就绪列表密度验证**：点击「启动 DSH」，待顶栏药丸转为绿底 `运行中 ▾` 且会话拉取完毕，核对一屏可显示 7+ 条记录、`session-<uuid>` 会话显示为 `未命名会话 · #bcce5e` 等，滑动列表核查 `ExtendedFAB` 自动收缩与展开，点击卡片 `⋮` 验证实时操作抽屉。
3. **原生聊天主工作流**：进入含多轮 bash/read 工具调用的历史会话 `Slash Command Explanation and Examples`，检查单层顶栏与底部输入卡模型胶囊（`deepseek-flash · max`），点击工具卡展开/收起格式化 JSON 与输出；点击模型胶囊验证 `BottomSheetDialog` 与右上角「凭据配置」入口；点击 `<>` 并输入 `/co` 验证斜杠命令浮层与实时过滤。
4. **深色模式与横屏回归**：切换系统深色模式复查会话就绪页（`values-night` 1dp 卡片描边）、原生聊天与模型 `BottomSheet`；切回浅色模式横屏（2340×1080）并点击输入框核查单层顶栏空间释放及软键盘唤起行为。

### 3.2 逐项结果

| 编号 | 验收要点 | 结果 |
|---|---|---|
| UI-S2-D01 | 未启动态顶栏宿主药丸 + 快速控制抽屉 + 离线缓存只读列表与操作抽屉 | **通过**：`01-chats-stopped.png`、`02-host-quick-sheet.png`、`03-session-actions-cached.png` |
| UI-S2-D02 | 就绪态绿底 `运行中 ▾` 药丸 + 紧凑卡片（一屏 7+ 条）+ `session-<uuid>` 友好看法 + 列表滚动 `ExtendedFAB` 收缩 | **通过**：`04-chats-ready.png`、`05-session-actions-live.png`、`06-chats-scrolled-fab-shrink.png` |
| UI-S2-D03 | 聊天单层顶栏融合（无 `chat_session_header` 横条）+ 底部输入卡模型胶囊集成 | **通过**：`07-chat-conversation.png` |
| UI-S2-D04 | 工具调用卡片 16dp 左缩进 + 结构化摘要 + 无裁切 + 展开显示无转义美化 JSON 与输出 | **通过**：`07-chat-conversation.png`、`08-chat-tool-expanded.png` |
| UI-S2-D05 | 模型与档位选择器升级全宽 `BottomSheetDialog`（带拖拽条、右上角凭据入口与高亮） | **通过**：`09-chat-model-sheet.png` |
| UI-S2-D06 | 斜杠命令浮层（含说明与立即执行/需补参数徽章）及键入 `/` 前缀实时过滤 | **通过**：`10-chat-slash-panel.png`、`11-chat-slash-filtered.png` |
| UI-S2-D07 | 深色模式回归（会话就绪 OLED 1dp 描边、原生聊天、模型 BottomSheet） | **通过**：`12-dark-chats-ready.png`、`13-dark-chat.png`、`14-dark-model-sheet.png` |
| UI-S2-D08 | 横屏（2340×1080）聊天正文视野提升与输入法聚焦检查 | **通过**：`15-landscape-chat-ime.png`（正文可视区增大）、`16-landscape-chat-ime-open.png`（系统默认抽取模式记录在案，见 §4） |

### 3.3 真机走查发现并同步关闭的格式化问题
1. **实际 UUID 会话带 `session-` 前缀未命中短标题规则**：首轮真机观察 `session-bcce5e4c-...` 仍显示长 ID；已在 `ChatDisplayFormatter.StripSessionPrefix` 剥离 `session-`/`sess-`/`sess_` 前缀后通过，`04-chats-ready.png` 与 `12-dark-chats-ready.png` 复测显示为 `未命名会话 · #bcce5e`。
2. **历史长参数截断导致 `JsonDocument.Parse` 回退原文 + 展开 JSON 含 `\u0026`**：已在 `ChatDisplayFormatter` 增补截断 JSON 的宽松字段正则提取，并启用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` 仅用于展示格式化，在 `07-chat-conversation.png` 与 `08-chat-tool-expanded.png` 复测确认已消除。

---

## 4. 与方案的偏差与说明

1. **BottomSheet 实现选择**：`HostQuickControlBottomSheet`、`SessionActionsBottomSheet` 与 `ModelPickerDialog` 采用 Material 3 `BottomSheetDialog` 加载全 XML 布局（与 Slice 1 无外部持有状态生命周期保持一致），未改为无参 `BottomSheetDialogFragment`；跨配置重建保留待若有需求再抽 ViewModel 绑定。
2. **横屏输入法抽取形态（F6 延伸）**：`16-landscape-chat-ime-open.png` 显示系统默认 Gboard 横屏会进入全屏抽取输入态（Extract UI），原因在于 `chat_input` 尚未设置 `imeOptions="flagNoExtractUi"`；留待 Slice 5 横屏与大屏专项一并调整。
3. **公共底座仍待 Slice 3/4 消费的组件**：`view_appbar.xml`（仅 `fragment_chats`/`fragment_chat` 同结构内联或直接声明，`<include>` 与二级页迁移在 Slice 3/4 完成）、`view_metric_tile.xml`、`dialog_scrollable_text.xml`、`view_setting_switch_row.xml` 仍按切片节奏归属 Slice 3/4。

---

## 5. 真机未覆盖 / 限制

- 本轮会话列表中未触发实时生成中的「运行中」或「待审批」会话，因此**轻量筛选芯片条（`全部 / 进行中 / 待审批`）**及卡片右下角高频「停止」按钮虽已通过编译与逻辑绑定，但未在真机列表同时出现多态数据时实拍验证。
- 未测试连接远程 SSH 主机后的顶栏 `远程已连` 药丸（`chats_remote_pill`）及远程聊天副标题。
- 未在真机实际提交重命名、Fork 分支或发送带图片附件的消息流程（重命名与 Fork 弹窗已核对渲染入口，图片预览条与 PR-05 保持一致结构未真机挑图重跑）。
- 新建/零凭据冷启动路径与在线更新仍沿用 Slice 1 未覆盖说明。

最后更新：2026-11-24
