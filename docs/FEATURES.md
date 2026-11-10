# 功能矩阵（FEATURES）

状态取值：`未开始` / `进行中` / `POC` / `通过（附证据）` / `受限（原因）` / `不可用（原因）`。
**只有附真机证据的条目才能标“通过”。**

## 引擎能力（方案 4.1）

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| DSH 启动与停止 | 通过（Android 17 真机） | 导入→启动→READY 18.5 s→停止，无孤儿进程；见 COMPATIBILITY.md「M2 运行时」 |
| `web` profile | 通过（Android 17 真机） | `--dump-config` 44.6 KB；运行中的 DSH `GET /` → 401（launch token 鉴权生效）；App 内 WebView 承载官方 WebUI 已通过（TASK-006） |
| `headless` profile | 受限（需模型凭据） | `--dump-config`、`--help` 通过；真实一次性任务需模型 API Key，未验证 |
| `sdk` profile | 通过（Android 17 真机） | JSON-RPC `initialize` → `deepseek-harness-sdk-runtime`，`shutdown` → `{}`，exit 0；会话/提示需模型凭据，未验证 |
| `sdk-minimal` profile | 通过（Android 17 真机） | 同 sdk 握手（initialize 5.1 s） |
| `acp` profile | 通过（Android 17 真机） | ACP v1 `initialize` → `deepseek-harness-acp`、能力声明；stdin EOF 正常退出 0；会话/提示需模型凭据，未验证 |
| `plugin` 命令 | 通过（Android 17 真机） | 离线 pnpm 11.1.2（corepack 缓存）+ dsh/pnpm 包装；`plugin list` / `add` / `remove` exit 0（用 `is-number` 验证安装与卸载）；安全模式、源选择未做 |
| Cordis profile / bundle / patch | 部分 | 五个 shipped profile 的 `--dump-config` 均成功合成；patch / bundle 切换未测 |
| Model Providers / 凭据 | 未开始 | |
| Session / Resume / Fork | 未开始 | 需模型凭据；WebUI 已可承载，待配置凭据后验证 |
| 工具调用与审批 | 未开始 | |
| Subagent / Agent Team | 未开始 | |
| Bash / PTY / 子进程 | 通过（Android 17 真机） | 原生 TerminalView + PRoot bash：命令执行、^C、多会话新建/切换/关闭、关闭页面后会话保留、「停止」连带结束终端进程均已验证；中文/宽字符、大量输出、横屏未测 |
| 文件、工作目录、附件 | 部分通过（Android 17 真机） | 文件页浏览 / 受保护 `.dsh` 拒绝 / SAF 导入 / SAF 导出已真机通过；WebUI 附件上传未验证（DSH 未发起文件选择，疑需先选工作区）；下载未做 |
| MCP / Skills / 自定义工具 | 部分通过（Android 17 真机） | DSH 0.2.1-rc.5 经 `--patch` 加载 `dsh-mcp-client` 条目并拉起 guest MCP 服务 `ui-facade.mjs`（真机 `ps` 可见为 DSH 子进程）；由模型实际调用 `mcp__ui__*` 需模型凭据，未验证；Skills / 其它自定义工具未做 |
| 更新与诊断 | 部分 | 离线导入 + 固定诊断（arch/debian/node/dsh 版本）+ 健康探测；在线更新与回滚 UI 未做 |

## 宿主基础设施

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| .NET 11 Android 空壳（TASK-002） | 通过（Android 17 真机） | Release（full trim，CoreCLR）在 Honor PTP-AN10 / Android 17 运行，冷启动约 220–290 ms；Android 10 未测。证据见 COMPATIBILITY.md 真机记录 |
| C# ↔ Java ↔ DSHBox Kotlin Bridge（TASK-003） | 通过（Android 17 真机），旋转与 Android 10 未测 | 31 轮×1000 次全部成功、双端在途为 0；强制 GC 后 JNI GREF 恒为 37、Java 存活持平。证据见 COMPATIBILITY.md 真机记录 |
| DSH WebUI（System WebView，TASK-006） | 通过（Android 17 真机），部分未测 | 官方 WebUI 加载 0.3–0.5 s；launch token 只在原生侧；DSH 重启换 token 后重开可鉴权；返回键正常。未测：401 自动恢复路径、外链交给浏览器、旋转、文件选择/下载（TASK-007） |
| 前台服务 | 通过（Android 17 真机） | `specialUse`，后台 90 s DSH 存活 |
| Android 硬链接兼容垫片（link-shim） | 通过（Android 17 真机） | DSH 以 `node --expose-internals --import /opt/dshbox/link-shim.mjs … --profile web` 运行（与 DSHBox 一致）；TASK-004 初版缺失此垫片，已补 |
| Runtime 层管理（TASK-004） | 通过（Android 17 真机），部分验收未测 | 磁盘不足 / 层缺失 / 更新失败未测；见 milestones/M2.md |

## 可选模块（方案 4.3）

| 模块 | 状态 | 证据 / 备注 |
|---|---|---|
| `optional.mobile-automation`（TASK-010） | 只读部分通过（Android 17 真机） | Interlock Relay core（白名单 `ui.snapshot` / `ui.node`）+ 通知栏审批 + guest `ui_*` 门面：`ui_list_devices` online、`ui_snapshot` 40/69 节点、`ui_get_element` 实时回读（含 `nativeId` / `enabled`），exit 0；拒绝 / 未作答 / 无障碍关闭均映射为稳定错误码。写操作（点击 / 输入 / 滚动 / 按键）未开放，见 ADR-0004 第 8 条；Android 10 未测 |

| `optional.browser`（TASK-011） | 部分通过（Android 17 真机） | DSHBox 浏览器栈原样复用（ADR-0005）：浏览器在独立 `:browser` 进程打开；只读自检经 browser-use 插件自带客户端 → guest CLI → broker → 命令服务，`status` OK、`observe` 读取百度首页（标题、正文、22 个元素）；安装 24 个 `browser_*` 工具 exit 0，DSH 重启后 `--dump-config` 含 `@local/browser-use`、无插件失败日志。未验证：模型实际调用 `browser_*`（需凭据）、控制请求审批流程、点击 / 输入 / 上传 / 下载 / 网络捕获、iframe、断网、Android 10 |

其余 `未开始`：`optional.remote-host`、`optional.developer`、`optional.workflow`、`optional.gecko-renderer`。`entertainment.*` **不提供**。
