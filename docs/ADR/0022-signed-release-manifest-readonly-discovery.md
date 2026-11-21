# ADR-0022：签名发布清单（Ed25519）与只读版本发现

- 状态：提议（2026-11-16；2026-11-19 钉入首把发布公钥 `ed25519:705aea09994abd46`，preview 通道可签发；stable 仍需真机验收证据与人工审核）
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
7. **没有可信钥匙就什么都不提供**：`ReleaseTrust.BuiltIn` 为空时（尚未钉钥匙，或文件损坏触发 fail-closed），发现直接返回 `UPDATE_NO_TRUSTED_KEYS`，连网络都不访问——不伪造“已是最新”。2026-11-19 已钉入首把钥匙，正常路径不再走该分支，但这条故障安全路径保留。

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
- **发布**：`.github/workflows/release-publish.yml`——规格 `release/<通道>.json` 经 PR 审阅；签名只在受环境约束的那个步骤中可见
  （`preview` → `release-preview`，仅 `main` 无必审；`stable` → `release-signing`，必需审批人 + 仅 `main`，见 ADR-0029）；发布后无密钥的 job 匿名读回并核对签名与每个制品；`stable` 须附写明每个版本号的真机验收记录。
- 清单有效期：工作流默认 30 天、上限 90 天（`expires_days`）；到期前重新运行工作流即重签。

## 修订（2026-11-16，vNext PR-04：下载）

- **只下载签名清单里的制品**：`VerifiedRelease` 只能由 `ReleaseManifestVerifier.Verify` 生成（internal 构造），`ArtifactDownloader`
  只接受它与其中的一项 `ReleaseArtifact`（逐字段相等）；名称再次校验（64 位十六进制摘要作目录、纯文件名作文件名），URL 必须 https。
  插件、Agent、UI 都无法让它去下载任意地址。
- **存放**：`files/updates/partial/<sha256>.part`（+ 续传日志）与 `files/updates/verified/<sha256>/<archive>`（+ 来源记录）；
  不在 `runtime-current` 或 `user-data` 下。已验证文件每次取用前重新计算哈希。缓存上限默认 2 GiB，按最近使用淘汰。
- **续传**：`Range` + `If-Range`（ETag，否则 Last-Modified）；只接受起点恰为已有字节数、总长等于清单的 206；200（忽略 Range 或实体已变）
  从 0 重下；超出清单大小立即失败。**完成后对整个文件重新做 SHA-256 与大小校验**，不信任任何已续传的前缀；不符即删除。
- **空间**：下载前检查剩余所需字节 + 64 MiB 余量；安装所需空间由 PR-05 的 preflight 另行检查。
- 错误码：`DOWNLOAD_NOT_OFFERED / INSUFFICIENT_SPACE / CACHE_FULL / HTTP_FAILED / INSECURE_REDIRECT / INCOMPLETE / SIZE_MISMATCH /
  SHA256_MISMATCH / WRITE_FAILED / CANCELLED`。下载器不安装、不切换任何东西（PR-05），尚未接入 UI（PR-06）。

## 修订（2026-11-21：github.com 边缘不可达时的 api.github.com 回退）

- **问题（真机复现）**：Honor PTP-AN10 / Android 17 的移动网络上，`github.com` 解析到 `20.247.166.64` 并**超时**
  （25 s 无字节），而 `api.github.com`（0.7 s）与 `objects.githubusercontent.com`（0.9 s）都正常。旧实现只用
  `https://github.com/<repo>/releases/download/<tag>/<asset>`，于是「检查更新」30 s 超时并显示
  `EXCEPTION: TaskCanceledException`。
- **决策**：新增 `GitHubAssetResolver`（`DshMobile.Runtime.Updates`）。清单与制品下载都先对规范 URL 发一次
  **6 s 预算**的请求；失败（超时 / 传输错误）时把该 URL 拆成 `owner/repo/tag/asset`，用
  `GET https://api.github.com/repos/<owner>/<repo>/releases/tags/<tag>`（`Accept: application/vnd.github+json`）
  找到同名制品的 `assets[].url`，再以 `Accept: application/octet-stream` 下载（API 会 302 到同一 CDN）。
- **安全边界不变**：回退只改「怎么取字节」。URL 仍必须 https、host 必须是 `api.github.com`（解析结果做了 host 校验），
  清单仍要验签、制品仍要 SHA-256 与大小校验；不接受清单/插件/Agent 提供的任意地址。
- **不隐藏失败**：两条路都失败时返回稳定的 `MANIFEST_FETCH_FAILED` 并说明「github.com 超时且 api.github.com 回退未解析」，
  不再以裸 `EXCEPTION` 形式冒到首页。
- **续传**：回退只用于**新下载**（`have == 0`）；已有 `.part` 的续传保持对原 URL 的 `Range`/`If-Range`，避免跨 URL 拼接。

## 后果

- 正面：在线更新的第一道门（来源真实性）在任何下载之前就位；错误码稳定、可在 UI 原样显示；内容规则把发布流水线的错误挡在安装器之外。
- 代价：发布流程多一步签名；私钥丢失需要发新版 App 换钥匙（因此支持多钥匙轮换）；清单有有效期，发布方需按期重签（建议 ≤ 30 天）。
- 不变：离线导入照旧只校验固定 SHA-256；`runtime-assets.lock` / `runtime-build.lock` 不受影响。

## 未决（需负责人）

1. ~~生成发布密钥~~：已完成（2026-11-19）。`node tooling/release-manifest-sign.mjs keygen <仓库外的私密目录>` 生成；私钥只放进发布环境（`release-preview` / `release-signing`，见 ADR-0029）
   的 secret `DSHM_RELEASE_SIGNING_KEY`（离线备份，不进仓库）；公钥（base64）以单独 commit 加入 `release-trusted-keys.txt`，keyId 登记于下。
2. ~~确定公开发布位置~~：已定（本仓库，见「修订」）。
3. ~~清单有效期~~：默认 30 天、上限 90 天；重签节奏 = 每次发布或到期前。

已信任的 keyId：`ed25519:705aea09994abd46`（2026-11-19；私钥仅存在于 `release-preview` 与 `release-signing` 两个环境的 `DSHM_RELEASE_SIGNING_KEY`，轮换时两个环境同时换、新旧钥匙同签一段再撤旧）
