# ADR-0009：C# Relay 宿主可选上线——独立信箱、仓库内 Java Shell 服务、Shizuku 状态轮询

- 状态：已接受（2026-11-14，PR-07 第九段；用户要求「写路径、Direct、Shizuku、协调器 / 容器 / 服务归属一次性迁移完毕，再统一真机测试」）
- 关联：[ADR-0007](0007-source-ownership-csharp-first.md)（C#-first）、[ADR-0008](0008-open-mobile-automation-writes.md)（写操作开放与防自批）、
  `docs/MIGRATION.md` PR-07 第九段、证据 `migration/parity/2026-11-14-PTP-AN10-A17-relay-host.md`

## 背景

PR-07 第一至八段把 Relay 的信封、准入、执行面、闸门、确认框与无障碍读取逐段移植成 C#，旧 Relay 始终是唯一决策者。
剩下的执行层（无障碍写路径、Direct、Shizuku）与把它们装配起来的协调器 / 容器 / 前台服务一起移植：它们彼此依赖，
单独上线任何一块都无法在真机上走通一次请求。按 ADR-0007 规则 3，新实现必须先在 LegacyWithShadow 下与旧实现比对。

## 决策

1. **C# 宿主可选、独立信箱**。`RelayHost`（Runtime，移植 `RelayContainer` / `RelayRuntime` / `RelayRuntimePhase`）由
   `CsRelay`（Android 组合根）在用户点「启动 C# 中继」时启动，只服务 `files/pilot-cs/run`，**不绑进 guest**；
   guest 的 `ui_*` 仍由旧 Relay 服务。C# 宿主读同一份 `relay` 偏好（档位、执行面偏好、shell 动词开关），
   `digest_salt` 只在缺失时写入。默认 `MigrationOptions.RelayHost = LegacyWithShadow`。
2. **审批不弱化**。C# 宿主的每次需要审批的调用都走 C# 通知卡 → 应用内确认页（ADR-0008 的一次性令牌与本 App 窗口内按下），
   `UiInForeground` 固定为 false，应用内卡片暂不接入；C# 宿主不自行开启任何系统权限、无障碍服务或 Shizuku 授权。
3. **双中继比对**。`RelayHostShadowCheck` 把同一组请求依次发给两个信箱（各自审批），去掉计时字段后比对；
   只作用于独立包名的调试测试目标 `org.example.dshmobile.testtarget`（`tests/DshMobile.TestTarget`，不发布），
   依赖屏幕状态的调用每边先复位测试目标。
4. **仓库内 Java Shell 服务**。Shizuku 用户服务的远端半边运行在 shell uid 的 `app_process` 里，没有 .NET 运行时，
   只能是 dex 中的 Java 类。为此新增最小类 `dshmobile.relay.ShellService`（`src/DshMobile.Android/Java/`，
   由上游 `RelayShellService.kt` 翻译，GPL-3.0，登记于 `docs/UPSTREAMS.md`），经 `AndroidJavaSource` 编入 APK，
   `relay-shell.pro` 防裁剪。这是 ADR-0007 规则 2「必须原生的能力放进本仓库可追溯的最小层」在 JVM 侧的对应物：
   它不依赖 DSHBox AAR，只承载事务码分派（FIRST_CALL+{0 RUN,1 INSTALL,2 CREATE,3 RELEASE,4 CAPTURE,5 QUERY,7 SCREENSHOT}），
   决策全在 C#。
5. **Shizuku 状态轮询**。rikka Shizuku 的 Binder 收到 / 死亡与授权结果监听器接口没有 C# 绑定。C# 宿主每 3 s
   读一次 `(pingBinder, checkSelfPermission)`，变化时调 `RelayHost.OnShizukuStateChanged`，同一时钟驱动 `OnHeartbeat`。
   代价是状态变化最多晚 3 s 被看到；绑定 / 解绑仍按上游规则只在两者同时成立时进行。
6. **暂不移植**（仍由旧实现负责，切换切片再处理）：GuestAssets / 能力表发布（`capabilities.json`）、悬浮窗、
   无障碍缺口提示、应用内审批卡。C# 宿主不发布任何 guest 可见文件。
7. **保留上游缺陷以保持影子可比**：`media.write` 类型嗅探的 RIFF 偏移（见 `docs/UPSTREAMS.md`）。切换为 C# 默认时单独修复并更新向量。

## 后果

- 真机比对发现并修复一个只存在于 C# 的缺陷：`StorageReaper` 的可用空间默认探测用了路径根 `/`（Android 上为只读系统分区），
  导致所有产物预留失败；现改为 statvfs 目录本身，与上游 `StatFs(dir)` 一致。
- 下一切片（切换）要做：C# 宿主接管 guest 信箱与 GuestAssets 发布、应用内审批卡、C# 无障碍服务成为唯一服务、
  修复保留的上游缺陷；之后才删除该切片的 DSHBox 依赖（ADR-0007 规则 3）。
- 每次重装 APK，部分 ROM（含本次 Honor）会关闭本 App 的无障碍服务，比对前需用户重新开启。
