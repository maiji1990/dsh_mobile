# ADR-0028：移除原生终端页与原生插件页

- 状态：已接受（2026-11-17；项目负责人决定 App 不再自带终端页与插件页：插件管理交回 DSH，App 只做运行环境与应用外壳。
  **终端移除有 Android 17 arm64 真机验收**，插件页移除后的页面复测待做）
- 关联：[ADR-0021](0021-product-ui-material3.md)（产品界面与导航框架；其中「4 栏、含终端栏」部分由本 ADR 取代，其余仍有效）；
  [ADR-0025](0025-terminal-v2-flow-control-and-links.md)（终端 V2，随页面删除失效，留作历史）；
  [ADR-0018](0018-remove-dshbox.md)（App 只剩 C# 实现）、[ADR-0027](0027-remove-bundled-dsh-import-instead.md)
  （运行环境由导入 / 在线更新维护）；AGENTS.md 规则 3（最小垂直切片）、5（不伪造成功）、9（真机证据）、12（Material 3）

## 背景

App 自 PR-06 起自带 C# 终端：PTY 字节经无界队列 → 64 KiB base64 批 → WebMessagePort 送进本地 xterm.js 页，ADR-0025 又加了
背压窗口、搜索、复制、链接确认与可配置辅助键。它是 App 里唯一自研渲染链的页面——WebView 资源拦截与 CSP、`TYPE_NULL`
键盘、键盘避让、横屏紧凑模式、会话回放与重命名——也是唯一仍有未测项的界面（中文组合输入、vim / less 光标模式）。而真正需要
命令行的场景（跑 DSH 的工具、看结构）都在 DSH 会话里发生，页面自身的价值与维护成本不成比例。

插件页是对 `dsh plugin` 的封装：PackageSpec / PackageName 白名单、profile 白名单、装后「可加载性」核对与回滚、随包 pnpm
的钉版本供给，外加逐次安装确认。DSH 自己已能安装、卸载与列出 Cordis 插件；App 侧复刻等于长期维护第二套插件语义和第二份上游
兼容负担（例如 spec 中 `>=` 的 shell 引号问题、真机上 pnpm / corepack 的 `not found` 缺陷）。

两者都是「App 替 DSH 做一件 DSH 已经会做的事」。负责人因此决定移除两个页面，本 ADR 记录删除边界、保留项、兼容处理与验收状态。

## 决策

1. **导航改为三栏**：首页 / 文件 / 更多（宽屏仍换成左侧 navigation rail）。`MainActivity` 保存的页签由 item id 改为
   tag（`navigation.tab.tag`）：旧版保存的 id 与 FragmentManager 状态不再回放，含已删除终端 Fragment 的状态不会被恢复，
   升级后落在首页。首页快捷方式只剩手机操控 / 远程主机 / 运行环境，运行环境卡横跨整行。
2. **删除终端页与整条渲染链**：`TerminalFragment`、`TerminalInputView`、`Assets/terminal`（页面与 vendored xterm.js
   及其许可文件）、`DshMobile.Application.Terminal` 的 `TerminalSessions` / `TerminalOutputPump` / `TerminalKeys` /
   `TerminalLinks`，协调器的 `CreateTerminalAsync` / `Terminals` 与「停止前关闭会话」两条路径，`dshm_terminal_background`
   配色、`terminal_*` / `nav_terminal` 文案、终端「新建会话」用的 `ic_add` 图标、`UiPrefs` 的字号与辅助键偏好，以及 `tooling/terminal-web.lock.json`、
   `tooling/terminal-stress.mjs`、`tests/terminal-web`、CI 的 Terminal page tests 步骤。
3. **保留 guest 侧 PTY 能力与 PTY 自检**：`PtyProbe` 移到 `DshMobile.Application.Runtime` 并持有 slice 名
   `PtyProbe.Slice`（`runtime.pty-probe`），`OperationNames` 与 `BridgeHost` 引用它；`TerminalCommandBuilder` 只服务
   自检；开发者选项里的「PTY 自检」仍打开 guest 登录 shell 并检查 tty / 窗口大小 / ^C / 无残留，仍是规则 9 的真机手段。
4. **删除插件页与插件命令链**：`PluginsActivity` 及其布局、首页与「更多」入口，`GuestPlugins`、
   `PluginCommand`、`PluginInstallGuard`、`PluginPackageFacts`、`PluginProfileReader`，协调器的
   `ListPluginsAsync` / `ChangePluginAsync` / `SpecPackageName`，`GuestUser`（只被终端开 shell 时的 passwd 供应使用）。
5. **保留 DSH 需要的 guest 工具链**：`GuestToolchain` 仍写入 `dsh` / `pnpm` / `xdg-user-dir` 包装，pnpm 仍是随包
   `11.1.2`、由 Node 直接运行、按 SHA-256 校验后以 copy 模式导入；只是不再有 App 侧的调用方。
6. **向量与测试**：仍有效的 POSIX 单 token 引号用例迁到 `tests/TestVectors/guest-command-line.json`
   （`ParityVectorTests.Quote_MatchesGoldenVectors` 消费），`plugin-commands.json` 随之删除；终端 / 插件两个切片的
   单测一并删除（`TerminalSessionsTests`、`TerminalOutputPumpTests`、`TerminalKeysV2Tests`、
   `TerminalWebAssetsTests`、`PluginInstallGuardTests`、`PluginProfileReaderTests`、`GuestPluginsTests`、
   `GuestUserTests`），`UpstreamPinnedValuesTests` 去掉插件白名单与 `GuestDshWrapper` 钉值。
7. **文案**：README、ARCHITECTURE、FEATURES、SECURITY、MIGRATION、UPSTREAMS 与 ADR-0024 / 0027 中「终端 / 插件页」的
   描述同步；许可页去掉 xterm.js；更新确认文案不再提「关闭终端会话」。

## 后果

- 正面：净删约 5.5k 行（含测试与资产）；App 少一个自研 WebView 渲染与输入链，CSP / 资源拦截契约不再由本仓库承担；
  不再复刻 DSH 的插件安装语义（白名单、装后回滚、pnpm 供给）与对应的上游兼容负担；导航更短。
- 代价与行为变更：App 内不再提供多会话交互 shell，需要命令行时在 DSH 内进行；App 只剩只读的 PTY 自检。插件安装 / 卸载
  不再经过 App 的确认与安装守卫：可加载性由 DSH 判定，装后失败不再有 App 侧回滚。旧版的终端字号与辅助键偏好
  （`terminal_font_size` / `terminal_key_bar`）不再读取，作为无害残留保留。
- 兼容：升级后首页与「更多」不再有入口；首页快捷方式改为两列；PTY 自检的操作名不变（`runtime.pty-probe`）；
  journal、状态机与运行层格式无变化。
- 安全不变（规则 11）：手机操控审批卡与 `FLAG_SECURE`、SSH 主机密钥固定与不可导出私钥、guest 命令 argv 逐 token 引号
  （现由 `guest-command-line.json` 锁定）均未削弱；删掉的只是两个页面自身的入口与守卫。
- 未做：插件页移除后首页与「更多」的真机复测；横屏导航与旧状态恢复的复测；DSH 内插件管理的真机验证。

## 真机验收

2026-11-17 在 Honor PTP-AN10（Android 17，arm64）上验证了终端移除：Release APK 覆盖安装（SHA-256
`c8a186eb…99bc1a`）后竖屏只有「首页 / 文件 / 更多」，依次进入文件、更多并返回首页；启动原有 DSH `0.2.2-rc.3` 后首页显示
「打开 DSH」，未认证回环 `GET /` 返回 HTTP 401；停止后恢复「启动 DSH」。记录见
[验收记录](../../migration/parity/2026-11-17-PTP-AN10-A17-remove-terminal.md)。

插件页移除随同一次提交落地，未单独做真机验收；横屏导航、旧版终端 Fragment 状态的系统恢复以及「PTY 自检」仍待真机复测。
