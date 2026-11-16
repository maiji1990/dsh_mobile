# ADR-0021：产品级界面（底部导航 + Material 3 + 动态取色 + 开发者选项）

- 状态：已接受（2026-11-15；项目负责人：「现在的界面只适合测试使用，我需要一个产品级 UI」；选定底部导航 4 栏、跟随系统 + 动态取色、
  开发工具放进「设置 → 开发者选项」、所有页面一次重做）
- 关联：AGENTS.md 规则 5（不伪造成功）、规则 11（安全不得削弱）、规则 12（Material 3；主题替换为独立 UI 切片并附截图级验收）；
  [ADR-0011](0011-relay-approval-surfaces-and-a11y-ownership.md)（审批卡与 `FLAG_SECURE`）、[ADR-0012](0012-csharp-ssh-client-sshnet.md)（主机指纹确认）

## 背景

迁移期间的主界面是验收面板：一屏十几个平铺按钮（准备 / 导入 / 启动 / 停止 / 诊断 / 模式探测 / PTY 自检 / 回滚 / 文件 / 终端 / 插件 /
手机操控 / 远程…）加一个滚动日志，状态从日志里读。各子页是代码拼出的平台 `Button` / `EditText`，主题只有 C# 终端用了 Material 3。
这适合逐项验收，不适合日常使用：主路径（导入 → 启动 → 打开 DSH）淹没在开发工具里，出错只能看日志。

## 决策

1. **导航**：单个 `MainActivity` 外壳 + `BottomNavigationView` 4 栏：首页 / 终端 / 文件 / 更多。各栏是 AndroidX `Fragment`，
   以 show / hide 切换（终端会话与 WebView 不因切栏重建）；返回键先交给当前栏（终端 / 文件的上一级），再回首页，最后退出。窗口宽度 ≥ 600dp（横屏手机、平板）时底部导航换成左侧 `NavigationRailView`（Material 3 navigation rail）；
   `MainActivity` 自己处理旋转（终端 WebView 不重建），所以两者在同一布局里，按 `ScreenWidthDp` 显示其一。终端页在横屏且键盘弹出时
   隐藏标题栏与会话标签，把行数让给终端。
   二级页（运行环境、插件、手机操控、远程主机、开发者选项、DSH 网页）是 `DshActivity` 子类：顶栏带返回、edge-to-edge、
   按系统栏 / 刘海 / 键盘内边距排版。
2. **首页状态卡**：状态只来自运行时 journal（`RuntimeSnapshot`）与 `BridgeHost` 记下的当前操作 / 上次结果，由纯模型
   `DshMobile.Application.Ui.HomeStatus` 映射为「阶段 + 主按钮 + 次按钮」（`HomeStatusTests` 19 例锁定），不解析日志。
   主路径：未安装 → 导入运行环境包（导入已包含安装内置 DSH）→ 启动 → 打开 DSH；失败时卡片显示稳定错误码与原因并给出重试，
   不显示成功（规则 5）。复制选中文件失败、找不到包分别报 `RUNTIME_BUNDLE_UNREADABLE` / `RUNTIME_BUNDLE_NOT_FOUND`。
3. **主题**：`Theme.DshMobile`（`Theme.Material3.DayNight.NoActionBar`）跟随系统深浅色；Android 12+ 用
   `DynamicColors.ApplyToActivitiesIfAvailable` 取壁纸色，其余系统用以 `#0061A4` 为种子的品牌配色（`values/colors.xml` 与
   `values-night/colors.xml` 给出全部 M3 色彩角色）。所有页面、审批确认页与审批卡共用该主题，原 `Theme.DshMobile.Material3` 删除。
4. **开发者选项**：诊断、设备 / 运行环境信息、DSH 模式探测、PTY 自检与操作日志移入「更多 → 开发者选项」，默认隐藏，
   在「关于」连点 7 次开启（`UiPrefs`，可在该页关闭）。手机操控页的「执行面偏好」也只在开启后出现。「安装内置 DSH」（`runtime.prepare`：已装版本不低于内置时保持不变，
   结果行如实写出「已安装 / 保持不变 / 失败与错误码」，见 `PrepareOutcome`）与
   回滚属于运行环境维护，放在「更多 → 运行环境」，回滚前确认。
5. **图标**：Material Icons（Apache-2.0）的 32 个矢量图形，以 `res/drawable/ic_*.xml` 收录，登记于 `docs/UPSTREAMS.md`；
   不使用 DSHBox 的任何品牌资产（规则 4）。

## 不变 / 不做

- 行为逻辑不变：各页调用的运行时 / Relay / SSH / 插件接口与确认对话框和改版前相同；只改布局、文案与入口位置。
- 安全不变（规则 11）：手机操控页的审批卡仍置于页顶并在显示期间给窗口加 `FLAG_SECURE`，`PageCardShown` 仍在 `OnResume` /
  `OnPause` 切换；无障碍 / 悬浮窗 / Shizuku 仍只打开系统或 Shizuku 自己的授权页，由用户本人决定；远程主机首次连接仍须逐字核对
  指纹后才固定，私钥不可导出；插件安装 / 卸载仍逐次确认。
- 终端页沿用 PR-06 的 WebView 封锁（只放行 `/terminal/**`、CSP 禁网络、WebMessagePort 传字节），源码契约测试改为检查
  `Terminal/TerminalFragment.cs`。
- 不引入 Jetpack Compose 或新的 NuGet 包（AppCompat / Fragment / RecyclerView 已由 Material 1.14.0.6 传递引入）。

## 验证

- 单测：`HomeStatusTests`（19 例）；Core 35 / Runtime 741 / Application 102 / guest 37 全部通过；Android 项目 0 警告 0 错误。
- 模拟器（API 37，x86_64，浅色 + 动态取色）截图：首页（已停止 / 出错）、终端（无会话）、文件、更多（含开发者选项开启后）、
  运行环境、插件、手机操控、远程主机、开源许可、开发者选项。该模拟器为 x86_64，arm64 运行层在其上以 SIGILL 退出
  （`DSH_EXITED`，exit 255），因此「运行中 / 打开 DSH」状态与终端会话未在本次截图中出现。
- **真机（2026-11-16，Honor PTP-AN10 / Android 17，浅色 + 动态取色）**：逐页截图与操作通过——启动 → 就绪 → 打开 DSH WebUI → 停止（无残留进程）、
  终端双会话 / CTRL+c=130 / 切栏重放 / 键盘弹出时底栏隐藏、文件、插件列表、手机操控 / 远程主机 / 运行环境各页、开发者选项（诊断、PTY 自检 PASS、
  模式探测与此前真机一致）；覆盖安装时 ADR-0019 数据迁移首次真机通过。证据：
  [2026-11-16-PTP-AN10-A17-product-ui.md](../../migration/parity/2026-11-16-PTP-AN10-A17-product-ui.md)。
- **真机续（同日，负责人本人开启无障碍 / 深色 / 自动旋转并作答审批卡）**：导入自建 0.2.0 包与旧 0.1.0 包、无槽位回滚报
  `BUNDLE_NO_ROLLBACK_SLOT`、手工建 `previous/` 后回滚成功并可启动；中继开关、页内与悬浮审批卡（`FLAG_SECURE`）、只读自检 exit 0、
  超时 / 无障碍缺失如实报错；深色模式逐页；横屏（据此加入 navigation rail 与终端紧凑模式）。同时修正：安装内置 DSH 的结果如实显示、
  可用空间读错挂载点、中继状态显示英文枚举名。证据：[2026-11-16-PTP-AN10-A17-runtime-relay-dark-landscape.md](../../migration/parity/2026-11-16-PTP-AN10-A17-runtime-relay-dark-landscape.md)。
- **未验证**：远程主机连接；审批卡显示期间 Agent 写操作挂起（只有单测）；平板 / 折叠屏的 rail；新版本横屏转回竖屏的底栏交接。
