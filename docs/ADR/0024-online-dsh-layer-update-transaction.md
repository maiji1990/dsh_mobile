# ADR-0024：DSH 层在线更新事务（保留旧层的健康检查）与在线更新界面

- 状态：已接受（2026-11-16，vNext PR-05 + PR-06，负责人要求两者一起推进）；**无真机证据**，真机验收见下「验收」
- 关联：[ADR-0022](0022-signed-release-manifest-readonly-discovery.md)（签名清单、只读发现、下载器）；vNext P0-02（DSH 层换层日志化事务）；
  [ADR-0021](0021-product-ui-material3.md)（界面）；AGENTS.md 规则 5（不伪造成功）、9（真机证据）、11（安全不削弱）

## 背景

签名清单、只读发现（PR-02）与安全下载器（PR-04）已就位，但下载好的制品还不能装：DSH 层安装器（P0-02）的事务在新层就位后**立即**退役旧层，
没有“先跑健康检查、不行就换回”的窗口；协调器在每次启动 DSH 前都会运行层恢复，若直接用“启动 DSH”来做健康检查，恢复会把正在检查的新层撤销。

## 决策

1. **保留旧层的健康检查（probing 阶段）**：`DshLayerInstaller` 的日志新增 `probing`——新层在 `dsh`、旧层仍在 aside。
   `InstallAsync(..., holdForProbe: true)` 停在这里；`CommitProbed()` 退役旧层，`AbortProbed()` 恢复旧层。默认行为不变。
2. **谁持有、谁保留**：`Recover(keepProbing)` 只有持有该探测的进程（`RuntimeCoordinator` 内存中的标记）才保留它；其他调用——尤其是 App
   被杀后的下一个进程——一律撤销为旧层。协调器的 `SyncAsync`（每个操作开始前）与 `StartAsync` 都按此恢复。于是**任何时刻被杀，结果只有两种：
   旧层，或已通过健康检查并提交的新层**。
3. **更新事务**（`UpdateCoordinator`，Application）：DOWNLOADING（可取消）→ VERIFIED → STOPPING（仅在用户确认后；否则拒绝）→ SWAPPING
   （换层前再次整文件校验，以 hold 安装）→ PROBING（经宿主启动 web：端口 + WebUI + HTTP 401，版本记录为目标版本，再停止）→ COMMITTED；
   不健康 → ROLLING_BACK → FAILED（`UPDATE_HEALTH_FAILED`，旧层已恢复）。每个阶段开始前写 `files/dshmobile-runtime/update-state.json`；
   重启后未完成的记录报告为 `UPDATE_INTERRUPTED`（只报告，磁盘状态由层日志负责）。更新前在运行则更新后重新启动。只改
   `runtime-current/dsh` 与 `files/updates`，不碰 `user-data`。
4. **范围**：只有 DSH 层在线安装。Runtime 包仍为离线导入（body 换层还没有可回滚的健康检查窗口，MIGRATION §7 的 `previous/` 决定未定）；
   Node 随 Runtime 包；App 本身需 Android `PackageInstaller` 与用户确认（未接）。这些行在界面上如实显示 `UPDATE_KIND_NOT_SUPPORTED` 与原因；
   被 Node 要求挡住的新版 DSH 也列出原因，**不会**乱序安装。
5. **界面**（运行环境页「在线更新」）：通道（stable 默认 / preview）、检查更新、四行（已安装 / 可用 / 不可用原因）、「下载并安装 DSH」
   （确认框写明会停止 DSH 与终端、不动工作区与凭据）、下载中可取消（保留已下部分）、重试即再点一次（续传）、上次更新记录、复制诊断。
   不在后台自动安装。

## 后果

- 正面：更新失败或中途被杀都回到能启动的旧层；用户数据不受影响；每一步的失败码可见。
- 代价：健康检查要真正启动一次 DSH（数秒到数十秒）；更新成功后旧层被退役（无 `previous/` 时删除），之后不能一键回退到更新前的 DSH 版本
  ——回退需安装更早的签名版本或 APK 内置版本（「安装内置 DSH」不降级，需另行设计）。
- 未做：App 自身更新、Runtime 在线安装、DSH 回退按钮、下载 / 安装的后台通知。

## 验收（待做，真机）

签名公钥与首个 preview 通道清单就绪后，在 Android 17 arm64 真机（Release 构建）：检查 → 下载（中途断网 / 切后台后续传）→ 安装 → 健康检查
→ 提交；构造一个启动失败的 DSH 层验证自动恢复；安装过程中 `am force-stop` 后重开，确认回到旧层且界面显示“中断”。
