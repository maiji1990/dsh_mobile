# DSH Mobile 自动升级与发布完整流程

## 设计

Runtime、DSH、APK 分别版本化；所有下载都使用不可变的版本/ SHA-256，不在正式构建期间使用 floating latest。自动检测只创建 PR，PR 必须通过 ARM64 双重构建和 App 测试后由负责人审核合并。Stable 永远需要人工验收和受保护签名环境审批。

## 时序

1. 每日 03:18 UTC（或手动）运行 upstream-update-proposals 工作流。runtime、dsh、assets 三个任务互相独立，生成三个独立审核 PR。
2. Runtime 候选：读取 Node 官方 index.json 的配置主版本最新补丁，从官方 SHASUMS256.txt 取 arm64 包校验值；更新 runtime-build.lock 的 NODE_VERSION、NODE_SHA256，递增 BUNDLE_VERSION。策略在 runtime-bundle/upstream-policy.json，当前目标 Node 26；Node 大版本升级需验证 ABI。Debian snapshot、NDK、PRoot 由另一个专项 PR 升级，不在夜间自动更换。
3. DSH 候选：从官方 deepseek-ai/deepseek-harness 的 dsh-v 标签选最新，并验证该版本已发布至 npm；更新 package.json、package-lock.json（--package-lock-only --ignore-scripts）、DSH_VERSION 和两个锁文件 SHA256；登记上游 tag commit。npm latest 不一定等于 GitHub 最新 tag。
4. 两种升级 PR 的检查：Android 测试与 APK 构建，Runtime/DSH 双重 ARM64 构建逐字节比对，App 实际导入/安装和 chroot DSH profile smoke。PR 中不能发布 GitHub Release。
5. 合并 main 后运行 runtime-layers，再验证成功才自动创建 runtime-<版本> 与 dsh-<版本> 不可变 GitHub 预发行版。已存在的版本不可覆盖。变化但版本未增加则报错。
6. assets 任务只在两种 Release 均存在且带 SHA256 digest 时生成独立 PR：runtime-assets.lock 钉本仓库真实下载地址及哈希；release/preview.json 加入真实 DSH provenance.sourceCommit。该 PR 审核合并后 Android CI 会执行 fetch-runtime.ps1，将 DSH 与 android-side 打入 APK，base/node 保持独立下载。
7. 预览通道：只有配置了 Ed25519 公钥、环境签名私钥以后，才能手动运行 release-publish.yml 选择 preview。其流程包括匿名下载 Release、计算真实摘要、签署 manifest、匿名回读验签。不能未经配置直接声称 OTA 生效。
8. 稳定通道：真机验证导入、启动、WebUI、终端、MCP、插件、在线更新、回滚等；新增 migration/parity/ 验收记录（包含两个制品版本）；经 PR 更新 release/stable.json；手动运行 release-publish.yml 的 stable + acceptance_evidence，在受保护环境人工批准后签署。

## 一次性 GitHub 设置（必须由仓库负责人完成）

### 自动 PR 身份

Settings → Secrets and variables → Actions 新增 UPDATE_PR_TOKEN，推荐 GitHub App 短期令牌，或仅授予本仓库 Contents:write、Pull requests:write 的 fine-grained PAT。机器人 PR 仍需正常审核。默认 GITHUB_TOKEN 创建的 PR 通常不会触发 pull_request 工作流，所以不能用它替代。没有 secret 时夜间更新工作流明确失败而不是伪造成功。

### 发布签名

本机仓库外安全目录运行：

    node tooling/release-manifest-sign.mjs keygen <outside-repo-dir>

只把输出的 raw Ed25519 publicKey（base64）通过 PR 写入 src/DshMobile.Runtime/Updates/release-trusted-keys.txt，随后构建并安装包含新公钥的 Android APK。
在 Settings → Environments 建立 release-signing，添加 required reviewers、环境 Secret DSHM_RELEASE_SIGNING_KEY（完整私钥 PEM）。不要将私钥放进代码、日志或 Artifact。未设置公钥的 App 如实报 UPDATE_NO_TRUSTED_KEYS。将 Preview / Stable 都当成需要验证的签名清单，不要跳过验签。

### Workflow 权限

runtime-layers 的 publish job 使用 contents:write；自动 PR 的 UPDATE_PR_TOKEN 具有最小写权限但不得访问签名私钥。主分支设置要求 android-dotnet / runtime-layers checks 与代码审核。请定期轮换 PAT/审阅自动化依赖。

## 操作

- Actions → upstream-update-proposals → Run workflow → runtime / dsh / assets / all。
- 本地检查：node tooling/update-upstream.mjs --component runtime --check
- 本地检查：node tooling/update-upstream.mjs --component dsh --check
- 本地检查：node tooling/update-upstream.mjs --component assets --check
- PR 检查失败时勿合并，排查 npm lock、provenance、Node ABI、Debian snapshot、重复构建 SHA、DSH profiles 兼容。
- Release 存在但 App 仍旧：先确认 assets 更新 PR 已合并，再检查 Android CI 的 Stage pinned Runtime and DSH assets 步骤；在线更新仍依赖单独的签名通道。
- 如果 Node 26 未通过真机兼容性验证，保持 Node 24 策略直至确认。不要为追求新版本绕过测试门禁。
- Android CI 产物只是 APK preview；正式可安装 APK 的生产签名、versionCode 递增及升级验收是独立任务，本自动化不生成私钥或伪造真机通过。

## 范围

此阶段实现最新版本检测 → 可审阅 PR → ARM64 验证 → 独立 GitHub Release → Release digest 同步 → 预览规格生成。真正的 signed channel manifest 发布受环境配置和人为审核约束，Stable 完全人工审核，设备端下载与安全更新继续遵循当前 C# 实现的安全策略。
