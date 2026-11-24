# UI Slice 3 验收记录：工作区重新布局与全 XML 化

> 对应方案：[UI_LAYOUT_OPTIMIZATION_PLAN.md](UI_LAYOUT_OPTIMIZATION_PLAN.md) §5.4（工作区重新布局）与 §6 Slice 3。
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12；全 XML 化布局与 BottomSheet、不引入 Compose / 新 NuGet 包、严格保留 `.dsh/` 受保护区只读拦截、符号链接禁止跟随及 `TextDocument` 冲突检查。
> 真机状态：**已真机验证（2026-11-24，Xiaomi M2007J1SC / LineageOS 24.0 / Android 17 / API 37 / arm64 / 非 root）**——净构建 Release `7C73C973…C57A`（43 933 266 B）覆装，设备 `base.apk` SHA-256 一致。

---

## 1. 本轮交付清单

### 1.1 工作区子仓库自动发现（`src/DshMobile.Core/` & `tests/DshMobile.Core.Tests/`）
- 扩展 [FileListing.cs](../../src/DshMobile.Core/Files/FileListing.cs)：新增 `DiscoverGitRepositories(WorkspaceFileSystem fs, string? start, int maxCount = 8, CancellationToken ct = default)`，在当前目录非 Git 仓库且未报 `GIT_UNAVAILABLE` 时，于后台线程扫描直接子目录的 `.git`，自动跳过符号链接与 `.dsh/` 受保护目录。
- 扩展 [FileWorkbenchTests.cs](../../tests/DshMobile.Core.Tests/FileWorkbenchTests.cs)：新增 `DiscoverGitRepositories_FindsImmediateNonProtectedGitChildren_SkipsSymlinksAndDsh` 单测校验正常子仓库发现及符号链接/`.dsh` 跳过。

### 1.2 「工作区」全面 XML 化与主布局重构（`src/DshMobile.Android/`）
- 重写 [fragment_files.xml](../../src/DshMobile.Android/Resources/layout/fragment_files.xml)：
  - 改为 `CoordinatorLayout + AppBarLayout(liftOnScrollTargetViewId="@id/files_list")`，内置紧凑 `40dp` 分段开关 `文件 | Git`；
  - 筛选搜索区复用共享 `view_search_pill.xml`（补充 `app:hintEnabled="false"` 消除 Pill 上边框标签裁切），并排放置 `files_sort_btn`；
  - Git 空态区 `git_empty_wrapper` 内置 `git_subrepos_bar` + `ChipGroup` 展示自动发现的子仓库跳转芯片；
  - 右下角拇指热区放置 `ExtendedFloatingActionButton`（`files_create_fab`）。
- 新建全部工作区专属 XML 组件与静态菜单：
  - [item_workspace_entry.xml](../../src/DshMobile.Android/Resources/layout/item_workspace_entry.xml)：含类型图标、标题、大小/修改时间/搜索路径副标题、`.dsh` 受保护锁标、选中态勾选徽标与行尾 `⋮` 操作按钮；
  - [item_file_crumb.xml](../../src/DshMobile.Android/Resources/layout/item_file_crumb.xml)：面包屑节点与 `›` 分隔箭；
  - [item_git_subrepo_chip.xml](../../src/DshMobile.Android/Resources/layout/item_git_subrepo_chip.xml)：子仓库快速跳转 Chip；
  - [sheet_file_actions.xml](../../src/DshMobile.Android/Resources/layout/sheet_file_actions.xml) & [FileActionsBottomSheet.cs](../../src/DshMobile.Android/Files/FileActionsBottomSheet.cs)：能力门控 8 项操作抽屉（`colorError` 红色删除项）；
  - [sheet_workspace_create.xml](../../src/DshMobile.Android/Resources/layout/sheet_workspace_create.xml) & [WorkspaceCreateBottomSheet.cs](../../src/DshMobile.Android/Files/WorkspaceCreateBottomSheet.cs)：单手新建文件 / 新建文件夹 / 系统选择器导入抽屉；
  - [files_toolbar.xml](../../src/DshMobile.Android/Resources/menu/files_toolbar.xml) & [files_selection_toolbar.xml](../../src/DshMobile.Android/Resources/menu/files_selection_toolbar.xml)：静态声明常规（含「刷新」「回到工作区根目录」）与多选批处理菜单。
- 全屏文本/代码阅读与编辑器（独立 Activity）：
  - 新建 [activity_file_editor.xml](../../src/DshMobile.Android/Resources/layout/activity_file_editor.xml)、[file_editor_toolbar.xml](../../src/DshMobile.Android/Resources/menu/file_editor_toolbar.xml) 与 [WorkspaceEditorActivity.cs](../../src/DshMobile.Android/Files/WorkspaceEditorActivity.cs)：支持只读预览/编辑态切换（顶栏 `编辑` / `退出编辑 | 保存`，底栏 `只读` / `编辑中` + 编码/行数/字数）、`TextDocument.Validate`、`ReadOnly`/二进制只读保护、三向版本冲突弹窗（重新加载/覆盖/保留编辑）及未保存退出确认。
- 重构 [FilesFragment.cs](../../src/DshMobile.Android/Files/FilesFragment.cs)（及微调 [ChatsFragment.cs](../../src/DshMobile.Android/Chats/ChatsFragment.cs) 搜索框 Hint 接线）：
  - 彻底清除 `EntryAdapter` / `BuildCrumbs` / `AskName` 中的命令式 `new ImageView` / `new MaterialButton` / `new EditText` / `new FrameLayout` 视图构建（改用 `dialog_text_input.xml` 与 XML 膨胀）；
  - 单击可读文本/代码文件直达全屏 `WorkspaceEditorActivity`，行尾 `⋮` 与压缩包点击拉起 `FileActionsBottomSheet`，长按进入多选；
  - 保证 `文件` 与 `Git` 模式顶栏严格单行零跳动（`[44,133][227,222]` 与刷新键 `[837,112][969,244]` 位置零像素偏移）；
  - 修复面包屑 `BuildCrumbs` 在搜索键入时每次全量重绘并调用 `FullScroll` 抢占搜索框焦点的问题（按 `_dir|_searchQuery` 增量复用并改用 `ScrollTo`），同时支持 `OnBack()` 退出递归搜索时清空筛选文本。

---

## 2. 桌面与构建基线（最后一次源码定稿后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测（含子仓库发现新增用例） | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **96 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **151 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | **880 通过 / 0 失败 / 2 跳过（总计 882）** |
| Agent 单测 | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **104 通过 / 0 失败 / 0 跳过** |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **48 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `pwsh ./tooling/build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 69 警告**，净构建 43 933 266 B 签名 APK 并通过 `NoBusinessWebViewGateTests` 4/4 |

**APK**：`io.github.maiji1990.dshmobile` Release，SHA-256 `7C73C973E6C645F25C1AB9F93645C5CD6C8F99CAEFD5B7E9D68B7195A0C8C57A`；设备 `/data/app/…/base.apk` 实测哈希完全一致。

---

## 3. 真机验收（2026-11-24，Android 17）

证据目录：`artifacts/screenshots/slice3-2026-11-24/`（16 张 + `index.html` + `manifest.tsv`）。

### 3.1 验收步骤与实测发现闭环
1. **搜索胶囊 & 工作区根目录**：覆装后检查「会话」页与「工作区」页 `view_search_pill` 居中且无上边框裁切；`/root/projects` 显示 `.dsh` 锁标、`pr06-repo` 文件夹与 `print-statements-demo.md`（行尾 `⋮`）、排序钮与 `+ 新建` ExtendedFAB（`00`、`01`）。
2. **新建/排序/文件操作抽屉**：点击 `+ 新建` 展开 `sheet_workspace_create.xml`（`02`）；点击 `名称 ↑` 打开排序单选框（`03`）；点击行尾 `⋮` 展开 `sheet_file_actions.xml`（`04`）。
3. **全屏文本阅读与编辑切换**：单击 `print-statements-demo.md` 直达 `WorkspaceEditorActivity` 只读视图（`05`，展示 409 行 / 8394 字符，顶栏「编辑」、底栏「只读」）；切换编辑后顶栏明确显示 `退出编辑 | 保存`，底栏显示 `编辑中`（`06`）。
4. **子目录面包屑、长按多选与递归搜索（含缺陷当场闭环）**：进入 `pr06-repo` 确认末级面包屑高对比加粗且不可误点（`07`）；长按 `hello.txt` 核验多选状态栏与卡片选中样式（`08`）；首轮输入 `hello` 发现仅录入首字母 `h`，排查确认系 `TextChanged` 每次触发 `BuildCrumbs().FullScroll()` 抢焦所致，改为按 `_dir|_searchQuery` 缓存并用 `ScrollTo` 修复重编覆装后，连续键入 `hello` + 回车成功触发全工作区递归搜索，命中 `pr06-repo/hello.txt` 并展示「找到 1 项（检查了 46 项）」（`09`）。
5. **文件 / Git 顶栏零跳动与子仓库跳转**：从「文件」切至「Git」（`01` vs `10`），实测 UI 树坐标：`工作区` 标题 `[44,133][227,222]` 与刷新钮 `[837,112][969,244]` 在双模式下**完全零像素偏移**；`/root/projects` 无有效仓库时隐藏分支条、展示自动扫描到的 `pr06-repo` 芯片（`10`）；点击芯片直达 `master` 分支变更（`11`），点击 `README.md` 展开原生红绿 Diff 与行号（`12`）。
6. **深色模式回归**：切换系统 Dark Mode 核验 Git 变更列表（`13`）、子目录 OLED 描边与加粗白色末级面包屑（`14`）、全屏阅读器（`15`），均对比分明无灰暗遮挡。

### 3.2 逐项结论

| 验收项 | 结论 | 对应截图 / 依据 |
|---|---|---|
| `files_toolbar` 单行稳定、刷新位双模式固定 | **通过** | `01` vs `10`（UI 树 `[44,133][227,222]` / `[837,112][969,244]` 一致） |
| `view_search_pill` 居中无裁切 + 实时筛选 + 回车递归搜索 | **通过**（修复 `FullScroll` 抢焦后重测） | `00`、`01`、`09` |
| XML 行卡（`.dsh` 受保护锁、行尾 `⋮`）、XML 面包屑高对比 | **通过** | `01`、`07`、`14` |
| 单击文本/代码文件直进全屏阅读与编辑（状态/动作词明确） | **通过** | `05`、`06`、`15` |
| `+ 新建` ExtendedFAB & `FileActionsBottomSheet`（red 删除项） | **通过** | `02`、`04` |
| 长按多选批量模式 | **通过** | `08` |
| Git 非仓库隐藏无效条、自动扫描一级子仓库跳转 & Diff 视图 | **通过** | `10`、`11`、`12`、`13` |

---

## 4. 偏差与未覆盖项（如实标注）

1. **SAF 文件导入/导出、实际磁盘编辑保存冲突与回收站删除撤销**：为保持测试机既有 `/root/projects` 用户文件完好且避免脚本自动化拉起系统外部文档选择器后失控，本轮未在真机执行真实写入保存/删除恢复与 SAF 往返（已由既有 `FileWorkbenchTests` 与 Slice 3 新增 Core 单测覆盖后端路径、编码、冲突检测与权限守卫）。
2. **软键盘弹出状态下编辑器保存与冲突三向弹窗真机触发、`.zip` 解压/压缩、聊天工具卡路径直达 Diff 跳入**：未构造真机交互样本；Diff 页右上角「预览文件」入口与 `WorkspaceEditorActivity` 仅通过代码接线与单路径打开验证。
3. **w600dp 平板尺寸与真机横屏浏览**：未在本轮采集横屏截图（横屏专测按计划并入 Slice 5）。
