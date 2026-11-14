# 2026-11-14 · Honor PTP-AN10 · Android 17 (API 37) · C# Relay 接管 guest 信箱（PR-07 切换切片，ADR-0010）真机记录

设备：PTP-AN10，`AN6B024B01054420`，Shizuku 13。构建：Debug（`dotnet build -t:Install`），基于 `f74b883` + 未提交的切换切片改动。
主 App `org.example.dshmobile`（uid 10267）。

前置条件由用户本人完成：在 Shizuku 里授权、点「手机操控 → 开启中继 → C# Relay（实验）」。App 与主机都没有代为开启服务、
代为授权或回答审批；主机端只用 `run-as` / `dumpsys` 观察，没有执行 `uiautomator dump`。

## 第一轮（09:00–09:05，无障碍服务未开启）

重装 APK 后本 ROM 关闭了本 App 的无障碍服务；`enabled_accessibility_services` 里只有第三方应用的服务。本轮因此只验证接管本身与失败即关闭。

### 接管

- `dumpsys activity services`：只有 C# 的 `dshmobile.relay.RelayCsChannelService` 在前台；旧 Relay 已由 `relay.stop` 停止，没有两个 Relay 同时服务一个信箱。
- `relay.log`：`runtime starting → channel started → control channel started → runtime running`，`shizuku user service connected`，
  `shizuku state binder=true granted=true` 只记一次。
- 影子 `automation.ui-tools` 01:00:53Z / 01:01:39Z 两次 Agree（`registered=True conflict=False`，层逐字节一致）：`ui_*` 工具层已由 C# 侧写入。

### guest 文件（`files/pilot` ↔ guest `/opt/pilot`，09:01 由 C# 宿主发布）

```text
bin/relay          41399 B  -rwx--x--x  sha256 9cb271f6…ff9b   = 旧 Relay 提交标记中的哈希
USAGE.md           59536 B  -rw-------  sha256 40470726…6dd8   = 旧 Relay 提交标记中的哈希
capabilities.json  13277 B  -rw-------  sha256 2cc62ea2…9446   与向量不同：设备状态不同（见下）
version.json         295 B  {"layout":1,"files":{…三份哈希…},"publishedAtWall":1794618100}
```

`capabilities.json` 与向量 `relay-guest/capabilities.PTP-AN10-A17.json`（旧 Relay 在无障碍开启时发布）逐条比较：能力顺序相同，
`protocol` / `entry` / `host` / `backend.shizuku`（running / authorized / bound / shellUid 2000 / version 13 / trustedDisplay 未建）相同；
只有 16 条不同，全部由无障碍服务未开启解释：

- 15 条无障碍能力（`ui.snapshot` / `ui.node` / `ui.tap` / `ui.swipe` / `ui.click` / `ui.longClick` / `ui.select` / `ui.dismiss` /
  `ui.scroll` / `ui.setValue` / `ui.waitFor` / `ui.setProgress` / `ui.imeAction` / `ui.text` / `ui.key`）：`systemPermission`
  `GRANTED → MANUAL_ONLY`，`usable true → false`，`surface` 字段去掉。
- `screen.capture`：`GRANTED → SESSION_CONSENT`（无障碍截图不可用，退回会话授权），仍 `usable`。

### guest 请求经 C# 宿主

```text
request state created id=mwfpaeyb-cvqltdvx cap=ui.snapshot     （guest 经 control-inbox 提交）
control op=submit result=ok ms=12
request accepted / admitted
gate call done cap=ui.snapshot result=denied ms=16             E_GATE_SYSTEM_MISSING（系统权限 MANUAL_ONLY）
request completed state=FAILED ms=31 · response published bytes=274
```

请求确实到达 C# 宿主，并按无障碍未开启的事实失败即关闭，返回稳定错误码；没有伪造成功。

### 日志噪声排查

`remote submitted cap=lifecycle waitMs=3000` 在本轮约每分钟一条：这是 Shizuku 后端周期巡检的 `QueryDisplay`
（`ShizukuBackend.CapLifecycle`，与上游 `CAP_LIFECYCLE` 相同），不是缺陷。日志中更早的大量同类行来自前几天的运行。

## 第二轮（09:08–09:10，用户开启了 C# `dshmobile.relay.DshAccessibilityService`）

`enabled_accessibility_services` 含 `org.example.dshmobile/dshmobile.relay.DshAccessibilityService`；前台仍只有 C# 通道服务。

### 能力清单重铺

无障碍绑定后 C# 宿主自动重铺（`publishedAtWall` 1794618100 → 1794618520）。新 `capabilities.json` 的 SHA-256 为
`50e5ea8e…734c`，与向量 `relay-guest/capabilities.PTP-AN10-A17.json`（旧 Relay 在同一台设备、同样事实下发布）**逐字节一致**。

### 自检（guest `relay doctor` + `ui-facade-check.mjs`，`process-guest.log`）

```text
relay doctor   ok=true · pack capabilities/guide=true · mailbox inbox/outbox/artifacts 可写 · manifest 39 项 / 29 可用
ui facade      tools 10 个（ui_list_devices … ui_screenshot）· ui_list_devices ["android-local:online"]
               ui_snapshot ok（app org.example.dshmobile，17 节点）
               ui_get_element e2 → STALE_ELEMENT（treeWarning: node not visible on display 0）· UICHECK exit 3
```

C# 宿主侧（`relay.log` + `pilot-audit/audit-20261114.jsonl`）：

```text
09:09:11  ui.snapshot  E_AWAITING_CONSENT（通知卡，8 s 内未答，安全重试）
09:09:13  ui.snapshot  approved_session → a11y ok 35 ms，回包 4031 B
09:09:21  ui.node      E_AWAITING_CONSENT（nodeId=2）
09:09:27  ui.node      approved_session → a11y ok 23 ms，回包 1544 B：同一个 TextView（文字、类、包名一致），visible=false + treeWarning
```

`exit 3` 不是 C# 缺陷：第二次 `ui.node` 在用户于通知栏点「本次会话允许」后 23 ms 内读取，通知栏仍覆盖在本 App 之上，
节点 `isVisibleToUser=false`，`ui.node` 按上游规则附 `treeWarning`，facade 按 ui_mcp 契约报 `STALE_ELEMENT`（「屏幕变了，重新快照」）。
上游没有「等通知栏收起再派发」的步骤，旧 Relay 遇到通知栏审批时同样如此；旧 Relay 在本 App 位于前台时改用应用内审批卡，
因此不经通知栏——这正是 ADR-0010 第 8 条记下的未移植项（应用内审批卡）。审批、闸门与失败即关闭均按预期。

**更正（第三轮推翻了上面的通知栏解释）**：见下。

## 第三轮（09:14，会话授权有效，无审批）

```text
09:14:44  ui.snapshot  decision=auto → a11y ok 30 ms（4030 B）
09:14:44  ui.node      decision=auto → a11y ok 2 ms（1541 B），nodeId=2 仍 visible=false + treeWarning → facade STALE_ELEMENT，UICHECK exit 3
```

没有审批、没有通知栏，结果不变，说明原因不是通知栏。截屏（`screencap`）与快照坐标表明：页面已向下滚动，
节点 2（页首说明文字）只剩 0–161 px 一条，整条落在状态栏 + 顶部应用栏（深色，标题节点 192–285 px）后面，用户确实看不到；
`isVisibleToUser=false` 是框架给出的真实状态（被 ScrollView 的内边距裁掉），`ui.node` 附 `treeWarning`、facade 报 `STALE_ELEMENT` 都正确。
快照只给裁到屏幕内的边框、不带可见性，`ui-facade-check.mjs` 按「第一个有边框且有名字的元素」选目标，于是选中了被应用栏挡住的 e2。
这与哪个 Relay 无关（同一棵无障碍树、同一规则；第七段 `ui.node` 影子比对相等），属于旧桥自检脚本的选点局限，按 AGENTS.md 规则 2 不给旧桥加需求。

C# 宿主在本轮的两次读取都成功、均为会话授权自动放行，审计完整。

## 第四轮（09:17，用户把页面滚到顶部后自检）

```text
09:17:32  ui.snapshot  auto → a11y ok 7 ms（4030 B）     节点 2（页首说明）0–314 px，节点 3（状态）314–1267 px
09:17:32  ui.node      auto → a11y ok 3 ms（1542 B）     nodeId=2 visible=false → STALE_ELEMENT，UICHECK exit 3
```

截屏：页面在顶部，状态栏 + 顶部应用栏（深色，约 0–337 px）仍盖住整段页首说明，「Relay: STOPPED」紧贴应用栏下沿。
### 根因（`dumpsys activity org.example.dshmobile` 视图树，只读）

```text
ActionBarContainer 0–337 · ScrollView 0–2800 · LinearLayout 337–6955（ScrollView 上内边距 337 px，全面屏内边距正确）
  TextView 页首说明 16–424（滚动为 0 时在屏 353–761，完全可见）
  ...
  TextView 输出（可选中文字）{VFED..CL. .F......}  ← 持有焦点
```

布局与全面屏内边距都正确，上面「内容从 0 px 起排」的推断是错的。真正原因：输出 `TextView` 设为可选中文字后在触摸模式下可获焦点，
窗口初始焦点落在它上面；自检运行时输出变长，`ScrollView` 为让获焦视图保持可见而**自行滚动**（快照坐标反推滚动约 447 px），
页首说明被推到应用栏下（屏 −94–314，落在 ScrollView 内边距区内，用户看不到）。guest 的 `ui.snapshot` 给出裁剪后的边框，
自检脚本选中它，`ui.node` 如实报告 `isVisibleToUser=false`。Relay（C# 与旧实现同一规则）的行为正确。

修复（本 App 页面，C#）：`AutomationActivity` 的 `ScrollView` 自身可获焦（`BeforeDescendants`）并在创建时请求焦点，
输出文字只在用户点它时才获焦，自检期间页面不再自行滚动。

## 第五轮（09:30–09:32，带页面焦点修复的构建）

构建经 FastDev 部署（设备上 `DshMobile.Android.dll` 与本地产物 MD5 一致 `db5e68dd…`）。重装后系统再次关闭本 App 的无障碍服务，
用户本人重新开启 C# `DshAccessibilityService`、选「开启中继 → C# Relay（实验）」、在页面顶部点「只读自检」。

```text
runtime starting · shizuku state binder=true granted=true · control channel started · runtime running · shizuku user service connected
version.json  bin/relay 9cb271f6…  capabilities.json 50e5ea8e…（= 向量，逐字节一致）  USAGE.md 40470726…
09:31:44  ui.snapshot  通知卡 → approved_session → a11y ok
09:31:50  ui.node      通知卡 → approved_session → a11y ok
09:31:56  ui.snapshot  auto → a11y ok 7 ms（3939 B）
09:31:56  ui.node      auto → a11y ok 3 ms（1071 B）

relay doctor   ok=true · manifest 39 项 / 29 可用 · 信箱可写
ui facade      ui_list_devices ["android-local:online"] · ui_snapshot ok（17 节点）· ui_get_element e2 ok（role text, enabled）
UICHECK {"exit":0,...}   DSHMOBILE_EXIT=0
```

影子 `automation.ui-tools` 01:31:36Z / 01:31:50Z Agree（层逐字节一致）。

## 结论

C# Relay 以 Guest 模式在真机上接管 guest 信箱：旧 Relay 先停、只有 C# 通道服务在前台；guest 文件由 C# 发布，三份文件与旧 Relay
发布的逐字节一致（`capabilities.json` 在相同事实下与向量一致，事实不同时差异全部可由该事实解释）；guest 的 `relay doctor` 与
`ui_*` facade 自检经 C# 宿主全部通过；审批（通知卡 → 会话授权）、失败即关闭（`E_GATE_SYSTEM_MISSING`、`E_AWAITING_CONSENT`）与审计均按预期。
排查中发现并修复一个本 App 页面缺陷（自检时页面自行滚动），与 Relay 实现无关。

## 未做（不阻塞本切片；翻转默认前补齐或经用户确认豁免）

1. 在 DSH 会话里发一次真实 `ui_*` 调用经 C# 宿主。
2. 「双中继比对」的 `automation.relay-guest-files` 影子报告（Shadow 模式下两边文件的只读比对）。
2. 在 DSH 里发一次真实 `ui_*` 调用（例如 `ui_snapshot`），确认经 C# 宿主成功返回。
3. 可选：旧 Relay 启动 +「启动 C# 中继」+「双中继比对」，得到 `automation.relay-guest-files` 影子报告。
