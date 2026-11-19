# ADR-0029：GitHub 通道自动发布、Runtime 层在线事务安装与首页在线初始化

- 状态：已接受（2026-11-19）
- 关联：[ADR-0020](0020-self-built-runtime-layers.md)（运行层自建与独立 Release）；[ADR-0022](0022-signed-release-manifest-readonly-discovery.md)
  （Ed25519 签名清单、只读发现与安全下载器）；[ADR-0024](0024-online-dsh-layer-update-transaction.md)（DSH 层在线更新事务与界面）；
  [ADR-0027](0027-remove-bundled-dsh-import-instead.md)（移除 APK 内置 DSH 层）；AGENTS.md 规则 5（不伪造成功）、9（真机证据）、10（版本锁定）、11（安全不削弱）

## 背景

`runtime-layers.yml` 与 `upstream-update-proposals.yml` 已实现 Runtime 包（`base` / `node` / `android-side`）与 DSH 层的定时检测、
双重可复现构建、不可变 GitHub Release 发布以及 `release/preview.json` 的对账 PR，但设备端仍高度依赖手动下载与 SAF 文件选择器导入，原因有四：

1. **通道清单未自动发布**：`release-publish.yml` 仅支持手动 `workflow_dispatch`，`release/preview.json` 合入 `main` 后不会自动更新
   `channel-preview` 的签名清单，且 30 天有效期缺少定时续签；
2. **`Runtime` 层不支持在线安装**：`UpdatePlanner` 仅支持在线安装 `dsh` 层，对 `runtime` 层直接返回 `UPDATE_KIND_NOT_SUPPORTED`；
   `LayerInstaller` 缺少类似 `DshLayerInstaller` 的 `probing`（保留旧层 aside 直至健康探测通过）阶段；当新版 DSH 要求新版 Node
   （如 `requiresNode: "26.x"`）时，无法在一次事务中联合升级 Runtime 与 DSH；
3. **首次安装无在线入口**：全新安装 APK（`NotInstalled`）或仅缺 DSH 层时，首页状态卡只提供本地文件选择器导入按钮；
4. **缺少主动感知与提醒**：App 启动或回到前台时不会自动检查通道清单，用户必须手动进入「更多 → 运行环境 → 检查更新」才能获知新版本。

## 决策

1. **GitHub 侧 `preview` 通道自动签发与定期续签**：
   - `.github/workflows/release-publish.yml` 新增 `push`（`main` 分支上 `release/preview.json` 或
     `src/DshMobile.Runtime/Updates/release-trusted-keys.txt` 变更）与每周一定时 `schedule` 触发，默认针对 `preview` 通道签发并上传
     `release-manifest.json` + `.sig`，随后由无密钥的 `verify` 作业匿名回读校验；
   - 若仓库尚未配置可信公钥或 `DSHM_RELEASE_SIGNING_KEY`，门禁步骤如实失败，绝不跳过验签或伪造成功；
   - `stable` 通道保持仅 `workflow_dispatch` 手动触发，强制校验 `migration/parity/*.md` 真机验收记录并经受保护环境人工审批。
2. **清单 `providesNode` 元数据与「Runtime + DSH 组合」目录评估**：
   - `release/<channel>.json` 与 `ReleaseArtifact` 为 `kind == "runtime"` 新增可选字段 `providesNode`（如 `"26.13.1"`，由
     `tooling/update-upstream.mjs` 在 `assets` 对账时从 `runtime-build.lock` 的 `NODE_VERSION` 自动填入，`tooling/release-manifest-build.mjs`
     与 `ReleaseManifestParser` 双向严格校验）；
   - `ReleaseCatalog.Evaluate` 先评估 `runtime` 行的最新可用候选 `runtimeUpdate`；评估 `dsh` 行时，若某候选的 `requiresNode`
     不匹配当前已装 `installed.Node`，但匹配 `runtimeUpdate.ProvidesNode`，则视为**随本次 Runtime 更新可满足**（不标记 `BlockedBy`）；
   - `UpdatePlanner.Plan` 返回 `UpdatePlan(ReleaseArtifact? Runtime, ReleaseArtifact? Dsh, IReadOnlyList<string> Notes)`，
     支持 `Runtime` 单项、`Dsh` 单项、以及 `Runtime + Dsh` 联合在线安装（含首次未安装时的全量在线初始化）。
3. **`LayerInstaller` 保留旧层的健康检查窗口（`probing` 阶段）**：
   - `LayerInstaller` 的换层日志新增 `phase=probing`：`InstallAsync(..., holdForProbe: true)` 在新层与 `runtime-profile.json`
     就位后保留 `dshmobile-body-old` aside 目录不立即退役；
   - `CommitProbed()` 将日志推至 `placed` 并退役旧 body；`AbortProbed()`（或任何非持有探测进程调用的 `Recover(keepProbing: false)`，
     如探测中途进程被杀后的下次启动同步）删除新 body 并将 `dshmobile-body-old` 移回原位；
   - `RuntimeCoordinator` 新增 `InstallRuntimeUpdateAsync`（换层前重算整文件 SHA-256、以 `holdForProbe: true` 安装、执行装后树核对
     `LayerExtractShadowCheck` 与 `RuntimeInstalled` 检查，核对不过立即 `AbortProbed()`）与 `FinishRuntimeUpdateAsync`。
4. **组合在线更新事务（`UpdateCoordinator.InstallPlanAsync`）**：
   - 顺序：
     1. **DOWNLOADING**（唯一可取消阶段）：按计划顺序下载 `Runtime` 和/或 `Dsh` 制品至 `files/updates/verified/` 并完成整文件 SHA-256 校验；
     2. **STOPPING**：若 DSH 正在运行，仅在 `stopConfirmed == true` 时停止宿主，否则拒绝；
     3. **SWAPPING**：按计划依次以 `holdForProbe: true` 换入 `Runtime` 和/或 `Dsh`（若换入 `Dsh` 失败且 `Runtime` 已处于 `probing`，
        立即回滚 `Runtime`）；
     4. **PROBING**：当安装后存在 DSH 层时，启动一次 `DshProfile.Web`（验证 READY + HTTP 401 + 运行版本等于目标 DSH 版本）再停止；
        当首次仅安装 Runtime 而尚无 DSH 层时，以 `InstallRuntimeUpdateAsync` 的装后树核对与 `RuntimeInstalled` 为准；
     5. **COMMITTED / ROLLING_BACK**：健康探测通过后依次提交 `Runtime` 与 `Dsh`；探测失败则逆序回滚（`Dsh` → `Runtime`），恢复至更新前状态。
   - `InstallDshAsync` 作为调用 `InstallPlanAsync` 的兼容入口保留。
5. **首页“零手动导入”在线初始化与主动更新提醒**：
   - `HomeAction` 新增 `OnlineInstall`：在 `RuntimeState.NotInstalled` 或 `RuntimeState.Ready`（`DshVersion == null`）时作为主按钮
     （`Primary`），原有的本地文件导入 `ImportRuntime` / `ImportDsh` 作为次按钮（`Secondary`）保留为离线兜底；
   - 点击首页在线安装或运行环境页在线安装时，若尚未检查或通道已切换则先执行 `CheckAsync`，弹窗列出待安装的 Runtime / DSH 版本、大小与
     SHA-256 前缀，经用户确认后执行 `InstallPlanAsync`；
   - `UiPrefs` 新增自动检查开关 `AutoCheckUpdates`（默认开启）与 6 小时节流时间戳；`MainActivity.OnStart` 在满足节流条件且无运行中操作时，
     后台静默执行只读清单检查；若已安装环境且发现可用更新，在首页状态卡下方展示更新提示卡供用户一键确认升级。

## 后果

- 正面：从 GitHub 定时构建到手机端感知、下载、事务换层、健康探测与自动回滚形成完整闭环；全新安装 APK 可直接在首页一键在线初始化；
  跨 Node 大版本的 Runtime + DSH 升级可在同一事务内安全完成。
- 代价：联合升级 Runtime + DSH 时，`runtime-current` 下会同时暂存 `dshmobile-body-old` 与 `dshmobile-dsh-old` 直至健康探测结束，
  对可用磁盘空间要求更高（下载器已有剩余空间检查，换层失败可完整回退）。
- 不变：离线 SAF 文件导入完整保留；Ed25519 验签与防重放底线不削弱；后台绝不静默中断正在运行的 DSH 会话。
