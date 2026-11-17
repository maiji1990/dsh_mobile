# 测试计划（TEST_PLAN）

> **PR-10（[ADR-0018](ADR/0018-remove-dshbox.md)）后为历史文档**：涉及旧桥、DSHBox Kotlin 调用、旧终端与浏览器（TASK-011）的步骤已不适用；**原生终端页与原生插件页已按 [ADR-0028](ADR/0028-remove-native-terminal-and-plugin-pages.md) 移除（2026-11-17）**——TASK-008 终端、vNext PR-08 终端 V2 与 TASK-009 插件管理的步骤仅作历史，App 侧只保留开发者选项的「PTY 自检」；自动化测试入口见 `AGENTS.md`「构建」，其余真机步骤仍可用于 C# 实现的验收。

完整用例见方案第 15 节。此处记录已实现的测试与 M1 验收步骤。

## 自动化（本机 / CI）

| 层 | 项目 | 覆盖 |
|---|---|---|
| 契约 | `tests/DshMobile.Core.Tests` | 请求/响应 JSON 形状与字段名、错误码常量与 Java 端一致、非法响应解析 |
| Native 单测 | `native/native-bridge/src/test`（JVM） | 协议解析、未知 method、重复 requestId、deadline、关闭后拒绝；`UiToolsRegistrationTest`（patch 层正文、原子写、只删自己写的） |
| Guest 浏览器自检 | 同上（`browser-check.test.mjs`） | 假插件客户端：status+observe 只用只读方法且不带控制代次、无会话 / 已关闭仍带代次 / 会话中途消失 → exit 2、通道不可用 → 3、自动化关闭不当成功、客户端缺失 → 6 |
| Guest 门面 | `node --test native/native-bridge/src/test/guest/`（CI 同跑） | 假 Relay CLI 回放真机回包：元素映射、compact/截断、`STALE_ELEMENT`（旧快照零底层调用、控件已变、节点消失）、审批等待重试、拒绝不重试、错误保守映射、MCP stdio 握手（`server/discover` 回落 / `initialize` / `tools/list` / `tools/call`）、guest 自检退出码 |

## M1 验收（需真机，手动 + 日志）

1. `adb install` 后打开 App，首页显示 Bridge 版本与 `bridge.info`（含 `kotlinVersion`、`sdkInt`、`abis`）。
2. 点击 “DSHBox Kotlin 调用”：显示 `Versions.compare("0.1.1","0.1.1-rc.2") = 1`。
3. 点击 “1000 次压测”：完成后显示 `ok=1000 failed=0 inFlight(C#)=0 inFlight(Native)=0` 与耗时。
4. 压测进行中旋转屏幕、切后台再回来：压测继续完成、计数一致、无崩溃。
5. `adb shell am kill` 结束进程后重开：Facade 重新初始化，显示正常。
6. 重复第 3 步至少 10 次。以每轮日志 `stress: after GC managed=… java=… grefs=…`（强制双端 GC 后测量）判断泄漏：`grefs` 与 `java` 不应随轮次持续增长。`dumpsys meminfo` 未 GC 的数值只作参考。
7. 在 Android 10 与 Android 16/17 至少各一台执行，结果写入 `docs/COMPATIBILITY.md` 真机矩阵。

## M2 验收（需真机）

前置：`pwsh runtime-bundle/fetch-runtime.ps1`；`adb push artifacts/runtime/downloads/dshapp-runtime-debian-arm64-0.1.0.zip /sdcard/Android/data/<applicationId>/files/`。

1. 全新安装后点「准备运行环境」：提示 base/node 缺失。
2. 点「导入运行环境包」：`runtime=True node=True`，DSH 层已装配；记录耗时。
3. 点「启动 DSH」：sandbox RUNNING、dsh READY、`token=True`；记录耗时。
4. 点「诊断」：health 全 true；`node`/`dsh` 版本与 `runtime-assets.lock` 一致。
5. `adb shell ps -A | grep <app user>`：运行中有 `libproot.so` / `node`；HOME 后台 ≥ 90 s 仍在。
6. 点「停止」后再 `ps`：仅剩 App 进程。启动后 `am force-stop`：App 用户下零进程。
7. 待补：磁盘不足、层缺失（删除 `runtime-current/node`）、损坏的 zip、导入中断、强杀后再次启动。

## TASK-005 DSH 模式探测（需真机）

1. 运行环境已导入；点「启动 DSH」，状态行须 `shim=True`；`adb shell ps -A -o PID,NAME,ARGS | grep node` 须含 `--import /opt/dshbox/link-shim.mjs`。
2. 点「DSH 模式探测」，等待 `probe summary`。期望：web/sdk/sdk-minimal/acp = pass，headless = limited（无凭据），plugin 视 pnpm 供给情况。
3. 每个 `FAIL` 检查行带退出码与 stderr 尾部，写入 COMPATIBILITY.md。
4. 待补（需模型凭据）：headless 一次性任务、sdk `session/prompt`、acp `session/new`+`session/prompt`、子代理、工具调用与审批。

## TASK-006 WebUI（需真机）

1. 启动 DSH 后点「打开 DSH」：状态 `WAITING_TOKEN → LOADING → LOADED`，显示官方 WebUI 首页。
2. `adb logcat --pid <app>` 中 `token=` 只允许 `***`（DSHBox 脱敏）与 `BinderProxy`，其它任何出现都算失败。
3. 返回键：无历史时关闭页面并 `DETACHED`。
4. 停止 → 启动 DSH（新 token）→ 重开：仍 `LOADED`。
5. 待补：页面打开时重启 DSH（就地重载）、旋转、外链、软键盘、文件选择与下载（TASK-007）、401 恢复。

## TASK-007 文件 / SAF（需真机；选择器步骤由人手动）

1. 首页「文件」：列出 `/root/projects`；`.dsh` 带 🔒。进入 `.dsh`，点 `.credentials.yaml` 必须被拒绝（无预览/导出）。
2. 「导入」→ 选择器选文件 → 状态「已导入文件 N: …」，列表出现该文件。
3. 点该文件 →「导出到…」→ 选择位置 → 状态「已导出 …」；目标文件大小一致。
4. WebUI 附件：先在 DSH 中选定工作区，再点输入框「+」→ 选择器选文件 → 附件出现在输入框（不发送）；日志 `webui file chooser: N file(s)`。
5. 首页「导入运行环境包」→ 选择器选 zip → 日志 copy / import 耗时与 `runtime=True node=True`。
6. 单元测试覆盖：路径规范化、符号链接（含悬空）拒绝、受保护目录、不覆盖、原子写入、文件名净化（`WorkspaceFileSystemTests`）。

### vNext PR-09 文件工作台（需真机 + 截图，[ADR-0026](ADR/0026-file-providers-trash-conflicts-archives.md)）

自动化：`WorkspaceProviderTests`（14 例）、`FileWorkbenchTests`（33 例：编码 / 行尾、排序 / 搜索、30,000 个文件、zip slip 六种写法、tar 链接 / FIFO、压缩往返）。

1. **闭环**：菜单「新建文件夹」「新建文件」（中文名）→ 点文件「编辑」输入内容 → 保存 → 长按多选 →「复制」→ 进入另一文件夹「粘贴到此处」（重名得到 `name (1)`）→ 「导出到…」到下载目录 → 「删除」→ Snackbar「撤销」恢复。
2. **冲突**：编辑器打开 `a.txt` 不保存；在终端 `echo other > /root/projects/a.txt`；回到编辑器保存 → 出现「文件已被修改」；分别验证「继续编辑」「放弃并重新加载」「用我的版本覆盖」。
3. **编码**：GBK 文件（`printf '\xd6\xd0\xce\xc4' > gbk.txt`）打开为只读并说明原因；CRLF 文件编辑保存后 `file` / `od -c` 仍为 CRLF；`chmod +x run.sh` 后编辑保存仍可执行；二进制文件不打开。
4. **受保护**：`.dsh` 可进入浏览但条目不能选中、没有任何动作；不能把文件粘贴 / 解压到 `.dsh`。
5. **链接**：终端 `ln -s /etc etc-link`：列表显示链接与目标，只能改名 / 移动 / 删除；删除后 `/etc` 不受影响；复制含链接的文件夹时提示跳过数。
6. **压缩包**：多选 →「压缩为 zip」→「解压到此处」得到新文件夹；导入一个含 `../evil` 条目的 zip（PC 上用 `python -c "import zipfile;z=zipfile.ZipFile('evil.zip','w');z.writestr('../evil','x')"` 生成）→ 解压报 `ARCHIVE_UNSAFE`，工作区与上级目录都没有新文件。
7. **大目录**：终端 `mkdir big && cd big && seq 1 30000 | xargs touch` → 进入 `big` 滚动流畅、排序 / 筛选可用；「搜索子文件夹」有结果数与是否截断。
8. **回收站**：删除后 `run-as <applicationId> ls files/dshmobile-files-trash` 有批次目录；撤销后批次消失。
9. 记录设备、Android 版本、App 版本、时间与截图；逐项写通过 / 失败 / 不适用及原因。

## TASK-008 终端（已废弃，需真机）

> **已废弃（[ADR-0028](ADR/0028-remove-native-terminal-and-plugin-pages.md)，2026-11-17）**：App 不再有终端页，下列步骤只能在 ADR-0028 之前的版本上复现。需要命令行时在 DSH 内完成；App 侧只剩开发者选项的「PTY 自检」。

注意：若设备使用中文等组合式输入法，`adb shell input text/keyevent` 注入会被输入法拦截，命令需人工输入。

1. 首页「终端」：出现会话标签「终端 1」与提示符。
2. 输入 `whoami; id; uname -m; node --version`：分别为 root / uid=0 / aarch64 / 与 runtime-assets.lock 对应的 Node 版本。
3. `sleep 30` 后点键栏 ^C：应立即返回提示符并回显 `^C`。
4. 返回首页再进入终端：同一会话与输出仍在。
5. 「+」两次 → 两个标签；点标签切换；「×」关闭活动会话；`adb shell ps -A` 中对应 `libproot.so/bash` 消失。
6. 关闭最后一个会话：显示「没有打开的会话」提示；「+」后恢复终端。
7. 首页「停止」：App 用户下沙箱进程为 0。
8. 待补：中文/emoji/宽字符、大量输出滚动、横屏与分屏、软键盘遮挡、粘贴大段文本。

### vNext PR-08 终端 V2（已废弃，需真机 + 截图，[ADR-0025](ADR/0025-terminal-v2-flow-control-and-links.md) → [ADR-0028](ADR/0028-remove-native-terminal-and-plugin-pages.md)）

> **已废弃（2026-11-17）**：终端页、xterm.js 资产、`tests/terminal-web`、`tooling/terminal-stress.mjs`、`tooling/terminal-web.lock.json` 与相关单测已随 ADR-0028 删除，下列步骤和上面的自动化入口都不再可执行，仅作历史。

自动化：`tests/terminal-web`（真实 vendored xterm.js headless：UTF-8 跨批拆分、Unicode 11 宽度、ack、搜索 / 复制 / 字号消息、链接过滤、4 MiB 压力）、
`TerminalOutputPumpTests`（窗口、停摆解除、读取端背压）、`TerminalKeysV2Tests`、`TerminalLinksTests`；`node tooling/terminal-stress.mjs 100` 为本机吞吐。

1. **大输出与背压**：`seq 1 5000000`、`yes | head -c 100000000 > /dev/null; yes | head -c 50000000`：输出期间界面可滑动、菜单可开；
   `adb shell dumpsys meminfo <applicationId>` 前后对比，App 内存不随输出线性增长；结束后提示符与 `echo ok` 正常。输出中切到别的 App 30 s 再回来：命令仍在推进或已完成。
2. **中文 / emoji / 宽字符**：`printf '中文😀é|\n'`：`|` 对齐在第 8 列；`ls` 含中文文件名的目录列对齐。
3. **搜索**：菜单「搜索」→ 输入 → 上一处 / 下一处、计数「n / m」，无匹配显示「无匹配」；关闭后键盘输入回到 shell。
4. **复制 / 全选 / 粘贴**：长按拖选 →「复制选中内容」提示已复制，粘贴到别的 App 一致；未选中时提示先选择；「全选」后复制得到可见输出。
5. **字号**：放大 / 缩小到边界（8 / 32）后不再变化，行列随之重算（`stty size` 变化）；重进 App 后保持。
6. **重命名**：改名后标签显示新名，留空恢复「终端 N」；超长名截断为 32 字。
7. **链接**：`echo https://example.com` 点链接 → 确认框显示主机 → 浏览器打开；`echo https://google.com@example.com` 显示 user@ 警告；`echo file:///etc/passwd` 不可点。
8. **ALT / 辅助键**：ALT 锁定后按 `b` 发 `ESC b`（readline 回退一个词）并自动解除；「辅助键设置」勾选 ^D、`|` 后键栏立即更新，「恢复默认」还原。
9. **TUI**：`vim`、`nano`、`htop`、`tmux`（若 guest 已装）：方向键、退出、窗口大小变化正常；`ssh` 待 PR-11。
10. 记录设备、Android 版本、App / DSH / Node 版本、时间与截图；逐项写通过 / 失败 / 不适用及原因。

## TASK-009 插件管理（已废弃，需真机）

> **已废弃（[ADR-0028](ADR/0028-remove-native-terminal-and-plugin-pages.md)，2026-11-17）**：原生插件页与安装守卫已删除，插件由 DSH 自己安装、列出和卸载；下列步骤与 `PluginSpecTest` 仅作历史。

1. 首页「插件」：`list: OK`，显示 web profile 已装插件（全新为空）。
2. 输入包名 →「安装」→ 确认警告 → `add <spec>: OK`，列表出现；DSH 重启后生效。
3. 点列表项 →「卸载」→ `remove <name>: OK`，列表移除。
4. 非法输入（`a; id`、`../x`、`https://…`）必须被拒绝为 `BAD_REQUEST`（亦由 `PluginSpecTest` 覆盖）。
5. 「DSH 模式探测」中 plugin 项应为 pass。
6. 待补：真实 DSH 插件加载、断网失败提示、安全模式恢复、镜像源。

## TASK-010 手机操控（只读，需真机；无障碍开启与审批由人手动）

1. 首页「手机操控」→「开启中继」：`Relay: STARTING/RUNNING`；末行「DSH 工具（mcp__ui__*）：已注册，下次启动 DSH 时加载」。
2. 无障碍未开时「只读自检」：`ui_snapshot` 报 `PROVIDER_UNAVAILABLE`（Relay `E_GATE_SYSTEM_MISSING`）。
3. 点「打开无障碍设置」，人工开启服务后返回：`无障碍服务：已开启`，`信箱运行中`。
4. 「只读自检」→ 下拉通知栏：
   - 「允许一次」：快照与回读各弹一次，exit=0，输出含 `ui_list_devices: ["android-local:online"]`、`ui_snapshot`、`ui_get_element … ok`；
   - 「本次会话允许」：`ui.snapshot` 与 `ui.node` 各一次，之后同会话不再询问；
   - 「拒绝」：`POLICY_DENIED`，自检不重试；不作答：约 50 s 后 `APPROVAL_REQUIRED`（exit=5）。
5. 首页「启动」后：`adb shell ps -A -o PID,USER,ARGS | grep ui-facade` 须见 `node /opt/dshbox-cache/dshmobile/ui-facade.mjs`，
   DSH 命令行含 `--patch …/dsh-official-plugin-overlay.yml`；「手机操控」末行为「已在当前 DSH 中加载」。
6. 「关闭中继」：末行「已注销，当前 DSH 仍保留到重启（调用会失败）」；重启 DSH 后不再有 `ui-facade` 进程。
7. 待补：模型实际调用 `mcp__ui__*`（需凭据）、通知被关时 `E_GATE_NO_FOREGROUND`、安全窗口（`SECURE_CONTENT`）、Android 10。

## TASK-011 浏览器（需真机；浏览器内输入网址、AI 开关与控制审批、安装确认由人手动）

1. 首页「启动」后进入「浏览器」：「浏览器通道：运行中」、guest CLI 就绪、「browser_* 工具：未安装」。
2. 未打开浏览器时「只读自检」：`status: OK state=CLOSED`，exit=2。
3. 「打开浏览器」：焦点为 `com.dshbox.browser.runtime.BrowserActivity`，`ps` 出现 `org.example.dshmobile:browser`，无崩溃；返回按钮为「返回 DSH Mobile」。
4. 在浏览器内加载页面并打开「AI 控制」，返回后「只读自检」：`status` OK（session ≥1、`automationEnabled: true`），`observe` OK 且含 url / title / 正文 / 元素数，exit=0。
5. 「安装 browser_* 工具」→ 确认：`Browser adapter assembled`，exit=0；状态「已安装，需重启 DSH」。
6. 首页「停止」→「启动」：状态「已在当前 DSH 中加载」，无插件失败日志行；「只读自检」末行 `--dump-config` 计数 ≥1。
7. 「移除 browser_* 工具」：exit=0，状态「已移除，当前 DSH 仍保留到重启」。
8. 待补：模型实际调用 24 个 `browser_*`（需凭据）、`browser_control` 请求 → 浏览器内批准、点击 / 输入 / 滚动、上传 / 下载审批、截图、网络捕获、iframe / SPA / 重定向、断网、Cookie 与 DSH WebUI 隔离实测、`:browser` 进程内存占用、Android 10。

## TASK-012 远程主机（需真机 + 一台 SSH 主机；主机侧管理员操作、表单输入、指纹核对由人手动）

前置（本机 Windows PC 作测试主机）：管理员运行 `tooling/setup-windows-ssh-host.ps1 -PublicKeyFile <手机公钥>`；普通用户
`npm install -g @deepseek-ai/dsh@0.2.1-rc.5`；`adb reverse tcp:2222 tcp:22`。

1. 「远程主机」：显示 `ecdsa-sha2-nistp256` 公钥与指纹（硬件保护）；PC 上 `ssh-keygen -lf` 指纹一致。
2. 填 `127.0.0.1` / `2222` / 用户 / `3091` →「保存主机」→「连接」：弹出主机指纹，与 PC `ssh-keyscan` / `ssh-keygen -lf` 一致后确认 → `CONNECTED`，显示远端 node / dsh 版本。
3. 「启动远端 DSH」：`READY`，转发 `127.0.0.1:<随机> → 3091`，`HTTP 401`，token「已在本机原生侧持有」；PC 上 3091 仅监听 127.0.0.1；logcat 中无 launch token。
4. 「停止远端 DSH」：回到 `CONNECTED`，token 无，「远端 DSH 已不再响应」；PC 上 3091 无监听。
5. 断线：`adb reverse --remove-all` + `adb kill-server` 约 60 s 后恢复，重新进入页面：`DISCONNECTED · UNREACHABLE`，token 无；「连接」不再弹指纹直接 `CONNECTED`；再启动得到新转发端口与新 token。
6. 隔离：远端 `READY` 时首页「启动」本机 DSH，两者同时 READY、互不影响；「断开」后远端端口释放、本机 DSH 不变。
7. 待补：`HOST_KEY_CHANGED`（换主机密钥或改指向）、`AUTH_FAILED`（移除公钥）、真实局域网 / 公网主机、远端 WebUI（独立进程）、Linux 主机、Android 10。

## vNext PR-07 Harness Agent E2E（发布硬门禁，需真机 + 负责人自己的模型凭据）

自动化部分（CI，不需要凭据）：`tests/guest/dsh-e2e.test.mjs`（假 DSH：通过 / 缺凭据 / 谎报成功 / 续接失忆）；`runtime-layers`
的 `tooling/runtime-smoke.sh` 在 arm64 chroot 中对真实 DSH 运行同一驱动，三个套件必须报告 `needs-credential`，并用 DSH 自己的解析器验证默认工作区修复。
**CI 结果不能代替下列真机步骤**（AGENTS.md 规则 9）。

前置：Release 构建；运行环境就绪、DSH READY；记录 App / DSH / Node / Runtime 版本、设备、系统版本、开始时间。凭据**只在设备上**输入，不要粘贴到聊天、
Issue、PR、截图或终端历史里；日志贴出前再检查一次。

1. **默认工作区**：全新安装（或删除 `/root/projects/deepseek-harness`）后在 WebUI 首次新建会话，不再出现「无法创建默认工作区」；
   终端里 `xdg-user-dir DOCUMENTS` → `/root/projects`，WebUI 显示的工作区为 `/root/projects/deepseek-harness/default-workspace`。
2. **凭据**：在 WebUI 的 **Models** 页面配置 API Key（DSH 经其 credentials service 保存；不要在终端 `export DEEPSEEK_API_KEY`，会留在 shell 历史与进程环境里）；新建文本会话得到回答。缺凭据时 DSH 报 `MISSING_CREDENTIAL`。
3. **驱动（终端执行）**：
   ```sh
   node /opt/dshmobile-cache/dshmobile/dsh-e2e.mjs 2>&1 | tee /root/projects/e2e-$(date +%Y%m%d-%H%M%S).log
   ```
   期望最后一行 `DSHE2E {"status":"pass",…}`、退出码 0：headless（`--json` 任务：shell 工具写 nonce 文件 → 工具调用 + 完成结果 + 文件内容 + 回答；
   新进程 `--session-id` 召回）、sdk（initialize → session/prompt → session/wait；新进程 session/resume 召回）、acp（session/new → session/prompt，
   权限请求以 allow-once 应答并计数 → end_turn；session/list；新进程 session/resume 召回）。若用的不是 `deepseek-official` / `deepseek-v4-flash`，
   加 `--provider … --model …`（sdk 必须显式指定）。单项：`--suites acp`。
4. **审批（WebUI，手动）**：普通权限模式下让模型执行一条 Bash 命令与一次文件修改，审批卡正确显示，拒绝后命令不执行、允许后执行；会话在 App 强杀重开后仍在、可继续（Resume），Fork 后两条会话互不影响。
5. **插件 / MCP / Skills**：在 DSH 内安装一个插件（原生插件页已按 [ADR-0028](ADR/0028-remove-native-terminal-and-plugin-pages.md) 移除）→ `--dump-config` 含其条目 → 模型实际调用一次该插件或 MCP 工具（含 `ui_*` 只读工具）→ 卸载后不再出现；Skill 被调用一次。
6. **Subagent / Agent Team**（DSH 版本支持时）：一次派生子代理的任务完成，父会话收到结果。
7. **后台 / 锁屏**：一个长任务运行中锁屏 15 / 60 / 180 分钟后回到前台，任务完成或给出明确失败；停止后 `ps` 无孤儿；运行中 `am force-stop` 后重开，journal 报告中断并可再次启动。
8. **WebView 回归**：附件上传、文件下载、令牌失效后 401 重新认证。
9. **清除凭据后**：在 WebUI 的 Models 页面删除凭据，重跑第 3 步，期望 `status: needs-credential`、退出码 3；`grep -rIl -- '<密钥前 8 个字符>' /root/projects/.dsh /root/projects/*.log`
   无结果（只用前缀检索，不输入完整密钥）。

证据：按 [migration/parity/TEMPLATE-vnext-pr07-agent-e2e.md](../migration/parity/TEMPLATE-vnext-pr07-agent-e2e.md) 记录，每个「通过」都写明版本、设备、系统、时间；不适用项写原因，不写「通过」。
