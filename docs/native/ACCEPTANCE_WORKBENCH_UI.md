# 深海靛蓝稳固工作台 UI 重构、对话归档/删除与插件双模式搜索真机验收报告

> 验收范围：原创「深海靛蓝 · 稳固工作台（Solid Workbench）」无悬浮 UI 重构、对话归档/取消归档/诚实删除全链路、以及插件「已安装 | 发现插件」双模式搜索。
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 真机证据、规则 11 安全不削弱、规则 12 Material 3 原生 UI）；延续全声明式 XML 与零业务 WebView 门禁。
> 真机状态：**已真机验证并完成缺陷闭环（2026-11-27，Xiaomi `M2007J1SC` / LineageOS 24.0 / Android 17 / API 37 / `1080×2340` @ 440 dpi / arm64）**——最终净构建签名 Release APK `C7A90068…9FFFE7`（45 050 707 B）使用 `adb install -r` 覆装，设备端 `base.apk` SHA-256 完全一致，修复后全流程 `logcat -b crash` **0 崩溃**。

---

## 1. 构建产物与设备环境信息

| 项目 | 实测记录 |
|---|---|
| 最终签名 Release APK | `src/DshMobile.Android/bin/Release/net11.0-android/io.github.maiji1990.dshmobile-Signed.apk` |
| APK 大小 | `45 050 707 B`（0 错误 / 62 警告） |
| 构建产物 SHA-256 | `C7A9006811B79DB7FC71AC1BF191F5318FBA4D08C6E333ADC4568CB0349FFFE7` |
| 设备端 `base.apk` 实测 SHA-256 | `c7a9006811b79db7fc71ac1bf191f5318fba4d08c6e333adc4568cb0349fffe7`（**完全一致**） |
| 安装方式 | `adb -s 4bf97d61 install -r`（保留已导入的 DSH 运行层、历史会话与凭据，未卸载清数据） |
| 测试设备 | `4bf97d61` — Xiaomi `M2007J1SC`（`lineage_cas`） |
| 系统与屏幕 | Android 17（API 37），物理分辨率 `1080×2340`，密度 `440 dpi` |
| 覆装前后设备状态 | 测试前为亮色模式（`night=no`）、竖屏（`user_rotation=0`）、自动旋转开（`accelerometer_rotation=1`）、`stay_on_while_plugged_in=0`，DSH 宿主进程已运行；验收结束已全部精确恢复原设定 |

---

## 2. 桌面单测与 APK 门禁基线（最终定稿修复后实测）

| 检查项 | 命令 | 结果 |
|---|---|---|
| Agent 单测（含归档重开 P0 回归与 XML/WebView 门禁） | `dotnet test tests/DshMobile.Agent.Tests -c Release --no-restore` | **144 通过 / 0 失败 / 0 跳过** |
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests -c Debug --no-restore` | **101 通过 / 0 失败 / 0 跳过** |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests -c Debug --no-restore` | **157 通过 / 0 失败 / 0 跳过** |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests -c Debug --no-restore` | **940 通过 / 0 失败 / 2 跳过（总计 942）** |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | **63 通过 / 0 失败** |
| 发布前净构建 + APK 门禁 | `pwsh -NoProfile -File .\tooling\build-clean-release-apk.ps1 -NoRestore` | **0 错误 / 62 警告**，通过 `NoBusinessWebViewGateTests` **4/4** |

---

## 3. 本轮真机实测发现并就地闭环修复的缺陷

### 3.1 [P0 崩溃] 归档刚刚打开过的会话后再点开已归档会话触发 `ObjectDisposedException` 闪退
- **真机复现路径**：首轮实测中（构建 `1A31CE73…470DE45`），新建临时会话 `#38da6e` 进入聊天页后返回列表 → 左滑归档该会话 → 切到「已归档 (1)」点击重新打开该会话 → 主线程抛出 `System.ObjectDisposedException: DshMobile.Agent.Engines.CoalescingPublisher`（`ChatFragment.OnStart` → `ChatViewModel.SetUiVisible` → `TranscriptHandle.Subscribe` → `CoalescingPublisher.Subscribe`）。
- **根因**：`SessionsViewModel.ArchiveSessionAsync` / `DeleteSessionAsync` 调用 `TranscriptEngine.RemoveSessionsByIdAsync` 释放了内存中的 `TranscriptHandle` 及底层 `CoalescingPublisher`，但应用级 `ChatViewModel._activeHandle` 仍持有指向该已释放句柄的悬空引用，导致再次进入 `ChatFragment.OnStart` 时对已释放对象执行订阅。
- **代码修复**：
  - [TranscriptEngine.cs](../../src/DshMobile.Agent/Engines/TranscriptEngine.cs)：新增 `SessionRemoved` 事件（覆盖主动移除与 LRU 淘汰）及 `TranscriptHandle.IsDisposed` 属性；
  - [ChatViewModel.cs](../../src/DshMobile.Agent/Presentation/ChatViewModel.cs)：订阅 `TranscriptEngine.SessionRemoved` 同步解绑 `_activeHandle` / `_transcriptSub` / `_followSub`；并在 `SetUiVisible` 与 `AttachHandle` 增加 `IsDisposed` 检查与 `ObjectDisposedException` 并发竞态兜底；
  - [SessionArchiveAndLocalStoreTests.cs](../../tests/DshMobile.Agent.Tests/Engines/SessionArchiveAndLocalStoreTests.cs)：新增端到端回归测试 `ChatViewModel_SurvivesSetUiVisibleAndReopen_AfterSessionsViewModelArchivesActiveSession`。
- **真机复验**：覆装定稿 APK（`C7A90068…9FFFE7`）后，在同一进程周期内严格执行「打开 `#38da6e` → 返回 → 归档 → 立即从『已归档 (1)』重新打开」，**零崩溃**且完整渲染「此对话已归档」横幅与「取消归档」按钮（见截图 `06`）。

### 3.2 [P2 视觉一致性] 「工作区」顶栏高度与字号未与其他主 Tab 对齐
- **视觉审阅发现**：首轮截图 `08` / `23` / `26` 显示「工作区」顶栏仍沿用旧版 `72dp` + `LargeTitle` 大字号，与「对话」「设置」的标准 `?attr/actionBarSize` 顶栏高度及字重不一致，切换底栏或横屏侧轨时顶栏上下跳动。
- **修复与复验**：修改 [fragment_files.xml](../../src/DshMobile.Android/Resources/layout/fragment_files.xml) 将顶栏统一为 `?attr/actionBarSize` 标准标题样式；重拍 `08`、`23`、`26` 确认三栏顶栏高度、字号与操作按钮垂直居中完全对齐。

### 3.3 [P2 细节排版] 「发现插件」搜索框占位符截断与聊天空态末尾孤字
- **视觉审阅发现**：首轮 `14_plugins_discover_default.png` 中占位文案在 `1080px` 宽下右侧截断为 `…cordis、mc`；首轮 `02_chat_new_session_light.png` 空态提示末尾「作」字单字换行。
- **修复与复验**：分别精简 [strings_workbench_plugins.xml](../../src/DshMobile.Android/Resources/values/strings_workbench_plugins.xml) 的 `plugins_search_discover_hint` 与 [strings.xml](../../src/DshMobile.Android/Resources/values/strings.xml) 的 `chat_empty_subtitle`；复拍 `06` 与 `14` 确认单行完整显示、零截断、零孤字。

---

## 4. 真机端到端验收矩阵

证据目录：[artifacts/screenshots/workbench-2026-11-27/](../../artifacts/screenshots/workbench-2026-11-27/)（共 **28 张**实机 PNG 截图 + [manifest.tsv](../../artifacts/screenshots/workbench-2026-11-27/manifest.tsv)），全部经逐图人工视觉审阅。

| 验收场景 | 实测内容与观察结果 | 结论 | 对应截图 |
|---|---|---|---|
| 1. 首页「对话」（亮色） | 深海靛蓝主题、1dp 结构卡片、稳固底部 3 栏导航（对话 \| 工作区 \| 设置，无双重 Insets、无裁切）、顶栏固定 `+`/搜索/更多、零 FAB、零底部悬浮条 | **通过** | `01` |
| 2. DSH 就绪态与筛选行 | 宿主胶囊显示绿点 `本地 DSH ▾`，横向筛选行完整展示 `全部` / `运行中` / `待处理` / `已归档` 芯片与历史会话列表 | **通过** | `01`、`04` |
| 3. 对话归档 / 取消归档 / 删除闭环 | 新建临时会话 `#38da6e`（不碰用户原有会话）：左滑打开 `SessionActionsBottomSheet`（归档 + 红色删除）→ 归档移出「全部」且芯片更新 `已归档 (1)` → 切入已归档见胶囊徽标 → **同进程重新打开已归档会话零崩溃**并展示「此对话已归档」横幅与「取消归档」→ 取消归档恢复至「全部」→ 点击删除弹出诚实说明确认框 → 确认后彻底从「全部」与「已归档」消失 | **通过** | `02`、`03`、`04`、`05`、`06`、`07`、`07b`、`07c` |
| 4. 聊天页真实长会话与全锚定交互 | 打开长会话 `Count from 1 to 200` 验证贴顶顶栏、贴底无缝停靠输入栏（零阴影/零浮钮）、上滑触发停靠式回到底部提示条 `chat_jump_bottom_bar`、停靠式斜杠命令面板、以及风格统一的 `ModelPickerDialog` | **通过** | `17`、`18`、`19`、`20` |
| 5. 工作区页（亮色） | 统一标准顶栏高度与字号、固定「新建或导入」按钮、无悬浮 FAB、文件/Git 槽式切换器、1dp 顶栏分隔线；点击「新建」弹出 `WorkspaceCreateBottomSheet` 正常关闭 | **通过** | `08`、`09` |
| 6. 设置页与凭据页（亮色） | 设置页 1dp 结构卡片、靛蓝主题、稳固底栏且无多余返回箭头；凭据页已移除底部悬浮 FAB、顶栏固定 `+` 与刷新入口，脱敏掩码与安全说明完整显示 | **通过** | `10`、`11` |
| 7. 插件双模式搜索（重点） | 已移除悬浮 FAB、顶栏固定手动安装/刷新、顶部「已安装 (203) \| 发现插件」槽式分段切换器；**已安装模式**输入 `timer` + 切换 `可管理` 命中 `cordis-plugin-timer` 并显示 `匹配 1 / 共 203 项`；**发现插件模式**自动请求 npm 公共仓库返回 `@deepseek-ai/*` 列表（`展示前 25 / 共 213683 个插件`），点击 `cordis` 快捷标签精准识别已安装项（`cordis-plugin-timer` 显示「官方 + 已安装」并置灰按钮），点击未安装项「安装」弹出含包规格与沙箱提示的安全确认框后取消（未污染设备） | **通过** | `12`、`13`、`14`、`15`、`16` |
| 8. 深色模式与横屏回归 | 系统 `night yes` 下聊天、对话、工作区、插件页呈现 `#0B1120` 深邃蓝黑底色 + `#60A5FA` 亮钴蓝高亮与清晰 1dp 描边；真实横屏（`user_rotation=1`）展示左侧 `NavigationRailView` + 双栏会话详情（1dp 竖/横分隔线严丝合缝）与宽屏工作区 | **通过** | `21`、`22`、`23`、`24`、`25`、`26` |
| 9. 稳定性（崩溃监控） | 覆装定稿 APK 后覆盖全部回归与交互流程，`adb logcat -b crash -d` 输出为空 | **通过（0 崩溃）** | `logcat -b crash` |

---

## 5. 客观未覆盖项与边界说明（如实标注）

1. **发现插件离线/断网错误降级态**：本次真机联网正常，npm 公共仓库实时返回真实结果；因此真机未额外断网触发网络错误降级横条与「重试」按钮（该分支已由 [NpmRegistryPluginSearchServiceTests.cs](../../tests/DshMobile.Agent.Tests/Engines/NpmRegistryPluginSearchServiceTests.cs) 与 [PluginInventoryAndViewModelTests.cs](../../tests/DshMobile.Agent.Tests/Presentation/PluginInventoryAndViewModelTests.cs) 单元测试锁定覆盖）。
2. **发现插件真实下载安装写盘**：为避免污染测试机运行层环境，点击「安装」验证安全确认弹窗后主动点击「取消」，未执行真实 npm 安装与 DSH 重启。
3. **运行中会话删除的二段「停活并删除」弹窗**：本次测试新建的临时会话处于空闲态，直接走一段式诚实确认弹窗完成删除；`SessionActive` 透传与 `stopActivity=true` 二次确认由 [SessionArchiveAndLocalStoreTests.cs](../../tests/DshMobile.Agent.Tests/Engines/SessionArchiveAndLocalStoreTests.cs) 单测锁定覆盖。
4. **测试前后 DSH 进程态**：本次接入真机前设备上 `libproot.so` / `node` 宿主已在运行，测试结束已恢复全部系统显示与电源设定（亮色、竖屏、自动旋转开启、`stay_on_while_plugged_in=0`）并退回主界面「对话」页，未强制停止用户原有的运行中 DSH 宿主。
