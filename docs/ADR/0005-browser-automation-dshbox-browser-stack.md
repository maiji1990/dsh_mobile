# ADR-0005：浏览器自动化复用 DSHBox 浏览器栈与 `browser-use` 适配插件

- 状态：已接受（2026-11-10）
- 来源：方案第 2、9.2、10 节，M6；TASK-011（验收：关键 24 工具对照通过）
- 关联：ADR-0004（手机操控，`ui_*`）

## 背景

DSHBox `v1.5.1` 的浏览器分四个 Android 库模块与一个 Cordis 插件：

| 部件 | 内容 | 依赖 |
|---|---|---|
| `browser/browser-api` | 契约常量、生命周期、任务契约 | 无 |
| `browser/browser-devtools` | CDP 连接、AX 树观察、网络 / WebSocket 捕获 | api |
| `browser/browser-runtime` | `BrowserActivity`（普通 View，非 Compose）、`BrowserCommandService`、上传 / 下载 Provider，全部运行在独立 `:browser` 进程 | common、api、devtools、`androidx.webkit` |
| `browser/browser-bridge` | `BrowserCliBroker`（主进程 LocalSocket + 一次性 token，转发到命令服务）；guest CLI `/opt/browser/bin/browser` 资产 | api |
| `plugin-manager/.../plugins/browser-use` | Cordis 插件 `@local/browser-use`（GPL-3.0）：经 DSH `defineTool` 注册 **24 个 `browser_*` 工具**，内部调用 guest CLI；自带 `install.sh` / `uninstall.sh` | DSH `@deepseek-ai/dsh-tools` |

DSHBox 的宿主接线只有几十行（`DshApp.initializeBrowserTools`）：沙箱运行时启动 broker、否则停止并广播撤销控制；
`:browser` 进程设置独立 WebView 数据目录。未修改的 sandbox-manager 已把 `browserEntryDir` 绑定到 guest `/opt/browser`。

## 决策

1. **原样复用四个库模块**（源码引用子模块，不修改），导出 AAR，C# 侧 `Bind="false"`；`androidx.webkit` 随 AAR 导出
   （许可审计白名单）。浏览器页面使用库自带的 `BrowserActivity`，由 Java Facade 以显式 Intent 打开；不生成 C# 绑定
   （AGENTS.md 规则 2 针对 Compose 页面与整体绑定，此处两者都不涉及）。C# 原生「浏览器」标签页留待后续，不阻塞首版。
2. **进程与数据隔离**：运行时组件留在 `:browser` 进程；该进程中 C# `Application` 只调用
   `WebView.SetDataDirectorySuffix("browser")` 后返回（不建 Bridge），使浏览器的 Cookie / 存储与 DSH WebUI（主进程 WebView）分离。
3. **权限**：App 清单声明 `${applicationId}.permission.BROWSER_INTERNAL`（`signature`）并 `uses-permission`，与 DSHBox 一致；
   命令服务与广播只对同签名可达。
4. **Broker 生命周期**：主进程 `RuntimeController` 持有一个 `BrowserCliBroker`；资产在初始化时落盘，沙箱
   `STARTING / RUNNING / RECOVERING` 时 `start()`，其余状态 `stop()` 并广播 `ACTION_REVOKE_CONTROL`（同 DSHBox）。
5. **工具注册（用户显式开启，默认关闭）**：把 DSHBox `browser-use` 插件资产（白名单复制，不含其它 DSHBox 插件）随 APK 打包，
   开启时暂存到 `.dsh/dshbox/dshbox-plugins/browser-use` 并以 guest 命令运行其**原版** `install.sh <web profile>`；关闭时运行
   `uninstall.sh`。安装器只改 web profile 的 `package.json` 依赖与 `node_modules/@local/browser-use`（与 `dsh plugin add`
   同一层面，可逆）。DSH 重启后生效，界面如实提示。
6. **命名空间分离**：浏览器只用 `browser_*`，与手机操控 `ui_*` 互不混用（方案 9.2）。`mobile-pilot` 等其它 DSHBox 插件仍不打包。
7. **品牌**：库内唯一带品牌的字符串 `browser_collapse`（「返回 DSHBox」）由 App 自身资源在全部 6 个语言中覆盖为
   「返回 DSH Mobile」，不改上游文件；其余资源无品牌。
8. **安全分级沿用上游**：浏览器内 AI 控制开关、控制权审批、上传前审批、下载、DevTools / 网络捕获的独立授权全部由
   `browser-runtime` 原样提供，宿主不放宽。

## 实施中确认的细节

- `.NET` 只为 C# 组件声明的进程生成 `MonoRuntimeProvider`；`:browser` 来自库清单，因此加了一个永不启动的
  `BrowserProcessAnchor`（`Service`，`Process=":browser"`，`Enabled=false`）让构建生成 `MonoRuntimeProvider_1`，
  否则 `:browser` 进程会带着托管 `Application` 却没有 .NET 运行时。
- 库已用 `browser_title` 等资源名；App 新增字符串一律 `dm_browser_` 前缀，只有 `browser_collapse` 是有意覆盖（品牌）。
- `androidx.webkit` 由 NuGet `Xamarin.AndroidX.WebKit` 1.12.1.2 提供（导出任务把 `androidx.*` 视为 NuGet 提供，不会自动导出）。
- 适配插件只在失败时写日志，DSH 日志在 App 私有目录；加载证据取两项：`dsh --profile web --dump-config` 中出现
  `@local/browser-use`，以及宿主侧只读回 DSH 日志里点名该插件的行（最多 5 行）。
- 浏览器空闲约 5 分钟自动关闭（`idle_timeout`），关闭后 `status` 仍带上一个 `sessionGeneration`，判断会话须看 `state`。

## 后果

- APK 增加四个库与 `androidx.webkit`；`:browser` 进程会再启动一份 .NET 运行时（只做目录设置），内存开销需在兼容记录中量化。
- 24 工具的行为以 DSHBox 实现为准；升级随 DSHBox 子模块单独 PR，需复核资产白名单与品牌覆盖。
- 由模型实际调用 `browser_*` 需模型凭据；无凭据时以 guest CLI（插件调用的同一入口）做真机自检。
