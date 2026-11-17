# ADR-0027：移除 APK 内置 DSH 层，改为导入 / 在线更新

- 状态：已接受（2026-11-17）；**Android 17 arm64 真机已验证覆盖安装、离线导入和启动**（[验收记录](../../migration/parity/2026-11-17-PTP-AN10-A17-dsh-import.md)）
- 关联：[ADR-0020](0020-self-built-runtime-layers.md)（运行层自建与独立 Release）；[ADR-0024](0024-online-dsh-layer-update-transaction.md)
  （DSH 层在线更新事务）；[ADR-0021](0021-product-ui-material3.md)（运行环境页与首页状态卡）；AGENTS.md 规则 5（不伪造成功）

## 背景

APK 一直打包约 90 MB 的 DSH 层（`assets/dsh/<version>.tar.zst`，构建前由 `fetch-runtime.ps1` 从 `dsh-<v>` Release
下载暂存）。DSH 有自己的版本节奏（rc 频繁），这带来三个问题：

1. **体积**：APK 被钉死在发布当天的 DSH 版本，90 MB 资产使每个 APK 都携带它，而多数用户随后立刻被在线更新替换；
2. **节奏耦合**：DSH 升级要重打 APK（assets PR → fetch → CI），而 base / node 早已改为设备端导入；
3. **死代码**：在线更新事务（ADR-0024）就位后，「安装内置 DSH」（`runtime.prepare`）只剩自愈场景，且首次安装路径
   （导入 runtime 包 → 自动装内置 DSH）与在线更新路径并存两套语义。

## 决策

1. **APK 不再打包 DSH 层**：`fetch-runtime.ps1` 只暂存 `android-side` 进 `assets/runtime/`（`.tar.zst` 条目跳过，
   lock 中的 DSH 条目保留为上游钉值记录）；csproj / CI 注释同步。`IBundledAssets` 与 guest 资产
   （`assets/dshmobile`、`assets/toolchain-cache`）不受影响。
2. **删除内置供给代码**：`BundledDsh`（选择 / 计划 / 供给）、`RuntimePreparer`（`runtime.prepare`）、协调器
   `PrepareAsync` 与 BridgeHost 的 `RunPrepareAsync`、`PrepareOutcome` 及其 UI 全部移除；`BundledDsh.cs` 更名为
   `BundledAssets.cs`，只保留 `IBundledAssets` 与 `RuntimeInstallCheck`。导入 runtime 包（`InstallAsync`）后的
   校验只看装后树核对与安装检查，不再有「准备」步骤。
3. **新增「导入 DSH 层」**（`runtime.import.dsh`）：选择本项目 Release 的 `<version>.tar.zst`，也支持 `<version>.tar.gz`。
   复制和安装共用独占操作，每次使用独立缓存目录。协调器计算 SHA-256，安装器解包时重算比对，检查 `bin.js` 和
   `@deepseek-ai/dsh/package.json`，按包内版本与文件名的一致性及已装版本决定是否换层。自算摘要用于检测两次读取之间的变化。
   首次导入通过包结构检查后提交。替换已有层时按 ADR-0024 暂存旧层，待新版本 WebUI 就绪、版本一致且进程正常停止后提交。
   探测失败时恢复旧层。若探测进程未停止，则保留旧层，待停止或进程重启后恢复。文件名无效返回 `BAD_REQUEST`，
   文件缺失返回 `DSH_BUNDLE_NOT_FOUND`，包内入口缺失或版本不符返回 `DSH_BUNDLE_INVALID`，健康检查失败返回
   `DSH_HEALTH_FAILED`，DSH 仍在运行时返回 `RUNTIME_INVALID_TRANSITION`。
4. **界面**：运行环境页「维护」区的「安装内置 DSH」改为「导入 DSH 层」；首页状态卡在 body 已装而 DSH 层缺失
   （journal 无 DSH 版本）时主按钮显示「导入 DSH」。运行环境页显示本次安装的版本或保留的版本。
5. **在线更新不变**（ADR-0024）：下载 → 验签 → hold → 健康检查 → 提交 / 恢复，与本地导入互不干扰，共用
   `DshLayerInstaller` 的换层事务与恢复。

## 后果

- 正面：APK 显著变小；DSH 升级不再要求重打 APK；删掉一条只有内置路径才用的代码链（含其 UI 解析层）；首次安装路径
  统一为「导入 runtime 包 → 导入 DSH 层（或在线更新）」。
- 代价：全新安装需要两步导入（runtime 包 + DSH 层）才能启动 DSH，首次引导多一步；无离线 DSH 兜底（在线更新依赖
  签名公钥配置，本地导入依赖用户拿到 Release 资产）。
- 兼容：已有安装不受影响（DSH 层已在 `runtime-current/dsh`）；journal / 状态机无新状态；`runtime.prepare` 操作名
  消失（其 UI 入口一并移除，属有意的行为变更，测试同步更新）。
- 未做：导入 DSH 层的降级确认（当前与在线更新一致：不降级）；DSH 层导入的进度条（层内两遍读取无字节级进度）。

## 真机验收

2026-11-17 在 Honor PTP-AN10 的 Android 17 arm64 上验证了 Release APK 覆盖安装、从 `0.2.1-rc.5` 导入 `0.2.2-rc.3` 并启动。回环未认证 `GET /` 返回 HTTP 401。Release APK 解包检查通过，详见[验收记录](../../migration/parity/2026-11-17-PTP-AN10-A17-dsh-import.md)。

真机待测项包括首次双包导入、同版或旧版保留、改名包拒绝、DSH 运行时拒绝导入、健康检查失败及导入中途 `am force-stop` 后恢复。
