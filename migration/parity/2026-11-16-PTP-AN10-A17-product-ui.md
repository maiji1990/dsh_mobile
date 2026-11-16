# 2026-11-16 真机验收：产品级界面（ADR-0021）+ PR-10 / ADR-0019 之后的 C# 唯一实现

- 设备：Honor PTP-AN10，Android 17（SDK 37），arm64-v8a，浅色模式（系统未开深色）；动态取色生效（取自系统壁纸）
- 构建：`ui/product-material3`（含 PR-10 / PR-11 / PR-12 与 #37 的 CI 修复），Debug，
  `dotnet build -t:Install -p:AdbTarget=-s%20<设备>`；**覆盖安装**，保留 2026-11-14 的旧数据（同一签名）
- 起点：已安装运行层（`runtime-current/{base,node,android-side,dsh}`）、旧目录 `files/dshbox`、`user-data/.dsh/dshbox`
- 操作只点本 App 自己的按钮；没有接受任何系统对话框，没有开启无障碍，没有作答审批或 Shizuku 授权，没有改系统设置
- 截图在本地（未入库：其中含设备与用户信息）

## 冷启动与数据迁移（ADR-0019）

`am start -W`：TotalTime 690 ms（Debug 构建）。首次启动日志：

```text
runtime coordinator: impl=csharp journal=Ready/Stopped gen=28
name migration (ADR-0019): moved .dsh/dshbox -> .dsh/dshmobile; layer state runtime-current/base: 1 file(s);
  layer state runtime-current/node: 1 file(s); layer state runtime-current/android-side: 1 file(s);
  layer state runtime-current/dsh: 1 file(s); guest paths rewritten in 1 file(s); removed files/dshbox
runtime (C#): journal=Ready/Stopped
```

ADR-0019 的一次性迁移此前只在模拟器上验证过，这是第一次真机通过。

## 界面（逐页）

| 页面 / 操作 | 结果 |
|---|---|
| 首页（已停止） | 状态卡「已停止 · DSH 未启动 · DSH 0.2.1-rc.5 · 运行环境已就绪」，主按钮「启动 DSH」；四个功能卡片 |
| 首页 → 启动 DSH | 卡片变为「启动中」+ 不确定进度条，随后「运行中 · DSH 已就绪」，主按钮「打开 DSH」、次按钮「停止」；前台服务通知出现 |
| 打开 DSH | 官方 WebUI 加载（「开始使用」登录对话框为 DSH 自身界面，未操作）；返回键回到首页 |
| 终端栏 | 进入即自动打开「终端 1」：`uname -m`=aarch64、`tty`=/dev/pts/0、Debian 13.6、Node v24.19.0；弹出键盘时底栏隐藏、辅助键栏贴在键盘上方、PTY 尺寸随之改为 14×41 |
| 终端：CTRL 键 + c | `sleep 30` 被中断，`^C`，`echo $?` = 130 |
| 终端：「+」新会话 / 切栏 | 「终端 2」打开；切到文件栏再回来，两个会话都在；切回「终端 1」完整重放之前的输出 |
| 文件栏 | `/root/projects`：`.dsh`（锁图标，受保护）、`apple-touch-icon.png` 14.5 KB |
| 更多 → 插件 | `dsh plugin list` OK，列出已装插件（见下「发现」第 2 条）；Profile 行、输出卡片正常 |
| 更多 → 手机操控 | 中继开关「已关闭」；无障碍「未开启」、悬浮审批卡「已允许」、Shizuku「已授权（v13，shell）」、DSH 工具「未注册」（未开启任何项） |
| 更多 → 远程主机 | 已保存的主机配置正确回填；连接卡「未连接」；本机公钥卡 |
| 更多 → 运行环境 | 运行状态「运行中」、DSH 0.2.1-rc.5、三层「已安装」、链接兼容层「正常」、可用空间 7619.5 MB；导入 / 修复 / 回滚三行 |
| 更多 → 关于 ×7 | Toast「已开启开发者选项」，「开发者」分组出现 |
| 更多 → 开源许可 | 许可对话框完整显示 |
| 开发者选项 → 诊断 | `health: port=True webUi=True http=401`；guest 4 条命令 588 ms：aarch64 / 13.6 / v24.19.0 / 0.2.1-rc.5 |
| 开发者选项 → 终端 PTY 自检（确认后） | **PASS**：open 60 ms、prompt、tty（pts / 24×80 / echo）、resize 30×100、ctrl-c status=130、exit 0、no-orphans（2 个进程消失） |
| 开发者选项 → DSH 模式探测 | `web=pass headless=limited sdk=pass sdk-minimal=pass acp=pass plugin=pass`（headless 需模型凭据，与此前真机一致） |
| 首页 → 停止 | `terminal.csharp: closed 2 session(s) on stop`、`host: stopped, no marker process left`、`stop: ok (Ready)`；卡片回到「已停止」；`ps -A` 无 `libproot.so` / `node` / `bash` 残留（停止前为 4 组） |

## 发现（均非本界面切片引入，未在本 PR 修改）

1. **DSH 提示「无法创建默认工作区，请通过“选择工作区”选择文件夹」**：DSH 的首次使用工作区放在
   `<Documents>/deepseek-harness`，Linux 上靠 `xdg-user-dir` 或 `documentsDirectory` 配置找 Documents；guest 两者都没有。启动 argv / 环境与
   PR-10 前一致（向量锁定），属既有行为。修正需改 DSH 启动环境或 patch 层，按规则 3 单独切片。
2. **残留插件 `@local/browser-use`**（`file:../../dshbox/dshbox-plugins/browser-use/plugin`）：DSHBox 浏览器时代装入的本地插件。
   ADR-0018 删除了浏览器，ADR-0019 又把 `.dsh/dshbox` 改名，此引用现在指向不存在的路径。`plugin list` 与 DSH 启动不受影响；可由用户在插件页卸载，
   或由迁移代码清理（需单独决定）。
3. **启动器图标仍是 .NET 模板图标**（Toast 中可见）：产品级界面尚缺自有图标（不得使用 DSHBox 品牌资产）。

## 未验证

- 深色模式（未改系统设置）；横屏
- 远程主机连接（本次没有可用的测试 sshd 与 `adb reverse`；未改用本机系统 sshd）
- 手机操控中继开启、审批卡、只读自检（需要用户本人开启无障碍并作答审批）
- 导入 / 修复 / 回滚（会改动已装运行层，本次未执行）
