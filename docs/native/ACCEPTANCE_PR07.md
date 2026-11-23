# PR-07 原生插件目录与受控管理（L0 / L1）验收报告

- **任务范围**：`N-07-01` ～ `N-07-03`（`docs/plan/DSH_Mobile_原生Android重构任务清单.md`）
- **分支**：`feat/native-pr07-plugins-l0-l1`（基于 `main` / PR-05 `e9c07de`）
- **架构依据**：[ADR-0033](../ADR/0033-native-agent-client.md)、[ADR-0028](../ADR/0028-remove-native-terminal-and-plugin-pages.md)（修订边界：原生侧仅负责 L0 状态清单与 L1 受控装卸/开关，不做 L2 动态 Schema 表单，不内嵌 WebView）
- **真机验收状态**：**待真机验证**（本分支已完成全量代码实现、全 XML Material 3 界面、`Release` 裁剪构建及桌面单元测试，尚未在 Android 17 真机运行现场 DSH 联调与交互回归）。

---

## 1. 交付清单

### 1.1 `N-07-01`｜`PluginInventoryService` 与受控变更服务（L0 / L1）

1. **Runtime 受控插件层（`src/DshMobile.Runtime/Plugins/`）**：
   - `PluginCommandRules.cs`：严格包名白名单校验（仅允许合法 unscoped/scoped npm 包名及可选精确 `@version`，使用 `Trim(' ')` 保留控制字符供校验拦截，拒绝 `;`、`&&`、`|`、`$(...)`、`..`、空格与 `-` 开头 flag 注入；受保护内置包与条目只读不可误删/误停）；所有 CLI 命令通过 `GuestCommandLine` 单引号转义生成 argv（`dsh plugin --profile web list --json --depth 0` / `add` / `remove`）。
   - `PluginPatchEditor.cs`：对 `profiles/web/cordis.patch.yml` 实行只针对目标条目 `disabled` 字段的原子更新（`.tmp` 写入 + `File.Move` 覆盖，保留现有注释与 `!!js` 标签）。
   - `GuestPluginService.cs`：封装 guest 内受控列表、安装、卸载、启停操作，解析 `pnpm list --json` 输出并按安全边界探测插件 `package.json` 是否声明自定义 WebUI；失败诊断经 `LogRedactor` 脱敏。
2. **Application 宿主编排扩展（`src/DshMobile.Application/`）**：
   - `Abstractions.cs`：`RuntimeOptions` 新增可选 `GuestShellRunner` 测试接缝。
   - `RuntimeCoordinator.cs`：新增 `RestartWebHostAsync`、`ListProfilePluginsAsync`、`InstallProfilePluginAsync`、`UninstallProfilePluginAsync` 与 `SetProfilePluginEnabledAsync`。
3. **Agent 纯托管领域与视图模型（`src/DshMobile.Agent/`，零 Android 引用）**：
   - `Domain/PluginModels.cs`：`PluginPackageValidator`、`PluginInstalledPackageView`、`PluginEntryView`、`PluginInventorySnapshotResult`、`PluginMutationResult`、`IPluginOperations`、`NoopPluginOperations`。
   - `Engines/PluginInventoryService.cs`：合并 L0（`IPluginGateway.GetPluginInventoryAsync()` 即 `pluginInventory/list` + 可选受控 `dsh plugin --profile web list --json`），错误摘要经 `SecretRedactor.RedactAndTruncate` 脱敏；统一执行 L1 包名校验、变更派发并置位 `RequiresDshRestart = true`，宿主重启时通过 `NotifyHostRestarted()` 清除重启横幅。
   - `Presentation/PluginsViewModel.cs`：`PluginsUiState`、`IPluginsActions`、`PluginsViewModel`（不读取异常 `.Message`，仅输出脱敏安全描述或稳定错误码）。

### 1.2 `N-07-02`｜`PluginsActivity` / `PluginsFragment`（Material 3 全 XML 布局）

- **全 XML 布局（`src/DshMobile.Android/Resources/layout/`）**：
  - `activity_plugins.xml`
  - `fragment_plugins.xml`（含置顶「需要重启 DSH 生效」横幅及重启快捷按钮、搜索过滤框、状态条、`RecyclerView` 清单、加载/空态视图）
  - `item_plugin_entry.xml`（插件名称/ID、版本与状态标签、脱敏报错摘要、自定义 WebUI 提示、启用/停用与卸载入口）
  - `dialog_plugin_install.xml`（受控 npm 包名/精确版本输入框）
- **原生页面与桥接（`src/DshMobile.Android/`）**：
  - `Plugins/PluginsActivity.cs`、`Plugins/PluginsFragment.cs`、`Plugins/PluginsAdapter.cs`
  - `Bridge/RuntimePluginOperations.cs`、`Bridge/BridgeHost.cs`、`Bridge/AgentConnectionScope.cs`（宿主启动握手完成后清除 `RequiresDshRestart`；宿主停机/重启同步刷新状态）
  - `More/MoreFragment.cs`：增加「插件管理」入口跳转 `PluginsActivity`（未启动 DSH 时如实提示需先启动 DSH）
  - `Ui/OperationNames.cs`、`Resources/values/strings.xml`：新增重启操作中文标签与插件页面文案；自定义 WebUI 插件如实提示「此插件后端正常运行，但暂无原生管理界面」。

### 1.3 `N-07-03`｜回归测试与边界核对

- `tests/DshMobile.Runtime.Tests/GuestPluginServiceTests.cs`：包名/注入参数拒绝（零进程启动）、`pnpm list --json` 解析、`cordis.patch.yml` 原子写回与注释保留、自定义 WebUI 清单探测、诊断日志脱敏。
- `tests/DshMobile.Application.Tests/RuntimeCoordinatorPluginTests.cs`：协调器 L0/L1 受控命令路径与测试接缝验证。
- `tests/DshMobile.Agent.Tests/Presentation/PluginInventoryAndViewModelTests.cs`：非法包名拒绝（零操作调用）、装卸失败后状态恢复不崩溃、`RequiresDshRestart` 置位与宿主重启清除、`SecretRedactor` 凭据/Token 脱敏、零 L2 Schema 表单 / 零 WebView 边界校验，以及全部 5 组 CI 架构安全门禁（`Quality/*`）。

---

## 2. 边界核对（ADR-0033 & ADR-0028）

| 检查项 | 核对结果 |
|---|---|
| 纯托管边界 | `DshMobile.Agent` 保持纯 `net11.0`，零 `Android.*` / `Java.*` 引用（通过 `ArchitectureBoundaryTests`） |
| 零动态 UI 拼建 | 所有新增原生页面、列表条目、安装弹窗均在 `Resources/layout/*.xml` 中定义，C# 仅做控件绑定与事件响应 |
| 零 WebView / 零 L2 Schema | `Plugins/` 与 XML 布局中无任何 `WebView`；不解析插件动态配置 Schema，自定义 WebUI 插件固定显示「此插件后端正常运行，但暂无原生管理界面」 |
| 命令注入防护 | 全链路经 `PluginPackageValidator` / `PluginCommandRules` 严格白名单放行（控制字符不静默 `Trim()`），并通过 `GuestCommandLine` 逐 token 单引号转义构造 argv |
| 错误与凭据脱敏 | 诊断摘要依次经 `LogRedactor` 与 `SecretRedactor.RedactAndTruncate` 脱敏，`PluginsViewModel` 遵守安全日志门禁 |

---

## 3. 桌面测试与构建基线（已实测通过）

> 以下结果均于最后一次源码定稿后实测执行并全绿通过（注：`Runtime.Tests` 含既有 Windows 平台跳过的 `XdgUserDirShimTests` 2 项）：

| 检查项 | 命令 | 结果 |
|---|---|---|
| Core 单测 | `dotnet test tests/DshMobile.Core.Tests` | 93 通过 / 0 失败 / 0 跳过（总计 93） |
| Runtime 单测 | `dotnet test tests/DshMobile.Runtime.Tests` | 872 通过 / 0 失败 / 2 跳过（总计 874） |
| Application 单测 | `dotnet test tests/DshMobile.Application.Tests` | 138 通过 / 0 失败 / 0 跳过（总计 138） |
| Agent 单测（含 5 组 Quality 门禁） | `dotnet test tests/DshMobile.Agent.Tests` | 80 通过 / 0 失败 / 0 跳过（总计 80） |
| Guest Node 测试 | `node tooling/run-node-tests.mjs tests/guest` | 48 通过 / 0 失败（退出码 0） |
| Android Release 裁剪构建 | `dotnet build src/DshMobile.Android -c Release` | 0 错误；总计 113 条既有警告，**PR-07 新增/修改文件 0 新增警告** |

---

## 4. 真机验收矩阵（待真机验证）

| 编号 | 验收场景 | 状态 | 备注 |
|---|---|---|---|
| `PR07-D01` | 「更多 → 插件管理」入口及未启动 DSH 提示 | **待真机验证** | 需 Android 17 Release 包实测冷启动/未启动分支 |
| `PR07-D02` | L0 清单合并展示（`pluginInventory/list` + 受控 `dsh plugin list --json`）与状态标签 | **待真机验证** | 需真实 `@deepseek-ai/dsh@0.2.2-rc.5` 运行态联调 |
| `PR07-D03` | L1 受控安装 / 卸载 / 启停二次确认弹窗与非法包名报错提示 | **待真机验证** | 需验证真实 PRoot 内 pnpm 执行与回写 `cordis.patch.yml` |
| `PR07-D04` | 变更成功后置顶「需要重启 DSH 生效」横幅与点击「立即重启 DSH」后横幅自动清除 | **待真机验证** | 需真机观察 `RestartWebHostAsync` 与握手清除闭环 |
| `PR07-D05` | 携带自定义 WebUI 插件展示「此插件后端正常运行，但暂无原生管理界面」（无 WebView） | **待真机验证** | 需真机装入带 `web` 清单入口插件验证文案与零 WebView |
