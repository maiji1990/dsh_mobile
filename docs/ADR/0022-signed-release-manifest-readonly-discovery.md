# ADR-0022：签名发布清单（Ed25519）与只读版本发现

- 状态：提议（2026-11-16；实现已合入代码但**未接 UI、未内置公钥**，生效需负责人生成发布密钥并在单独 PR 中钉入公钥，见「未决」）
- 关联：vNext 方案 P0-03 / 第 4 节「统一版本清单与手机在线安装」；[ADR-0020](0020-self-built-runtime-layers.md)（运行层自建、Release 待发布）；
  [SECURITY.md](../SECURITY.md) M2「Runtime 层签名清单校验」；AGENTS.md 规则 5（不伪造成功）、规则 10（版本锁定）、规则 11（安全不得削弱）

## 背景

运行层与 DSH 层目前只能离线导入，完整性靠 `runtime-assets.lock` 里的固定 SHA-256。在线更新需要 App 自己判断“这份版本清单是不是我们发布的”：
清单里的 SHA-256 只证明下载的字节与清单一致，不能证明清单本身没被发布源或中间人一起替换。源码仓库是私有的，Release Asset 不能匿名下载，
也不能把 PAT / Actions token / GitHub 凭据放进 APK。

## 决策

1. **独立签名**：发布清单 `release-manifest.json` 旁边放分离式签名信封 `release-manifest.json.sig`
   （`{"schema":1,"signatures":[{"alg":"ed25519","keyId":"ed25519:…","sig":"<base64>"}]}`）。签名算法 Ed25519（RFC 8032，无 context），
   被签消息 = `"DSHM-RELEASE-MANIFEST-V1\n"` + 清单文件的**原始字节**（域分离；不做 JSON 规范化，签什么验什么）。
   `keyId` 由公钥派生（`ed25519:` + SHA-256(公钥) 前 16 位十六进制），信封不能把签名冒名记到另一把钥匙上。可同时信任多把钥匙（轮换：新旧钥匙同签一段时间）。
2. **可信边界在 C#**：`DshMobile.Runtime.Updates.ReleaseManifestVerifier` 先验签再解析；插件、Agent、guest 代码都不参与也无法跳过。
   验证实现用已随 SSH.NET 钉住的 `BouncyCastle.Cryptography` 2.7.0（不新增依赖）；RFC 8032 测试向量 1 + Node 内置 Ed25519
   （`tooling/release-manifest-sign.mjs`）签出的夹具做互通校验。
3. **严格内容（schema 1）**：`channel` 只有 `stable` / `preview`；`generatedAt` / `expiresAt` 为带 `Z` 或偏移的 RFC 3339；每个 artifact 的
   `kind ∈ {runtime,node,dsh,app}`、`version` / `abi` / `archive`（纯文件名、限定后缀）、`url`（绝对 https、无凭据、无片段）、
   `sha256`（64 位小写十六进制）、`size`（1 B–4 GiB）；`dsh` 层必须钉 `sourceRepo` / `sourceTag` / `sourceCommit`；重复 JSON 键、
   同一 (kind, version, abi) 重复、尾逗号都拒绝（`MANIFEST_SCHEMA_INVALID`）。未知字段忽略（受签名覆盖；不兼容变更升 `schema`）。
4. **防替换 / 防重放**：清单的 `channel` 必须等于用户所选通道（`MANIFEST_CHANNEL_MISMATCH`，防止把 preview 当 stable 投递）；
   过期（`MANIFEST_EXPIRED`）、来自未来超过 10 分钟（`MANIFEST_NOT_YET_VALID`）、比本机上次接受的清单更旧（`MANIFEST_ROLLBACK`）都拒绝。
   “上次接受的 generatedAt” 由调用方持久化（下一切片接入）。
5. **只读发现**：`ReleaseCatalog.Evaluate` 按 Runtime / Node / DSH / App 四行列出本机 ABI 的候选（新到旧），按 `minAppVersion`、
   `requiresNode`（`24.x` 或精确）判断可否应用，给出“比已装新的最新可用版本”；`ReleaseDiscovery.CheckAsync` = 取两文件 → 验证 → 评估。
   **不下载制品、不安装、不切换、不写状态**；下载 / 事务安装 / 回滚是 P0-06。
6. **发布托管**：源码仓库保持私有；签名的制品与清单发布到**单独的公开位置**（推荐公开仓库 `dsh_mobile_releases` 的 Release 或 CDN），
   App 匿名 https 拉取（`HttpReleaseSource`：只 https、拒绝带凭据的 URL、跳转到非 https 即失败、每个文件 ≤ 1 MiB、不发送 Authorization）。
   不走私有仓库 + OAuth（方案中“仅个人使用”的备选不实现）。
7. **没有可信钥匙就什么都不提供**：`ReleaseTrust.BuiltIn` 当前为空，发现直接返回 `UPDATE_NO_TRUSTED_KEYS`，连网络都不访问——不伪造“已是最新”。

## 修订（2026-11-16，vNext PR-02）

- **托管改为本仓库**：负责人把源码仓库 `maiji1990/dsh_mobile` 改为公开（GitHub API 核实 `visibility: public`），决策 6 的“单独公开位置”
  因此就是本仓库的 GitHub Releases，不再另建 `dsh_mobile_releases`。通道清单固定在 release `channel-stable` / `channel-preview`
  （`ReleaseEndpoints.ManifestUrl`：`https://github.com/maiji1990/dsh_mobile/releases/download/channel-<通道>/release-manifest.json`，`.sig` 同目录），
  该 release 只放这两个文件；制品在版本化 release 中。地址只来自代码。
- **可信公钥是一份文件**：`src/DshMobile.Runtime/Updates/release-trusted-keys.txt` 嵌入 App（`ReleaseTrust.BuiltIn`），发布工作流也用它验证自己的产物；
  格式错误时 App 信任为空（失败即关闭）。
- **防重放下限落盘**（决策 4 的“由调用方持久化”）：`ReleaseAcceptanceStore`，`files/dshmobile-runtime/release-acceptance.json`，每通道只升不降；
  文件损坏 → `UPDATE_STATE_UNREADABLE`（不当作空），写失败 → `UPDATE_STATE_WRITE_FAILED`（该次检查不给出候选）。
- **App 接入（仍只读）**：`UpdateChecker`（Application）+ 开发者选项「检查更新（只读）」；不下载制品、不安装。
- **发布**：`.github/workflows/release-publish.yml`——规格 `release/<通道>.json` 经 PR 审阅；签名只在 environment `release-signing`（必需审批人）的
  一个步骤中可见；发布后无密钥的 job 匿名读回并核对签名与每个制品；`stable` 须附写明每个版本号的真机验收记录。
- 清单有效期：工作流默认 30 天、上限 90 天（`expires_days`）；到期前重新运行工作流即重签。

## 后果

- 正面：在线更新的第一道门（来源真实性）在任何下载之前就位；错误码稳定、可在 UI 原样显示；内容规则把发布流水线的错误挡在安装器之外。
- 代价：发布流程多一步签名；私钥丢失需要发新版 App 换钥匙（因此支持多钥匙轮换）；清单有有效期，发布方需按期重签（建议 ≤ 30 天）。
- 不变：离线导入照旧只校验固定 SHA-256；`runtime-assets.lock` / `runtime-build.lock` 不受影响。

## 未决（需负责人）

1. 用 `node tooling/release-manifest-sign.mjs keygen <仓库外的私密目录>` 生成发布密钥；私钥只放进 environment `release-signing` 的 secret
   `DSHM_RELEASE_SIGNING_KEY`（并离线备份），不进仓库；公钥（base64）以单独 commit 加入 `release-trusted-keys.txt`，keyId 登记于下。
2. ~~确定公开发布位置~~：已定（本仓库，见「修订」）。
3. ~~清单有效期~~：默认 30 天、上限 90 天；重签节奏 = 每次发布或到期前。

已信任的 keyId：（无）
