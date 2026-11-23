# PR-00 ～ PR-04 真机验收报告（Android 17 / M2007J1SC）

> 对应任务：[DSH_Mobile_原生Android重构任务清单.md](../plan/DSH_Mobile_原生Android重构任务清单.md) N-00-* ～ N-04-*
> 规则依据：[AGENTS.md](../../AGENTS.md) 规则 5（不伪造成功）、规则 9（真机证据）
> 复现方式：pwsh -File tooling/device-e2e/run-pr00-04.ps1 -Serial <serial> -ApkPath <release apk>
> 证据目录（本机）：artifacts/device-e2e/20261123-100451/（report.md / report.json / 9 张截图 / stop.log）

## 1. 环境与构建

| 项目 | 值 |
|---|---|
| 设备 | Xiaomi M2007J1SC（lineage_cas，LineageOS），arm64-v8a，adb serial 4bf97d61 |
| 系统 | Android 17（API 37），构建 CP2A.260605.016 |
| 网络 | **无默认网络**：Active default network: none；ping api.deepseek.com → unknown host；ip route 为空 |
| 仓库 | main @ df5e942（PR-03 合并）+ PR-04 未提交改动（Chat/*、Agent/Presentation/*、Rendering/* 等） |
| APK | dotnet build src/DshMobile.Android -c Release（TrimMode=full），SHA-256 14AEEEF2…C1F619A |
| DSH 层 | 设备原为 0.2.2-rc.3，本次经 App「运行环境 → 导入 DSH 层」升级为 **0.2.2-rc.5**（与 runtime-bundle/runtime-assets.lock 及 Golden Fixture 一致；导入时健康检查启动 16.9 s 通过） |
| 凭据 | 设备 .dsh/.credentials.yaml 仅有 record、**缺 refs**（首轮 MISSING_CREDENTIAL）。为打通链路，用 tooling/device-e2e/install-device-credentials.ps1 从本机 DSH 凭据库补齐 refs；**任何密钥均未写入日志、截图或本报告** |

## 2. 桌面自动化（PR-00 ～ PR-03 门禁）

| 套件 | 结果 |
|---|---|
| DshMobile.Core.Tests | 93 通过 / 0 失败 |
| DshMobile.Runtime.Tests | 832 通过 / 2 跳过 / 0 失败 |
| DshMobile.Application.Tests | 135 通过 / 0 失败 |
| DshMobile.Agent.Tests（5 套质量门禁、Stream6000 性能门禁、Golden Fixture、DshRemoteConnection 契约） | 56 通过 / 0 失败 |
| node tooling/run-node-tests.mjs tests/guest | 48 通过 / 0 失败 |
| dotnet build src/DshMobile.Android -c Release -t:Rebuild | 0 错误；**119 条警告**（去重 110 条），其中 **0 条来自 PR-02/03/04 新代码的 Trim 警告**（IL2026/IL3050 全部位于既有 Relay 代码） |

> 备注：任务清单记录的「Release 警告 ≤ 75」与本仓库实测清洁基线（116）不一致；本次 119 相对 116 增量为 +3（ChatFragment/ChatTimelineAdapter 的 CS8604），未新增 Trim 警告。阈值与基线口径需负责人确认。

## 3. 真机结果摘要（修复前首轮，缺陷修复后的复测见第 8 节）

run-pr00-04.ps1 一次完整运行的结论：**PASS 16 / FAIL 1 / INFO 3**。

| ID | 检查项 | 结果 | 证据 |
|---|---|---|---|
| PR00-01 | 首页「打开原生会话（实验）」并行入口可见 | PASS | pr00-home.png |
| PR00-02 | 首页功能入口（手机操控 / 远程主机 / 运行环境）完整 | PASS | 同上 |
| PR00-03 | DSH 层版本 0.2.2-rc.5 且运行环境就绪 | PASS | 首页状态卡 |
| PR00-04 | WebView 回滚入口保留且可正常加载 DSH Web UI | PASS | 手工打开「打开 DSH」，首屏与「预览版说明」弹窗正常（artifacts/device-e2e-webui2.png） |
| PR00-05 | 「文件」页无回退 | PASS | 显示 /root/projects 与 .dsh（受保护）、导入/刷新可用 |
| PR00-06 | 「更多」页无回退 | PASS | 功能卡片、运行环境、后台预热 DSH 均可见 |
| PR01-01 | 启动 DSH 到 Ready | PASS | start: ok sandbox=Running dsh=Ready version=0.2.2-rc.5 |
| PR01-02 | 启动阶段测点齐全（host.preconditions … bridge.complete） | PASS | logcat startup-perf |
| PR01-03 | 冷启到就绪耗时（单次样本） | INFO | 12.8 / 13.3 / 14.0 s（脚本 3 次）；另手工 9.7 / 11.1 s（n=5，均值约 12.2 s）。记录基线约 5.9 s，本地无同条件配对样本，需 N-08-07 A/B 采集复核 |
| PR01-04 | 真机记录 protocol.connected / protocol.initialized | **FAIL** | 见缺陷 F4 |
| PR04-01 | 原生聊天打开并建立会话 | PASS | session-704d894f-… + deepseek-official/deepseek-flash · /root/projects |
| PR04-02 | 模型 / 工作区标签 | PASS | 同上 |
| PR04-03 | 能力门控（图片 / 斜杠入口可见） | PASS | 底部工具栏 |
| PR04-04 | 斜杠命令来自 commands/list | PASS | compact, export, feedback, goal, permission（pr04-slash.png） |
| PR04-05 | SAF 选图生成附件 chip | PASS | img-1 可移除（pr04-image.png） |
| PR04-06 | 横竖屏切换保留草稿与会话 | PASS | landscape 下 draft-rotation-test 与 session id 不变（pr04-landscape.png） |
| PR04-07 | 输入法弹出后输入框可见可编辑（adjustResize） | PASS | 输入 -ime-probe 上屏 |
| PR04-08 | 深色 / 浅色切换（原地） | INFO | 见缺陷 F2：切换前后截图**字节完全一致**（pr04-dark-yes.png 与 pr04-dark-no.png 均为 103699 B） |
| PR04-09 | 返回键退出聊天回首页 | PASS | — |
| PR01-05 | 优雅停止 DSH | PASS | stop: ok (Ready) |
| PR01-06 | 停止后无 guest 残留进程 | PASS | 过滤 libproot / proot-loader / node 计数 0 |
| （补充） | am force-stop 强杀 App 后无孤儿进程 | PASS | 4 s / 10 s 两次抽样均为空，Android 进程组回收生效 |
| （补充） | 文本发送链路（到 DSH） | PASS（受限） | 会话日志 turn/start → request/header → assistant/attempt → llm/retry ×5 → turn/end 完整；失败原因是 TRANSPORT（无网络），不是客户端协议错误 |

## 4. 缺陷清单

| ID | 严重度 | 现象与定位 | 建议 |
|---|---|---|---|
| **F1** | 中 | 宿主未运行时聊天横幅显示原始占位符：「DSH 未运行或连接已断开（%1$s）」。[ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs) 第 315 行对 Android 格式串（%1$s）使用 .NET string.Format，参数被丢弃；同文件第 332/333 行的 GetString(res, arg) 路径格式正常 | 改为 GetString(Resource.String.chat_status_disconnected, code)，并补 UI 文案测试 |
| **F2** | 中 | 系统深色 ↔ 浅色**原地切换**时已打开的 ChatActivity 不重新主题化（截图字节相同）。ChatActivity 声明 ConfigChanges.UiMode，系统不重建 Activity，而代码未触发 AppCompat 重新 ApplyDayNight | 在 OnConfigurationChanged 中调用 AppCompatDelegate.ApplyDayNight()，或去掉 UiMode 让 Activity 重建 |
| **F3** | 中 | 会话级错误只映射为 InvalidState（[ChatViewModel.cs:556](../../src/DshMobile.Agent/Presentation/ChatViewModel.cs#L556)），真实原因（MISSING_CREDENTIAL、TRANSPORT 等）不显示；rc.3 下更只表现为「仅显示部分历史」 | 将 SessionErrorGatewayEvent.Message 经脱敏/白名单映射为稳定错误码 + 简短原因，在横幅与失败轮次行展示 |
| **F4** | 中 | 真机 startup-perf 缺 protocol.connected / protocol.initialized（[DshRemoteConnection.cs:447](../../src/DshMobile.Agent/Transport/DshRemoteConnection.cs#L447)、[:476](../../src/DshMobile.Agent/Transport/DshRemoteConnection.cs#L476) 已埋点，但 [AgentConnectionScope.cs:34](../../src/DshMobile.Android/Bridge/AgentConnectionScope.cs#L34) 构造连接时未注入 StartupTrace） | 把 StartupTrace 传入 AgentConnectionScope，N-01-02 的真机测点才算落地 |
| **F5** | 低 | 未知/诊断事件直接以原文进入时间线（[llm/retry]、event.unknown: agent/inbox/spliced、[request/context]），用户视角是噪声 | 诊断事件折叠为可展开的「运行细节」或仅计数 |
| **F6** | 低 | 横屏且输入法弹出时聊天区高度被压缩到 0，仅剩输入框（artifacts/device-e2e-chat-land.png） | 横屏 + IME 时收起工具栏/斜杠栏，或给列表设置最小高度 |
| **F7** | 低（行为说明） | 每次「打开原生会话」都新建 session（f5e5d402 → 704d894f → …），不续接上次会话；PR-04 是并行实验入口，会话管理属 PR-05 | PR-05 的 ChatsFragment 接入后应恢复最近会话 |
| **F8** | 阻断（环境） | 设备**无任何默认网络**，所有 LLM 场景（真实对话、工具审批、图片发送、斜杠命令执行、6000 词长流、滚动锚点）无法验收 | 设备联网后重跑本脚本 + 手工对话矩阵 |

## 5. 未完成项（联网后仍未覆盖）

- 中文 IME 组合上屏：设备只装了 AOSP LatinIME（无中文输入法），无法验证；本次验证了 IME 弹出时 adjustResize 与中英文混排显示。
- 拒绝后“零工具执行”的独立核实：guest `/tmp` 在应用私有目录内，Release 包不可 `run-as`；未提供可信的设备侧证据，**按规则 9 仍需补测**。
- T07 精确口径（6,000 词 + 400 行代码块）：本次让模型输出长文档时它选择了写文件/跑脚本，实际渲染的是工具卡；帧统计（917 帧 / 2 janky / p90 11 ms）可以佐证流式性能，但不等于 T07 的固定负载。
- Cache-First 首屏、会话列表、凭据引导：属 PR-05，未纳入本次范围。

## 6. 结论（修复前首轮；修复后复测结论见第 8 节）

- **PR-00：通过**（页面/入口无回退、层版本正确、WebView 回滚可用、构建与单测门禁全绿）。
- **PR-01：部分通过**——启动/握手/停止/无孤儿已在真机验证；缺 `protocol.*` 测点（F4），启动耗时 9.7–14.0 s 需按 N-08-07 配对采集。
- **PR-02：基本通过**——真机完成能力探测、会话创建/续接、命令清单、文本与图片发送、审批三态（允许/Hold-to-undo/拒绝）且不死锁；斜杠命令仅手输路径可用（F10）。
- **PR-03：桌面门禁全绿，但真机暴露参数回溯失效（F9）与断线重连失效（F14）**；`Stream6000` 计数门禁通过的结论不受影响。
- **PR-04：功能主路径通过**（入口、能力门控、选图、旋转、IME、返回、停止、真实对话、图片、审批、滚动锚点、帧统计），但 **F1/F2/F9/F10/F11 必须修复**才满足验收门禁；F12/F13 为体验问题。

## 7. 联网复测：真实对话、图片、审批、长文（2026-11-23 10:32–11:05）

设备接入 Wi‑Fi（`Active default network: 100`，`api.deepseek.com` ping 23 ms）后，DSH 侧模型目录加载成功（`model listing … loaded 2 model(s)`），重跑真实交互。

| 场景 | 结果 | 证据 |
|---|---|---|
| 文本回合（流式 + Markdown） | **通过**：用户消息 → 思考过程卡 → Markdown 表格（两列两行）→ bash 代码块（带“复制”）→ 助手文本 | live-2-assistant.png / live-page-3.png |
| 工具调用卡 | **通过**：工具名 bash、状态“已完成”、参数 JSON 展示 | page 7 文本转储 |
| 审批卡出现（workspace-write 预设） | **通过**：等待授权 / 拒绝 / 允许一次 | live-8-approval-card.png |
| Hold-to-undo | **通过**：允许一次 → “即将允许…” + 撤销 → 撤销后回到“等待授权” | live-10-undo.png + 状态转储 |
| 拒绝审批 | **通过（有限）**：卡片消失；拒绝后工具是否执行未能从设备侧独立核实（guest /tmp 不可读） | live-11-rejected.png |
| 图片发送 | **通过**：SAF 选图 → `img-1` → 模型准确描述（“Mobile chat app error screenshot.”） | live-15-image-answer.png |
| 斜杠命令（手输 `/permission`） | **通过**：返回 `current preset danger-full-access (available: read-only, workspace-write, danger-full-access)` | 文本转储 |
| 斜杠命令（点芯片） | **失败**：`/compact` 执行报 `command.invalid_syntax`（缺陷 F10） | live-17 状态 + 横幅 |
| 会话续接 | **通过**：重新打开原生会话恢复到 `session-704d894f-…`（不再是每次新建） | 聊天标题 |
| 流式性能 | **通过**：`dumpsys gfxinfo` 917 帧 / 2 janky（0.22%）/ p50 5 ms / p90 11 ms / p99 14 ms | gfxinfo 输出 |
| 滚动锚点 | **通过**：底部无浮钮；上滑后出现“回到底部” | live-21-fab.png |
| 停止与孤儿进程 | **通过**：`stop: ok (Ready)`，guest 进程 0 | stop 日志 |
| 主机重启后重连 | **失败**：停止 DSH → 再次启动 → 原生聊天停在 `TransportLost`，重试/重开 Activity 均无效，需强杀 App（缺陷 F14） | 10:58–11:02 状态转储 |

### 7.1 联网复测新发现缺陷

| ID | 严重度 | 现象与定位 | 建议 |
|---|---|---|---|
| **F9** | **高** | 审批卡显示 **“Agent 请求执行工具 unknown”** 且红字“参数尚未加载”，与实际工具（bash）不符；`callId` → `tool/call` 参数回溯在 rc.5 上失效（[RequestsEngine.cs](../../src/DshMobile.Agent/Engines/RequestsEngine.cs) + [RemoteProtocolCodec.cs](../../src/DshMobile.Agent/Protocol/RemoteProtocolCodec.cs) 的 approval 事件字段映射） | 按 rc.5 wire 形状修正 approval 事件解析（工具名/参数/`callId`），并补一条真实样本的 golden fixture |
| **F10** | **高** | 斜杠命令芯片点击后执行的是不带 `/` 的 `compact` → `command.invalid_syntax`；手输 `/permission` 正常。[ChatFragment.cs](../../src/DshMobile.Android/Chat/ChatFragment.cs) 的 `OnSlashChipClicked` 直接传 `cmd.Name`，而 `commands/execute` 需要完整命令行 | 芯片路径补 `/` 前缀（或统一走 `ExecuteCommandAsync("/" + name)`），并加一条命令向量 |
| **F11** | **高** | 未知/辅助事件被渲染成一等时间线卡片：`event.unknown: agent/inbox/spliced`（每轮数条到十余条）、`session/end-seed`、`session-log-deepseek/delivery-accepted`、`deliverables/presented`、`workspace/changes`、`job/registered|settled`。每轮结束后正文被这些卡片挤到上方，底部几乎全是事件卡 | 事件白名单：仅把用户可见语义（消息、工具、审批、问答、轮次状态）映射为行；其余降级为折叠的“运行细节”或完全不计入时间线 |
| **F12** | 中 | DSH 注入的系统提示（`<system-reminder>`、技能目录、时间提示）被当成普通聊天气泡展示，内容极长且与用户无关 | 过滤 `system` 角色消息（或折叠为“上下文”条目） |
| **F13** | 低 | `session/title` 事件没有更新顶栏标题（仍显示 `session-<id>`），反而成为一张事件卡 | 解析 title 事件并更新会话标题 |
| **F14** | **高** | 同一 App 进程内，DSH 停止再启动后原生聊天**无法重新连接**：横幅停在 `TransportLost`，点「重试连接」无效，退出并重开 `ChatActivity` 仍无效；只有强杀 App 重开才恢复（新会话 `dd56cb48`）。对应 T06「运行中杀 DSH/断线 → 恢复校准」 | 检查 `AgentConnectionScope`/`DshRemoteConnection` 在 `HostStopping` 排空后的形状：重新 `ConnectAsync` 前重置 drained 状态与 token 代次，并补一条「host 重启后重连」真机回归 |

### 7.2 对门禁的影响

- PR-02「真机无 UI 端到端可发送文本/图片、触发斜杠命令并完成审批不死锁」：**文本、图片、审批（允许/撤销/拒绝）已在真机跑通且不死锁**；斜杠命令仅手输路径可用（F10）。
- PR-04「真机完成一轮带工具调用/审批/图片/斜杠命令的对话」：**功能跑通，但 F9（审批卡工具名/参数错误）与 F11（事件卡淹没正文）属于必须修复的验收阻断项**。
- PR-03 的 `RequestsEngine` 参数回溯（`N-03-07`）在桌面单测为绿，但真机 rc.5 上失效——需用真实 approval 帧补 fixture。
- PR-03 的断线恢复（T06）在真机**不通过**：DSH 停止再启动后连接无法重建（F14）。


## 8. 修复后真机复测（2026-11-23 12:05–12:12）

- APK：`dotnet build src/DshMobile.Android -c Release`（2026-11-23 11:48），SHA-256 `92E6E0073B53…FEBA7378`；覆盖安装保留数据。
- 环境：同一设备/系统；Wi‑Fi 已连接；DSH 层 `0.2.2-rc.5`。
- 桌面门禁：Core 93 / Runtime 832（2 跳过）/ Application 135 / **Agent 61**（新增瀑布帧、晚到 `tool/call`、`session/title`、斜杠前缀、重连恢复等用例）/ guest 48，全部通过；`Release` 构建 0 错误。
- 脚本矩阵（`run-pr00-04.ps1`，证据 `artifacts/device-e2e/20261123-120553/`）：**PASS 18 / FAIL 0 / INFO 2**。

| 缺陷 | 复测结果 | 证据 |
|---|---|---|
| F1 断连横幅占位符 | **已修复**：显示「DSH 未运行或连接已断开（STOPPED）」 | 文本转储 |
| F2 深色原地切换 | **已修复**：切换前后截图哈希不同，浅色主题生效；草稿与会话保留 | fix-dark-before/after.png |
| F3 错误原因透传 | 单测覆盖（`DshFailureMapper.FromSessionError`）；本次真机未复现会话级错误，未取得端到端样本 | Agent.Tests 61/61 |
| F4 `protocol.*` 测点 | **已修复**：真机 `startup-perf` 记录 `protocol.connected` / `protocol.initialized` | PR01-04 PASS |
| F5/F11 事件卡片淹没正文 | **已修复**：一轮对话后时间线仅含 用户消息 / 助手文本 / 工具卡，无 `event.unknown`、`agent/inbox/spliced`、`job/*` 等卡片 | 「Reply with exactly PONG」回合转储 |
| F6 横屏 + 输入法 | **仍不通过**：横屏且 IME 打开时 RecyclerView 为 `[90,442][2340,906]`，完全落在 IME 之下（不可见）；IME 关闭时仅 `[90,587][2340,796]`（约 209 px） | fix-landscape-ime.png / fix-landscape-noime.png |
| F9 审批工具名/参数 | **已修复**：卡片显示「Agent 请求执行工具 `bash`」与 `{"command": "id -u", …}`；允许后出现「执行工具 · bash / 已完成」工具卡 | fix-approval-allowed.png |
| F10 芯片斜杠命令 | **已修复**：芯片显示 `/compact`，执行返回「Compacted 12 history items (~1643 tokens).」，无 `command.invalid_syntax` | fix-compact.png |
| F12 系统提示气泡 | **已修复**：时间线不再出现 `<system-reminder>` / 技能目录气泡 | PONG 回合转储 |
| F13 会话标题 | **已修复**：顶栏标题变为会话标题（如「Reply with exactly PONG」） | PONG 回合转储 |
| F14 主机重启后重连 | **已修复**：停止 DSH → 再启动 → 重开原生会话即可恢复连接，无 `TransportLost` | 12:04 状态转储 |

> F6 仍为未修复项：横屏 + 软键盘场景的可用性未达 PR-04「横竖屏」验收要求，需继续处理（折叠更多元素或横屏改用 `adjustPan`/全屏输入模式）。

### 8.1 修复后结论

- **PR-01**：启动/握手/`protocol.*` 测点/停止/无孤儿全部通过（启动 10.5–17.2 s，仍需 N-08-07 配对采集）。
- **PR-02**：文本、图片（上一轮）、审批三态、`/permission` 与 `/compact` 均真机通过；F9/F10 关闭。
- **PR-03**：`Stream6000` 与契约门禁全绿；真机参数回溯（F9）与断线重连（F14）已恢复。
- **PR-04**：除 **F6（横屏 + IME）** 与 **中文 IME 组合上屏（设备无中文输入法）** 外，其余验收项均已真机通过；`N-04-05` 在 F6 处理与中文输入法验证前不应关闭。

> 按规则 5、9：上述「未验证」项不得在任何文档中标记为真机通过。