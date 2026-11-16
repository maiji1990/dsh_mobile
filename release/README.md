# 发布通道规格（release/）

`release-publish.yml`（ADR-0022，vNext PR-02）发布某个通道时读取 `release/<channel>.json`：列出要放进该通道签名清单的制品。
规格像代码一样经 PR 审阅；发布时制品的 SHA-256 / 大小从真实文件计算，URL 固定为本仓库版本化 Release 的下载地址。

```json
{
  "repository": "maiji1990/dsh_mobile",
  "artifacts": [
    { "kind": "runtime", "version": "0.2.0", "abi": "arm64", "file": "dshmobile-runtime-debian-arm64-0.2.0.zip",
      "releaseTag": "runtime-0.2.0", "minAppVersion": "0.1.0" },
    { "kind": "dsh", "version": "0.2.1-rc.5", "abi": "arm64", "file": "0.2.1-rc.5.tar.zst", "releaseTag": "dsh-0.2.1-rc.5",
      "requiresNode": "24.x", "sourceRepo": "deepseek-ai/deepseek-harness", "sourceTag": "dsh-v0.2.1-rc.5",
      "sourceCommit": "<dsh-0.2.1-rc.5 Release 的 provenance.json 里的 sourceCommit>" }
  ]
}
```

- `kind`：`runtime` / `node` / `dsh` / `app`；`dsh` 必须钉 `sourceRepo` / `sourceTag` / `sourceCommit`（完整 40 或 64 位）。
- `file` 必须是 `releaseTag` 那个 Release 里已有的资产：`runtime-layers.yml` 经验证后自动或手动发布的 `runtime-<BUNDLE_VERSION>`（bundle zip）或
  `dsh-<DSH_VERSION>`（`<版本>.tar.zst`，`provenance.json` 里有 `sourceCommit`）。
- 规则与 App 的 `ReleaseManifestParser` 一致（`tooling/release-manifest-build.mjs`，测试互相钉住）。

**目前没有任何通道规格**：发布通道尚未配置，且还没有发布公钥
（`src/DshMobile.Runtime/Updates/release-trusted-keys.txt` 为空）；在两者就绪前工作流会在「Gate」一步失败，而不是发布 App 无法验证的清单。

`stable` 只能在真机验收后发布：`acceptance_evidence` 必须指向 `migration/parity/` 下的验收记录，且记录里出现每个制品的版本号。

## CI 构建与独立发布

- PR：`runtime-layers.yml` 只构建 / 验证，不写 Releases；两个版本各构建两次，逐字节校验失败即阻断发布。
- main：运行层相关路径发生变化时，自动构建并验证两个产品；成功后创建缺失的 `runtime-<BUNDLE_VERSION>` 与 `dsh-<DSH_VERSION>` 预发行版。已经存在的标签不覆盖：新产物与已发布的主包逐字节一致则跳过，不一致则要求先在 `runtime-build.lock` 升级版本。
- 手动：在 Actions → runtime-layers → Run workflow 勾选 `publish_runtime` 或 `publish_dsh`，可单独发布一个产品；若标签已存在，手动发布仍拒绝覆盖。
- `android-dotnet.yml` 在每次成功构建后上传 `dshmobile-apk-preview` Artifacts（保留 30 天）；它不是已完成正式签名/运行环境部署/真机验收的应用 Release。
- 正式 `channel-preview` / `channel-stable` 签名清单仍由 `release-publish.yml` 单独人工触发，要求仓库配置可信公钥与受保护的 `release-signing` 私钥；stable 额外要求真机验收。
- Android APK 与 Runtime、DSH 都是不同的制品，不能只凭构建成功就声称最终 APK 已包含最新两种运行层或已验证安装。应在设备上分别验证下载、导入、更新及启动。

首次合并这份 CI 修改（工作流文件在路径过滤范围内）会触发缺失版本的自动发布；请在 Actions 中检查结果，并先确认运行环境的真实制品，再填写 `release/preview.json` 与配置签名。

## 自动版本升级

详见 [自动升级与发布完整流程](../docs/RELEASE_AUTOMATION.md)。夜间 `upstream-update-proposals.yml` 分别为 Node/Runtime、DSH 和 Release Assets 提出独立 PR；通过 CI、审核合并后发布不可变 Runtime/DSH Release。运行环境签名通道仍需配置公钥和受保护的签名私钥。
