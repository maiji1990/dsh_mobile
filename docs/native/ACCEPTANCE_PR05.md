# PR-05 原生凭据配置、Cache-First 首屏与会话管理验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（`N-05-01` ～ `N-05-05`）
> 分支：`feat/native-pr05-onboarding-sessions`
> 规则约束：遵守 [AGENTS.md](../../AGENTS.md) 规则 5、9。
> 真机环境：Xiaomi M2007J1SC（LineageOS，Android 17 / API 37，arm64），Release APK `6F001E0D81BD…7B21A169`（2026-11-23 14:52 构建），DSH 层 `0.2.2-rc.5`，Wi‑Fi 在线。

## 1. 本轮实现清单（全 C# + 全 XML 声明式布局）

| 任务项 | 主要代码与布局资产 | 说明 |
|---|---|---|
| `N-05-01`｜原生 Provider / 凭据配置与首次引导 | `CredentialsViewModel`、`CredentialsActivity` / `CredentialsFragment`、`activity_credentials.xml` 等 | 展示 Ref 的已配置/未配置状态、保存与清除、零凭据引导 |
| `N-05-02`｜模型目录与运行中切换 | `ChatViewModel.RefreshModelCatalogAsync` / `SelectModelAsync`、`ModelPickerDialog` | 按 `SwitchModelInSession` 门控模型与 Reasoning Effort 切换 |
| `N-05-03`｜`ChatsFragment` 会话列表与生命周期 | `SessionsViewModel`、`ChatsFragment`、`item_session_row.xml`、底部 `nav_chats` | 新建、搜索、重命名、Fork、角标、能力门控取消、历史分页 |
| `N-05-04`｜0.2s Cache-First 首屏 | `KeptTranscriptStore.TryLoadMostRecentAsync`、`ChatViewModel.LoadMostRecentCachedSessionAsync`、`ChatActivity` 测点 | 先渲染缓存，连接后校准；记录 `native.activity_visible` / `native.cache_first_rendered` |
| `N-05-05`｜旧 Web Profile 只读盘点 | `WebProfileInventory`、`ChatsFragment` 横幅与详情弹窗 | 只读存在性盘点，不读凭据、不改目录 |

## 2. 桌面自动化测试与裁剪构建验证（F15–F19 修复后重跑）

| 检查项 | 结果 |
|---|---|
| `dotnet test tests/DshMobile.Core.Tests --no-restore` | 通过：93 / 93 |
| `dotnet test tests/DshMobile.Runtime.Tests --no-restore` | 通过：834 / 836（2 跳过） |
| `dotnet test tests/DshMobile.Application.Tests --no-restore` | 通过：135 / 135 |
| `dotnet test tests/DshMobile.Agent.Tests --no-restore` | 通过：65 / 65（含 F15/F16/F17/F19 新增契约与 ViewModel 单测 + 5 组 Quality 门禁） |
| `node tooling/run-node-tests.mjs tests/guest` | 通过：48 / 48 |
| `dotnet build src/DshMobile.Android -c Release --no-restore` | 通过：0 错误 / 73 警告（与门禁 ≤75 一致，PR-05 新增与修复文件无新增告警） |

## 3. Android 17 arm64 真机验收矩阵（2026-11-23 15:00–15:35）

| 验收项 | 期望行为 | 真机结果 |
|---|---|---|
| `N-05-01` 零凭据首次闭环 | 零凭据安装后不打开 WebView 完成配置、状态查看、保存与清除 | **不通过**：已配置凭据显示「未配置」并反复弹出「尚未检测到可用的 API Key」引导（缺陷 F15）；自定义引用保存成功但列表不显示、无法清除（F16） |
| `N-05-02` 模型目录与切换 | 顶栏显示模型/Reasoning Effort，可切换且实时生效 | **通过**：选择器列出 `DeepSeek-V4.1-Flash` / `DeepSeek-V4-Pro` / `DeepSeek Account` 与 Off/Low/High/Max；切换档位后顶栏变为 `deepseek-official/deepseek-flash · max`，切换模型后为 `deepseek-official/deepseek-v4-pro · high`，并成功返回 `final` |
| `N-05-03` 会话页与历史分页 | 新建、搜索、重命名、Fork、角标、能力门控取消、打开会话看到历史 | **部分通过**：列表/搜索/重命名/Fork/运行中角标/停止轮次均可用；**打开会话后历史与缓存正文被清空（F17）**；行标题不可点、只能点「打开」小按钮（F18） |
| `N-05-04` Cache-First 冷启 | 0.2s 级渲染最近对话，DSH 就绪后无重复行校准 | **部分通过**：`native.activity_visible` = **90.7 ms**、`native.cache_first_rendered` = **96.2 ms**，缓存正文与草稿框在 DSH 就绪前可见；**就绪后正文被清空（F17）**，「无重复行校准」无法成立 |
| `N-05-05` 旧 Web Profile 盘点 | 只读展示 `user-data/.dsh` 兼容状态，不读凭据、不改目录 | **通过**：横幅「检测到历史 Web 数据目录（只读）」+ 详情弹窗列出 `--root-projects-- [需迁移/只读保留]`，可关闭；实现仅列目录，不解析凭据 |

## 4. 真机缺陷

| ID | 严重度 | 现象与证据 | 建议 |
|---|---|---|---|
| **F15** | **高** | 凭据页与引导判定把已配置的 DeepSeek 凭据判为未配置。真机凭据库（root 直读 `files/user-data/.dsh/.credentials.yaml`）refs 为 `DEEPSEEK_API_KEY`、`DSH_OPENCODE_GO_ACCOUNT_…`、`DSH_TEST_REF`；界面只查询内置小写 ref（`deepseek_api_key` 等）并全部显示「未配置」；同一时刻聊天实际可用（模型回复 `OK` / `final`），且每次进入聊天都会弹出「尚未检测到可用的 API Key」引导 | 从 `credentials/describe` 全量（或 settings 的 `apiKeyEnv`）发现真实 ref 名，内置列表改用 DSH 规范的 `DEEPSEEK_API_KEY` 等大写名；引导仅在没有可用模型时触发 |
| **F16** | 中 | 「新增自定义引用」保存成功（凭据库里已出现 `DSH_TEST_REF`）但列表不显示，无法查看状态或清除 | 保存后把该 ref 加入本地已知列表并立即刷新；列表渲染 `DescribeCredentialsAsync` 返回的全部条目 |
| **F17** | **高** | 打开已有会话时，缓存正文先显示、DSH 就绪后整表被清空，只留空态；会话日志（`session-fff2fb48…`）确实含 `user/message`（`source.kind="user"`）与 `assistant/message`（`final`）。截图：`pr05-cache-first-t1200.png`（有内容）→ `pr05-reconcile-final.png`（空态） | 校准期间以「历史快照 + 水位」合并而非替换；修正 `session/events` → reducer 的 user/assistant 行映射，确保校准不清空已有行 |
| **F18** | 低 | 会话列表整行不可点击，只能点行内「打开」小按钮；标题点击无响应 | 整行点击即打开会话 |
| **F19** | 中 | 引导弹窗在进入聊天/会话页时反复出现（因 F15 误判），阻塞正常操作与自动化 | 与 F15 一并修复；对同一会话记住「本次已忽略」 |

## 5. 结论与修复进度（修复后真机复测见第 7 节）

- **初次真机（2026-11-23 15:00–15:35）**：`N-05-02`、`N-05-05` 真机通过；`N-05-04` 测点达标（96.2 ms），但暴露阻断与体验缺陷 **F15–F19**。
- **F15–F19 修复已完成源码实现、桌面全套验证，并于 2026-11-23 16:05–16:12 在 Android 17 Release 包真机复测通过（见第 7 节）**：
  - **F15（高）**：修正 `RemoteDshGatewayClient.DiscoverCredentialRefsAsync` 向 `settings/describe` 传空参数 `{}`（对齐 Typert `describe: () => ...` 签名，消除此前携带 `redactSecrets` 被拒绝的问题）；将内置默认与回退引用名统一为 DSH POSIX 大写规范（`DEEPSEEK_API_KEY`、`OPENAI_API_KEY`、`ANTHROPIC_API_KEY`、`GEMINI_API_KEY`、`OPENROUTER_API_KEY`），并校验引用格式；`CheckNeedsCredentialOnboardingAsync` 在模型目录已有可用模型时不再误触发引导。
  - **F16（中）**：`CredentialsViewModel` 与 `CredentialsFragment` 新增自定义引用名格式校验、`SaveCredentialAsync` 写入后立即合并展示及本地 `custom-credential-refs.txt`（仅保存非密钥引用名，绝不落密钥）持久化，重启后仍可查看状态与清除。
  - **F17（高）**：排查 `@deepseek-ai/dsh-api-session-controller` 源码确认 `session/create` 仅复用空白会话、传入已有非空会话 ID 会新建并返回空白 UUID 从而覆写历史。修正 `ChatViewModel.ConnectAndAttachCoreAsync` 对非强制新建的已有 `sessionId` 绕过 `session/create` 直接连接并由 `session/follow(activate: true)` + `session/events` 校准；同时在 `LoadCachedSessionAsync` 与 `ChatFragment.StartupAttachSequenceAsync` 消除缓存加载与实时连接的并发竞态。
  - **F18（低）**：在 `item_session_row.xml` 为卡片与标题容器启用 `clickable`/`focusable`/水波纹并在 `ChatsAdapter` 绑定整行、内容主体与标题直接点击打开会话。
  - **F19（中）**：在 `ChatViewModel` 增加按 `SessionId` 记忆的 `DismissCredentialOnboarding()`（点击“稍后”或取消弹窗即记住当前会话不再反复打断）。
- **门禁状态**：F15–F19 已在真机复测关闭，`N-05-02`/`N-05-03`/`N-05-04`/`N-05-05` 具备勾闭条件；`N-05-01` 仍缺「全新零凭据首次安装」场景（需清数据重装，会丢失本机既有凭据与运行层），由负责人决定是否执行后再勾闭。

## 6. 复现命令（设备侧）

```
adb install -r -d src/DshMobile.Android/bin/Release/net11.0-android/io.github.maiji1990.dshmobile-Signed.apk
adb shell am start -n io.github.maiji1990.dshmobile/crc6407bba3bacbe6d328.MainActivity
# 凭据：更多 → 模型与凭据配置；模型：聊天页 ⋮ → 切换模型；会话：底部「对话」
# Cache-First 测点：adb logcat -s DshMobile:I | findstr native.
```

## 7. 修复后真机复测（2026-11-23 16:05–16:12）

- APK：Release `CC7FD87EF318…3340EBF2`（2026-11-23 15:57 构建），覆盖安装保留数据；桌面门禁 Core 93 / Runtime 834（2 跳过）/ Application 135 / Agent 65 / guest 48 全绿，Release 构建 0 错误。
- 环境：同机 Android 17，DSH `0.2.2-rc.5`，Wi‑Fi 在线。

| 缺陷 | 复测结果 | 证据 |
|---|---|---|
| **F15** 凭据误判 | **已修复**：凭据页显示 `DeepSeek API Key / 已配置 / DEEPSEEK_API_KEY · 来源：file`，掩码 `sk-210d8••…••db82`；其余 Provider 显示大写 ref（`OPENAI_API_KEY` 等）；宿主未启动时提示「需先启动 DSH 后才能查询和写入凭据」而非误报未配置 | fix2-credentials.png |
| **F16** 自定义引用 | **已修复**：新增 `DSH_TEST_REF2` 后立即出现在列表并显示「已配置 · 来源：file」；点「清除」→ 二次确认 → 该 ref 变为「未配置」 | fix2-custom-ref.png / fix2-custom-cleared.png |
| **F17** 校准清空正文 | **已修复**：DSH 停止时打开 `Count from 1 to 200` 会话显示缓存正文；点「启动 DSH」并连接后正文仍在（数值行 5 段 + 标题 1 行，空态 0） | fix2-cache-before.png / fix2-cache-after.png |
| **F18** 行标题不可点 | **已修复**：直接点击会话行标题即可打开该会话 | 16:09 操作 |
| **F19** 引导弹窗反复出现 | **已修复**：进入聊天/会话页不再弹出「尚未检测到可用的 API Key」 | 16:08–16:12 多次进出 |

### 7.1 修复后任务矩阵

| 任务 | 复测结论 |
|---|---|
| `N-05-01` 凭据配置 | **通过**（状态查看 / 保存 / 清除 / 引导判定）；「全新零凭据首次安装」场景因本机已存在凭据未重装验证 |
| `N-05-02` 模型目录与切换 | 通过（上一轮） |
| `N-05-03` 会话列表与历史 | **通过**：列表/搜索/重命名/Fork/运行中角标/停止轮次/打开会话显示历史 |
| `N-05-04` Cache-First | **通过**：`native.cache_first_rendered` = 96.2 ms（上一轮），连接后正文保留、无空态与重复行 |
| `N-05-05` 旧 Web 数据盘点 | 通过（上一轮） |

### 7.2 回归

- 真实回合：发送 `Say DONE` → 用户气泡 1、助手气泡 1、事件卡 0（F5/F11/F12 未回归）。
- 模型与档位仍显示 `deepseek-official/deepseek-v4-pro · high`。

### 7.3 结论

F15–F19 均已在本机 Android 17 Release 包上复测通过，PR-05 的退出门禁「强杀重启 0.2s 显示上次对话并无缝校准」与「无需 WebView 完成凭据配置」在地验证成立。

补充：为验证「零凭据」路径，曾临时清除 DeepSeek 凭据（root 先备份 `.credentials.yaml`，测后原样恢复并确认重新显示「已配置 · 来源：file」）。清除后凭据页正确显示「未配置」，进入聊天**不再误弹引导**；但引导弹窗未出现——因为 DSH 仍提供内置模型目录，而实现约定是「零凭据 **且** 缺少非凭据可用模型」才引导。因此**「全新零凭据安装」的首次引导闭环仍未真机验证**（需清数据/重装，会丢失本机既有凭据与运行层，由负责人决定是否执行）。
