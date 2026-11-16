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
- `file` 必须是 `releaseTag` 那个 Release 里已有的资产：`runtime-layers.yml` 手动发布的 `runtime-<BUNDLE_VERSION>`（bundle zip）或
  `dsh-<DSH_VERSION>`（`<版本>.tar.zst`，`provenance.json` 里有 `sourceCommit`）。
- 规则与 App 的 `ReleaseManifestParser` 一致（`tooling/release-manifest-build.mjs`，测试互相钉住）。

**目前没有任何通道规格**：本仓库还没有发布过运行层 Release（vNext PR-03），也还没有发布公钥
（`src/DshMobile.Runtime/Updates/release-trusted-keys.txt` 为空）；在两者就绪前工作流会在「Gate」一步失败，而不是发布 App 无法验证的清单。

`stable` 只能在真机验收后发布：`acceptance_evidence` 必须指向 `migration/parity/` 下的验收记录，且记录里出现每个制品的版本号。
