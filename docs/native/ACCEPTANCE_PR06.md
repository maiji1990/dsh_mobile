# PR-06 工作区 Git/Diff、跨会话收件箱与后台通知验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（`N-06-01` ～ `N-06-04`）
> 分支：`feat/native-pr06-git-inbox-notifications`
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 无真机证据不勾 `[x]`）。
> 真机状态：**已真机验证（2026-11-23，Android 17）**——Git/Diff、跨会话收件箱与后台问答通知通过；审批类通知动作（`approval/policy=never`）、超长/二进制 Diff 与工具卡路径点击直达未覆盖，见第 4 节。

## 1. 本轮实现清单（全 C# + 全 XML 声明式布局）

| 任务项 | 主要代码与布局资产 | 说明 |
|---|---|---|
| `N-06-01`｜只读工作区 Git 与 Diff 服务 | `WorkspaceGitService.cs`、`ProotGitCommandExecutor.cs` | 复用 `WorkspaceFileSystem` 的 `.dsh/` 双向不可触与符号链接拒绝策略；校验仓库根/相对路径不逃逸工作区；强制 Diff 大小（128 KiB）与行数（1500 行）上限并标注截断；支持经 `GuestCommandLine` 单引号运行 PRoot `git` 与离线可注入假测试 |
| `N-06-02`｜`FilesFragment` Git 页签与 Diff 查看器 | `fragment_files.xml`、`item_git_change.xml`、`item_git_diff_line.xml`、`DiffLineView.cs`、`GitAdapters.cs`、`FilesFragment.cs`、`WorkspacePathExtractor.cs`、`item_chat_tool_call.xml`、`ChatTimelineAdapter.cs`、`ChatFragment.cs` | 全 XML 声明文件/Git 切换、`DiffLineView`（旧/新行号、增删高亮、截断横幅、主线程零解析）；支持从原生聊天工具卡点击安全工作区路径直达 `MainActivity` 预览或 Git Diff |
| `N-06-03`｜跨会话待办收件箱与角标 | `InboxViewModel.cs`、`sheet_inbox.xml`、`item_inbox_request.xml`、`InboxBottomSheet.cs`、`activity_main.xml`、`MainActivity.cs`、`ChatActivity.cs` | 聚合 `RequestsEngine` 全局待审批与待问答请求；在 `MainActivity` 顶部横幅与 `nav_chats`（BottomNav + NavigationRail）实时展示角标；点击收件箱条目直达 `ChatActivity` 对应会话并定位交互卡片；修复 `ChatViewModel.OnGatewayForwardedEvent` 按 `AgentId` 归属跨会话请求 |
| `N-06-04`｜后台审批 / 问答系统通知通道 | `BackgroundRequestNotificationCoordinator.cs`、`AgentRequestNotifications.cs`、`DshMobileApplication.cs` | 通过 `IActivityLifecycleCallbacks` 追踪前后台；仅后台收到 `Waiting` 请求时向独立高优先级渠道 `dshmobile_agent_requests` 发通知（经 `SecretRedactor` 脱敏，绝不含密钥或敏感参数全文）；快捷按钮使用 `Exported = false` 的 `AgentRequestNotificationReceiver` + `PendingIntentFlags.Immutable` + 128-bit 一次性随机数，经 `RequestsEngine.DecideApprovalImmediateAsync` 原子单写者认领；与 Relay `ui_*` 通道（`dshmobile_relay_approval`）物理隔离 |

## 2. 桌面自动化测试与 Release 构建验证

| 检查项 | 结果 | 说明 |
|---|---|---|
| `dotnet test tests/DshMobile.Core.Tests` | **通过：109 / 109** | 含 `WorkspaceGitServiceTests`（`.dsh/` 拒绝、`..`/绝对路径越界、仓库根逃逸、符号链接与 `.git` 指针拒绝、行号计算与大小/行数截断、未跟踪文件安全读取与二进制检测） |
| `dotnet test tests/DshMobile.Runtime.Tests` | **通过：836 / 838（2 跳过）** | 含 `ProotGitCommandExecutorTests`（`GuestCommandLine` POSIX 单引号转义、超时与缺失 `git` 错误码分类）与 `SourceHygieneTests`；2 项为既有的 Windows 平台跳过用例（`XdgUserDirShimTests`） |
| `dotnet test tests/DshMobile.Application.Tests` | **通过：135 / 135** | 既有编排与状态机全回归 |
| `dotnet test tests/DshMobile.Agent.Tests` | **通过：78 / 78** | 含 `InboxAndNotificationTests`（跨会话聚合、标题刷新、后台通知脱敏、并发防双发原子认领、拒绝零执行、非导出 Receiver 与 `Immutable` 契约）及 5 组 CI 门禁 |
| `node tooling/run-node-tests.mjs tests/guest` | **通过：48 / 48** | guest 工具链与 `ui_*` 契约回归 |
| `dotnet build src/DshMobile.Android -c Release` | **通过：0 错误 / 73 警告** | `TrimMode=full` 警告数与 PR-05 基线（73）持平（≤75），PR-06 新增文件 0 新增告警 |

## 3. 真机验证清单与进度（Android 17 arm64，未关闭项保持待验证）

| 场景 | 验证要点 | 当前状态 |
|---|---|---|
| `N-06-01` / `N-06-02` 工作区 Git 与 Diff | 在真实含 Git 仓库的工作区中切换 Git 页签、查看分支与变更列表、打开 Diff 查看行号与高亮、超长 Diff 截断提示、从聊天工具卡点击路径直达预览/Diff | **部分通过（2026-11-23）**：Git 页签、分支/变更列表、Diff 查看器通过；超长 Diff 截断、二进制 Diff 与工具卡点击路径直达**未覆盖**；另登记交互缺陷 F23 |
| `N-06-03` 跨会话收件箱与角标 | 多会话存在待审批/待问答时，`MainActivity` 顶部横幅与 `nav_chats` 角标实时计数；打开 `InboxBottomSheet` 点击条目跳转 `ChatActivity` 并定位卡片 | **真机通过（2026-11-23）**：已通过问答请求端到端验证（见第 4 节） |
| `N-06-04` 后台审批/问答通知（T04） | App 退居后台后收到 `approval/request` 或 `user-questions/request` 弹出高优先级脱敏通知；通知栏点击「允许一次/拒绝」与页内卡片并发点击至多生效一次；拒绝后确认零工具执行；验证与 Relay `ui_*` 通知互不串台 | **部分通过（2026-11-23）**：后台问答通知通过；审批类通知快捷动作与拒绝零执行因 DSH `approval/policy=never` 无法构造 `approval/request` 样本，**未验证** |

---

## 4. Android 17 真机验收（2026-11-23 17:50–18:12）

- 设备：Xiaomi M2007J1SC（LineageOS，Android 17 / API 37，arm64）；APK：Release `B6E7FD99…594686`（0 错误 / 113 警告）；DSH `0.2.2-rc.5`，Wi‑Fi 在线。
- 测试用工作区仓库：以 host 侧 git 仓库（1 次提交 + 1 个已修改文件 + 1 个未跟踪文件）经 `adb push` 到 `user-data/pr06-repo`。

| 场景 | 结果 | 证据 |
|---|---|---|
| `N-06-01/02` Git 页签与变更列表 | **通过**：文件页出现「文件 / Git」切换；Git 页显示 `分支：master`、`仓库根：/root/projects/pr06-repo`、`M README.md 未暂存`、`M hello.txt 未暂存`、`? new-file.txt 未跟踪`，含刷新与搜索框 | `pr06-git.png` / `pr06-git2.png` |
| Diff 查看器 | **通过**：点变更条目进入 Diff，显示 `diff --git` 头、`@@ -1,3 +1,4 @@` 高亮、旧行号 1–3 红底 `-`、新行号 1–4 绿底 `+`，并有「返回变更列表」「预览文件」入口 | `pr06-diff2.png` |
| `N-06-03` 跨会话收件箱 | **通过**：在会话中触发 `ask_user_question`（待回答问题卡）后，`MainActivity` 顶部横幅显示「共有 1 项待处理（审批 0 · 问答 1）」，会话行显示「当前 / 运行中 / 待确认 1」；「查看待办」打开收件箱，列出「问答 · Count from 1 to 200 · 待处理 · Pick a color · Which color…」，「跳转会话」直达该会话且卡片可见 | `pr06-inbox-banner.png` / `pr06-inbox-sheet.png` / `pr06-inbox-jump.png` |
| `N-06-04` 后台通知 | **通过（问答）**：退到后台后发出通知，渠道 `dshmobile_agent_requests`（importance=4，命名「Agent 审批与问答」），标题「Agent 等待回答 · Pick a color」、正文为问题文本、`bigText` 含会话名，`vis=PRIVATE`（锁屏不展示内容） | `pr06-notification.png` / `pr06-notification-shade.png` |
| `N-06-04` 审批通知快捷动作 | **未验证**：本机 DSH 会话以 `danger-full-access` 创建（`approval/policy=never`），`/permission workspace-write` 只改 preset 不产生审批请求；工具在 workspace 沙箱后端缺失时直接失败，无法构造 `approval/request`，故「允许一次 / 拒绝」通知按钮与「拒绝后零执行」未取证 | — |
| 其他未覆盖 | 超长 Diff 截断、二进制 Diff、聊天工具卡点击路径直达预览/Diff（需含文件路径的工具调用样本） | — |

### 4.1 真机发现

- **F22（低，构建门禁）**：本分支全量 Release 构建为 **113 条警告**，高于文档记录的 73 条；PR-06 新增代码贡献 4 条 `CS0618`/`CS0672`（`FilesFragment.StartActivityForResult`/`OnActivityResult` 过时），其余为既有告警在不同增量范围内的重现。建议按统一的 clean-build 口径记录基线。
- **F23（低，交互）**：Git 变更行与文件行的点击区域较小，实测点击后曾误触到其它页面；建议整行可点并加水波纹（与 PR-05 的 F18 同类）。

### 4.2 结论

PR-06 的 Git 状态/变更列表/Diff 查看器、跨会话收件箱（横幅 + 角标 + 详情 + 跳转）与后台问答通知已在 Android 17 真机通过；审批类通知动作与「拒绝零执行」因当前 DSH 审批策略为 `never` 无法构造样本，仍为**待验证**。
