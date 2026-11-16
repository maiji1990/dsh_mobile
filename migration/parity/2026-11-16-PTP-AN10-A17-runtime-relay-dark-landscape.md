# 2026-11-16 真机验收（续）：运行环境导入 / 安装内置 DSH / 回滚、手机操控中继与审批卡、深色模式、横屏

- 设备：Honor PTP-AN10，Android 17（SDK 37），arm64-v8a；动态取色生效
- 构建：`ui/product-material3`（同日上午验收之后），Debug，`dotnet build -t:Install -p:AdbTarget=-s%20<设备>`，覆盖安装
- 接上午的验收：[2026-11-16-PTP-AN10-A17-product-ui.md](2026-11-16-PTP-AN10-A17-product-ui.md)
- 系统设置（无障碍服务、深色模式、自动旋转）与手机方向都由负责人本人操作；审批卡由负责人本人作答；没有代为接受任何系统对话框
- 截图在本地（未入库：其中含设备与用户信息）

## 1. 运行环境：导入 / 安装内置 DSH / 回滚

运行环境包经 `adb push` 放进 `/sdcard/Download`，在「更多 → 运行环境 → 导入运行环境包」里用系统文件选择器选取。

| 操作 | 结果 |
|---|---|
| 导入自建包 `dshmobile-runtime-debian-arm64-0.2.0.zip`（93 MiB，ADR-0020） | 复制 0.2 s；`install: ok in 5404 ms (base=10148, node=5781, android-side=11)`；逐项核对 `Agree`；`runtime.prepare: ok (… DSH kept at 0.2.1-rc.5)`；共 9.4 s。journal `Ready → Staging (import requested) → Ready (import done)`；`runtime-profile.json` 为 `dshmobile-runtime-debian-arm64` 0.2.0。导入中页面显示「正在导入运行环境… extract node · 33%」，结束后显示「「导入运行环境」已完成」 |
| 回滚（此时没有 `previous/`）→ 确认 | 页面显示「「回滚运行环境」未成功 · BUNDLE_NO_ROLLBACK_SLOT: no previous runtime body to roll back to」；`runtime-current` 未改动 |
| 安装内置 DSH（原「修复 DSH」） | `DSH kept at 0.2.1-rc.5`：已装版本不低于内置版本，没有重装（见发现 1） |
| 手工 `run-as mkdir runtime-current/previous`（测试准备，见发现 2），再导入旧包 `dshapp-runtime-debian-arm64-0.1.0.zip`（192 MiB） | `install: ok in 7367 ms`；`Agree`；共 12.7 s；0.2.0 的 base / node / android-side / profile 移入 `previous/`，当前为 0.1.0 |
| 回滚 → 确认 | `rollback: restored base, node, android-side, runtime-profile.json; runtime=True (ok)`；当前 profile 回到 0.2.0，0.1.0 移入 `runtime-current/failed/`，`previous/` 变空 |
| 回滚后启动 DSH | `host: ready in 16.2 s`；`start: ok sandbox=Running dsh=Ready version=0.2.1-rc.5`；WebUI 可打开 |
| 全程 `user-data` | 未改动（导入 / 回滚只动 `runtime-current` 下的层） |

## 2. 手机操控中继与审批卡

| 操作 | 结果 |
|---|---|
| 「手机操控中继」开关（DSH 运行中） | `c# relay (guest) start: ui_* scripts staged, ui_* layer registered`；行显示「已开启 · 信箱运行中」（修正前显示英文 `Running`，见发现 4）；「DSH 工具」行「已注册，需重启 DSH 才会加载」，重启 DSH 后为「已在当前 DSH 中加载」 |
| 无障碍未开启时「只读自检」 | 弹出「需要无障碍服务」：请求已被拒绝，须由本人在系统设置开启，App 不代为开启；输出 `ui_snapshot: ERROR PROVIDER_UNAVAILABLE / E_GATE_SYSTEM_MISSING`，`UICHECK exit 4`（不伪造成功） |
| 负责人开启无障碍后「只读自检」（页面在前台） | 页内审批卡出现，窗口带 `FLAG_SECURE`（`dumpsys window`：`AutomationActivity fl=… SECURE …`；`screencap` 被拒，截不到卡片）。第一次请求在负责人作答前过期（`page card: ui.snapshot expired`），自检重新发起；负责人允许 → `ui.snapshot -> ALLOW_SESSION`、`ui.node -> ALLOW_SESSION`，`relay selftest: exit=0`。卡片收起后 `SECURE` 标志撤销，可再截图 |
| 终端栏里运行 `node /opt/dshmobile-cache/dshmobile/ui-facade-check.mjs`（手机操控页不在前台） | 悬浮审批卡出现：`ty=APPLICATION_OVERLAY`、`fl=… SECURE …`。第一次超时未答 → `APPROVAL_REQUIRED / E_AWAITING_CONSENT`（`retryable: true`），`UICHECK exit 5`；再次运行、负责人允许 → `UICHECK exit 0`，快照 `org.example.dshmobile` 20 个元素，`ui_get_element e1: ok` |
| 覆盖安装 App | 系统关闭了本 App 的无障碍服务（Android 对更新包的标准行为），需本人重新开启；运行中的 DSH 被杀，下次启动日志 `journal=Failed/Stopped … RUNTIME_INTERRUPTED` → 读盘后 `Ready/Stopped`，`ps` 无残留 |

未覆盖：Agent 写操作在审批卡显示期间被挂起（单测覆盖，本次没有写操作请求）；通知栏审批面。

## 3. 深色模式（负责人在系统中开启）

首页（已停止 / 运行中）、终端、文件、更多、运行环境、插件、手机操控、远程主机、开发者选项、DSH 网页逐页截图：全部切到深色 Material 3 配色，文字与卡片对比正常；DSH 自己的网页也跟随深色。切换深浅色时 `MainActivity` 重建，终端会话重放。

## 4. 横屏（负责人开启自动旋转并转动手机）

| 页面 | 修正前 | 修正后 |
|---|---|---|
| 外壳 | 底部导航栏占约 1/4 屏高；首页状态卡的「打开 DSH」被压到底栏下 | 宽度 ≥ 600dp 时改用左侧导航栏（Material 3 navigation rail），状态卡完整可见；竖屏选中「终端」后转为横屏，导航栏上仍选中「终端」（横屏转回竖屏的交接本次未做） |
| 终端 | 约 4 行；弹出键盘后终端 0 行（标题栏和会话标签占满键盘上方） | 9 行（`stty size` = `9 78`）；横屏弹出键盘时隐藏标题栏与会话标签，保留当前行与辅助键栏（`1 78`），收起键盘后恢复 `9 78` |
| 文件、更多、插件、开发者选项、DSH 网页 | 可用 | 同左；内容避开刘海侧 |

旋转不重建 `MainActivity`（终端 WebView 与会话保留）。

## 发现与处理

1. **「修复 DSH」名不副实**：按钮调用 `runtime.prepare`，沿用旧实现的版本仲裁（已装版本不低于内置即保持不变），并不会重装同版本的 DSH 层，页面却显示「已完成」；旧 prepare 在 DSH 层安装失败时也返回成功。行为由向量锁定，未改；改为如实说明：
   按钮改名「安装内置 DSH」并写明规则；结果行显示「已装 DSH x 不比内置旧，保持不变」或「已安装 DSH x」；DSH 层安装失败 / 层缺失 / APK 无内置 DSH 时显示失败与错误码（`PrepareOutcome`，`PrepareOutcomeTests` 9 例）。
2. **回滚槽位在实际使用中永远不会产生**：没有任何代码创建 `runtime-current/previous/`；DSH 层安装只在它已存在时才写 `previous/dsh`（而本次 DSH 层没有被重装），导入时它不存在就直接删除旧层（继承旧 `swapRuntimeBody`，向量 `install-scenarios.json` 锁定）。所以「回滚到上一版」在真实设备上总是如实报 `BUNDLE_NO_ROLLBACK_SLOT`。本次靠手工建目录验证了回滚成功路径；是否改为导入前先建 `previous/`（多占一份运行层的空间）需单独决定，未改。
3. **可用空间读错**：显示 7619.5 MB，实际 261 GB。`RuntimeStatusReader.FreeBytes` 按路径前缀从 `DriveInfo.GetDrives()` 挑挂载点，在 App 的挂载命名空间里选中了数据隔离用的 tmpfs（约为内存的一半）。已改为对路径本身 `statvfs`（`new DriveInfo(path)`，与旧实现 `StatFs(path)` 一致），显示 260.0 GB；大小格式增加 GB 档。`RuntimeFreeBytesTests` 2 例。
4. **中继状态行显示英文枚举名** `Running`：改为「正在开启 / 已开启 / 部分可用」。
5. 运行环境页底部说明称运行环境包「在 GitHub Release 发布」，实际尚未发布：已改为「由本项目的构建脚本生成」。
6. 上午记录的残留插件 `@local/browser-use` 已不在列表里：`profiles/web/package.json` 在 08:12 被改写，早于本次第一次导入（08:18），与导入 / 回滚无关（二者不触及 `user-data`）。

## 仍未验证

- 远程主机连接（没有可用的测试 sshd）
- 审批卡显示期间 Agent 写操作被挂起（只有单测）；通知栏审批面
- 平板 / 折叠屏的 navigation rail（只在横屏手机上验证）；新版本上横屏转回竖屏时底栏接管（修正前的版本转回竖屏正常）
