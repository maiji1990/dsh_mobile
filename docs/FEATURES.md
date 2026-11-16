# 功能矩阵（FEATURES）

状态取值：`未开始` / `进行中` / `POC` / `通过（附证据）` / `受限（原因）` / `不可用（原因）`。
**只有附真机证据的条目才能标“通过”。**

> **PR-10（[ADR-0018](ADR/0018-remove-dshbox.md)）后**：App 只有 C# 实现。下表的真机证据大多采集于 PR-10 之前（旧桥 / DSHBox 实现，或当时开发者可选的
> C# 路径）；C# 成为唯一实现后的真机复验按负责人决定跳过，只有单测与模拟器冒烟（见 [MIGRATION.md](MIGRATION.md) 第 6 节）。
>
> **证据时代标记（vNext PR-00，2026-11-16 复核，详见 [VNEXT_STATUS.md](VNEXT_STATUS.md)）**：「真机·当前 C#（11-16）」= PR-10 之后的 C# 唯一实现在
> Honor PTP-AN10 / Android 17 上的两次验收（[产品界面](../migration/parity/2026-11-16-PTP-AN10-A17-product-ui.md)、
> [续测](../migration/parity/2026-11-16-PTP-AN10-A17-runtime-relay-dark-landscape.md)；Debug 构建，早于 maiji1990/dsh_mobile#41）；
> 「**待复测**」= 只有 PR-10 之前的真机证据；「**待测**」= 从未真机验证。证据列保留原始记录。

## 引擎能力（方案 4.1）

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| DSH 启动与停止 | 通过（真机·当前 C#（11-16）） | 导入→启动→READY 18.5 s→停止，无孤儿进程；见 COMPATIBILITY.md「M2 运行时」 |
| `web` profile | 通过（真机·当前 C#（11-16）） | `--dump-config` 44.6 KB；运行中的 DSH `GET /` → 401（launch token 鉴权生效）；App 内 WebView 承载官方 WebUI 已通过（TASK-006） |
| `headless` profile | 受限（需模型凭据） | `--dump-config`、`--help` 通过；真实一次性任务需模型 API Key，未验证 |
| `sdk` profile | 握手通过（真机·当前 C#（11-16），模式探测）；会话待测 | JSON-RPC `initialize` → `deepseek-harness-sdk-runtime`，`shutdown` → `{}`，exit 0；会话/提示需模型凭据，未验证 |
| `sdk-minimal` profile | 握手通过（真机·当前 C#（11-16），模式探测）；会话待测 | 同 sdk 握手（initialize 5.1 s） |
| `acp` profile | 握手通过（真机·当前 C#（11-16），模式探测）；会话待测 | ACP v1 `initialize` → `deepseek-harness-acp`、能力声明；stdin EOF 正常退出 0；会话/提示需模型凭据，未验证 |
| `plugin` 命令 | 部分：`plugin list` 通过（真机·当前 C#（11-16））；add / remove **待复测**（仅 PR-10 前真机） | 离线 pnpm 11.1.2（corepack 缓存）+ dsh/pnpm 包装；`plugin list` / `add` / `remove` exit 0（用 `is-number` 验证安装与卸载）；安全模式、源选择未做 |
| Cordis profile / bundle / patch | 部分 | 五个 shipped profile 的 `--dump-config` 均成功合成；patch / bundle 切换未测 |
| Model Providers / 凭据 | 未开始 | |
| Session / Resume / Fork | 未开始 | 需模型凭据；WebUI 已可承载，待配置凭据后验证 |
| 工具调用与审批 | 未开始 | |
| Subagent / Agent Team | 未开始 | |
| Bash / PTY / 子进程 | 通过（真机·当前 C#（11-16））；中文 IME / vim / 大输出**待测** | 原生 TerminalView + PRoot bash：命令执行、^C、多会话新建/切换/关闭、关闭页面后会话保留、「停止」连带结束终端进程均已验证；横屏（9 行，键盘弹出时紧凑模式）已验证；中文/宽字符、大量输出未测 |
| 文件、工作目录、附件 | 部分：浏览 / 受保护 `.dsh` 通过（真机·当前 C#（11-16））；SAF 导入导出**待复测**（仅 PR-10 前）；附件 / 下载未实现 | 文件页浏览 / 受保护 `.dsh` 拒绝 / SAF 导入 / SAF 导出已真机通过；WebUI 附件上传未验证（DSH 未发起文件选择，疑需先选工作区）；下载未做 |
| MCP / Skills / 自定义工具 | 部分：`ui_*` 门面自检通过（真机·当前 C#（11-16））；模型实际调用**待测**（需凭据） | DSH 0.2.1-rc.5 经 `--patch` 加载 `dsh-mcp-client` 条目并拉起 guest MCP 服务 `ui-facade.mjs`（真机 `ps` 可见为 DSH 子进程）；由模型实际调用 `mcp__ui__*` 需模型凭据，未验证；Skills / 其它自定义工具未做 |
| 更新与诊断 | 部分：导入 / 回滚通过（真机·当前 C#（11-16），回滚需手工建 `previous/`）；在线更新**阻断**（无发布公钥） | 离线导入（系统文件选择器）+ 回滚（「更多 → 运行环境」，真机通过，但槽位只在 `previous/` 已存在时产生）+ 固定诊断（arch/debian/node/dsh 版本）+ 健康探测；在线更新未做（签名清单校验 + 只读版本发现已在 C# 实现并有单测，[ADR-0022](ADR/0022-signed-release-manifest-readonly-discovery.md)；未内置发布公钥、未接 UI、无下载 / 安装）；DSH 层换层已改为日志化事务，备份失败或中途强杀不再丢失旧层（单测 + 故障注入，无真机证据） |

## 宿主基础设施

| 功能 | 状态 | 证据 / 备注 |
|---|---|---|
| .NET 11 Android 空壳（TASK-002） | **待复测**：Release full-trim 证据仅 PR-10 前；11-16 两次真机均为 Debug 构建 | Release（full trim，CoreCLR）在 Honor PTP-AN10 / Android 17 运行，冷启动约 220–290 ms；Android 10 未测。证据见 COMPATIBILITY.md 真机记录 |
| ~~C# ↔ Java ↔ DSHBox Kotlin Bridge（TASK-003）~~ | **已删除（ADR-0018）**；此前：通过（Android 17 真机），旋转与 Android 10 未测 | 31 轮×1000 次全部成功、双端在途为 0；强制 GC 后 JNI GREF 恒为 37、Java 存活持平。证据见 COMPATIBILITY.md 真机记录 |
| DSH WebUI（System WebView，TASK-006） | 通过（真机·当前 C#（11-16）），401 恢复 / 下载 / 文件选择**待测** | 官方 WebUI 加载 0.3–0.5 s；launch token 只在原生侧；DSH 重启换 token 后重开可鉴权；返回键正常。未测：401 自动恢复路径、外链交给浏览器、旋转、文件选择/下载（TASK-007） |
| 前台服务 | 部分：前台服务通知通过（真机·当前 C#（11-16））；后台 90 s 存活**待复测**（仅 PR-10 前） | `specialUse`，后台 90 s DSH 存活 |
| Android 硬链接兼容垫片（link-shim） | 通过（真机·当前 C#（11-16）：DSH 启动即依赖） | DSH 以 `node --expose-internals --import /opt/dshmobile/link-shim.mjs … --profile web` 运行（与 DSHBox 一致；路径 ADR-0019 由 `/opt/dshbox` 改名）；TASK-004 初版缺失此垫片，已补 |
| Runtime 层管理（TASK-004） | 部分：导入 + 装后核对通过（真机·当前 C#（11-16））；低磁盘 / 中途强杀**待测**；DSH 层换层事务（#41）仅单测 | 磁盘不足 / 层缺失 / 更新失败未测；见 milestones/M2.md |
| C# 运行时协调器（ADR-0007，PR-00/01/02；影子比对已于 ADR-0018 删除） | 通过（真机·当前 C#（11-16）：覆盖安装杀进程后 journal `RUNTIME_INTERRUPTED` → 读盘恢复） | Release full-trim：启停 / 重复启动 / 停止无孤儿 / 强杀后 journal 恢复为 `RUNTIME_INTERRUPTED`；导入时 `runtime.bundle-verify` 与 `runtime.layer-extract` 影子均 `Agree`（16,119 项逐项一致）。实现仍为旧桥；失败清理分支仅单测。见 COMPATIBILITY.md「ADR-0007 迁移」 |
| C# 宿主（ADR-0015 起默认，ADR-0018 起唯一实现） | 通过（真机·当前 C#（11-16）：ready 16.2 s、停止无残留） | 不经 DSHBox 代码：C# 启动沙箱 + DSH、取 launch token、C# WebView 加载官方 WebUI；10.1 s READY；停止 / 强杀无孤儿；token 不入日志。（当时运行期间终端 / 手机操控不可用；之后的版本已接通。）见 COMPATIBILITY.md「ADR-0007 里程碑」 |
| C# 终端（ADR-0016 起默认，ADR-0018 起唯一实现） | 通过（真机·当前 C#（11-16）），部分未测（同「Bash / PTY」） | 不经 DSHBox 终端代码：C# PTY + PRoot bash、Material 3 界面、本地 xterm.js 显示、原生键盘输入；多会话 / 重放 / resize / ^C / 大量输出 / 关闭与强杀无残留已验证；横屏已验证；中文组合输入、vim 等应用光标模式未测。见 COMPATIBILITY.md「C# 终端界面」 |

## 可选模块（方案 4.3）

| 模块 | 状态 | 证据 / 备注 |
|---|---|---|
| `optional.mobile-automation`（TASK-010） | 只读通过（真机·当前 C#（11-16），页内 + 悬浮审批卡）；写操作**待复测**（仅 PR-10 前）；通知栏审批面**待测** | Interlock Relay core（白名单 `ui.snapshot` / `ui.node`）+ 通知栏审批 + guest `ui_*` 门面：`ui_list_devices` online、`ui_snapshot` 40/69 节点、`ui_get_element` 实时回读（含 `nativeId` / `enabled`），exit 0；拒绝 / 未作答 / 无障碍关闭均映射为稳定错误码。写操作（点击 / 输入 / 滚动 / 按键）未开放，见 ADR-0004 第 8 条；Android 10 未测 |

| `optional.browser`（TASK-011） | **不提供（ADR-0018 删除，负责人决定「不需要浏览器」）** | 历史：DSHBox 浏览器栈原样复用（ADR-0005）：浏览器在独立 `:browser` 进程打开；只读自检经 browser-use 插件自带客户端 → guest CLI → broker → 命令服务，`status` OK、`observe` 读取百度首页（标题、正文、22 个元素）；安装 24 个 `browser_*` 工具 exit 0，DSH 重启后 `--dump-config` 含 `@local/browser-use`、无插件失败日志。未验证：模型实际调用 `browser_*`（需凭据）、控制请求审批流程、点击 / 输入 / 上传 / 下载 / 网络捕获、iframe、断网、Android 10 |

| `optional.remote-host`（TASK-012） | **待复测**：真机证据仅 PR-10 前（11-14 → Windows OpenSSH）；11-16 未验证 | ADR-0006：Keystore 硬件保护 ECDSA 密钥登录 Windows OpenSSH；首连 `HOST_KEY_UNKNOWN` → 核对固定 → 连接；探测远端 node v26.13.0 / dsh 0.2.1-rc.5；启动远端 `dsh web`（仅回环）、本地转发经 `GET /` 得 401、token 只在原生侧；停止 / 断开后远端端口释放；断线检出 `UNREACHABLE` 并作废 token，固定指纹免确认重连；与本机 DSH 同时运行互不影响。未做：远端 WebUI（需独立进程隔离 Cookie）、真实网络（经 `adb reverse`）、`HOST_KEY_CHANGED` 真机演练、Android 10 |

其余 `未开始`：`optional.developer`、`optional.workflow`、`optional.gecko-renderer`。`entertainment.*` **不提供**。
