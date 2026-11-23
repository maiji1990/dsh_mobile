# PR-06 工作区 Git/Diff、跨会话收件箱与后台通知验收记录

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md)（`N-06-01` ～ `N-06-04`）
> 分支：`feat/native-pr06-git-inbox-notifications`
> 规则约束：严格遵守 [AGENTS.md](../../AGENTS.md) 规则 1–12（尤其规则 5 不伪造成功、规则 9 无真机证据不勾 `[x]`）。
> 真机状态：**待真机验证**（本轮仅完成纯托管架构实现、桌面全套单测与 `DshMobile.Android` Release 裁剪构建验证）。

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

## 3. 待真机验证清单（Android 17 arm64，无证据不勾 `[x]`）

| 场景 | 验证要点 | 当前状态 |
|---|---|---|
| `N-06-01` / `N-06-02` 工作区 Git 与 Diff | 在真实含 Git 仓库的工作区中切换 Git 页签、查看分支与变更列表、打开 Diff 查看行号与高亮、超长 Diff 截断提示、从聊天工具卡点击路径直达预览/Diff | **待真机验证** |
| `N-06-03` 跨会话收件箱与角标 | 多会话存在待审批/待问答时，`MainActivity` 顶部横幅与 `nav_chats` 角标实时计数；打开 `InboxBottomSheet` 点击条目跳转 `ChatActivity` 并定位卡片 | **待真机验证** |
| `N-06-04` 后台审批/问答通知（T04） | App 退居后台后收到 `approval/request` 或 `user-questions/request` 弹出高优先级脱敏通知；通知栏点击「允许一次/拒绝」与页内卡片并发点击至多生效一次；拒绝后确认零工具执行；验证与 Relay `ui_*` 通知互不串台 | **待真机验证** |
