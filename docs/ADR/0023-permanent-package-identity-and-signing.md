# ADR-0023：正式包标识、版本号规则与发布签名

- 状态：**部分接受**（2026-11-16，vNext PR-01b）。项目负责人选定：包名 `io.github.maiji1990.dshmobile`（选项 1A）、versionCode 由版本号推导（选项 2 推荐规则）。
  **发布签名（选项 3）待定**：release keystore 须由负责人本人生成并保管，生成前 APK 仍为调试签名。
- 关联：vNext 方案 PR-01 第 4 步；[ADR-0022](0022-signed-release-manifest-readonly-discovery.md)（发布清单签名，与 APK 签名是两把不同的钥匙）；
  AGENTS.md 规则 4（品牌：不得用 DSHBox 名称 / 资产）、规则 11（不导出私钥）

## 背景

`src/DshMobile.Android/DshMobile.Android.csproj`：`ApplicationId=org.example.dshmobile`（模板占位）、`ApplicationVersion=1`（固定）、
`ApplicationDisplayVersion=0.1.0`；没有 `AndroidKeyStore` 配置，Release APK 用 .NET for Android 的调试密钥签名。

Android 以**包名 + 签名证书**识别一个应用：

- 包名一旦发布就不能改：换包名的 APK 是另一个应用，不能覆盖升级，旧数据不会被新包看到。
- 签名证书必须永远相同：换证书的同包名 APK 会被系统拒绝安装为升级（只能先卸载 → 数据全丢）。
- `versionCode`（`ApplicationVersion`）必须单调递增，否则系统拒绝降级安装；在线 App 更新（vNext PR-06，`PackageInstaller`）依赖它。

代码层面改包名的影响已核对（2026-11-16）：生产代码只在 csproj 写死包名；运行时一律读 `Context.PackageName`；Shizuku provider 的
`authorities` 是 `${applicationId}.shizuku`；Java 用户服务类在独立的 `dshmobile.relay` 包。测试与向量中的 `org.example.dshmobile` 只是样例数据，
可以不动。

**数据层面**改包名 = 新应用：`files/runtime`（运行层，数百 MB）、`user-data`（工作区与 `.dsh` 凭据 / 会话）、运行时 journal、远程主机配置与
主机指纹、Android Keystore 中的 SSH 私钥都不会转移。Keystore 私钥**不可导出**（规则 11），只能在新包中重新生成并重新登记到各远端主机。
目前没有公开发布，受影响的只有负责人自己的测试设备——**越早定越便宜**。

## 选项

### 1. ApplicationId（反向域名，需是负责人能控制的命名空间）

| 选项 | 说明 |
|---|---|
| A. `io.github.maiji1990.dshmobile`（**推荐**） | GitHub 用户名对应的 `maiji1990.github.io` 命名空间，无需另购域名；与仓库归属一致 |
| B. `<负责人自有域名反写>.dshmobile` | 若有自有域名，更适合长期品牌 |
| C. 保留 `org.example.dshmobile` | **不推荐**：`example.org` 是保留示例域名，发布到任何应用市场都可能被拒，也会与他人的模板工程冲突 |

不得包含 `dshbox`（规则 4、ADR-0019）。

### 2. versionCode 规则

推荐：`versionCode = major × 1 000 000 + minor × 10 000 + patch × 100 + build`（`build` 0–99，同一版本的重打包），由
`ApplicationDisplayVersion` 推导并在 CI 中校验单调递增；`0.1.0` → `10000`。备选：CI run number（简单，但与可读版本脱钩）。

### 3. 发布签名（APK）

- 由负责人在本地生成 release keystore（RSA 4096 或 EC P-256，有效期 ≥ 25 年），**离线备份两份**；丢失即无法再升级已安装的 App。
- CI 中只在受保护的 GitHub environment（必需审批人）里以 secret 注入（base64 keystore + 密码），仅签名 job 可见；不进仓库、日志、Actions artifact。
- 与 ADR-0022 的发布清单 Ed25519 密钥**分开**保管：一把签 APK，一把签版本清单，任一泄露不连带另一把。
- 调试签名的 APK 只用于开发；真机验收记录须写明签名类型。

## 决策

1. **`ApplicationId = io.github.maiji1990.dshmobile`**，自 2026-11-16 起永久不变。集中在 `src/DshMobile.Android/PackageIdentity.props`，
   csproj 只 import 它；`PackageIdentityTests` 钉住包名并禁止在 csproj 中重复声明。
2. **versionCode 由版本号推导**：`major × 1 000 000 + minor × 10 000 + patch × 100 + DshVersionBuild`。`ApplicationDisplayVersion` 必须为
   `major.minor.patch`（minor、patch 0–99），`DshVersionBuild` 0–99（同版本重打包，`-p:DshVersionBuild=N`）；不合规则构建报错。
   `0.1.0` → `10000`。每次构建在日志中打印 `DSH Mobile package <id> <版本> versionCode <n>`。与已发布版本比较的单调性检查在有正式发布后由
   发布工作流做（vNext PR-02 / PR-06）。
3. **发布签名：待定**。负责人生成 release keystore 后另起 PR：签名配置只读 CI secret（受保护 environment）或本地未入库文件，密码以
   `env:` 方式传入；发行配置缺 keystore 时报错，不静默改用调试签名。

## 从 `org.example.dshmobile` 迁移（仅负责人的测试设备；没有公开发布过）

新旧包名是两个应用，可以同时安装。新包首次启动是全新状态（无运行层、无工作区、无凭据）。

1. 安装新包（`io.github.maiji1990.dshmobile`），保留旧包。
2. **工作区**：旧包文件页目前只能逐个文件导出（SAF「导出」）；整目录导出未实现。调试构建可用
   `adb exec-out run-as org.example.dshmobile tar -C files -cf - user-data/<项目目录>` 先备份到电脑（**不要**带出 `user-data/.dsh`，其中含凭据）。
3. **运行层**：在新包「更多 → 运行环境 → 导入运行环境包」重新导入（不能从旧包复制）。
4. **DSH 凭据 / 会话**：在新包的 DSH 里重新登录；旧会话不迁移。
5. **SSH**：新包会生成新的 Keystore 密钥（旧私钥不可导出，规则 11）；把新公钥登记到各远端主机，并重新核对主机指纹。
6. 确认新包可用后卸载旧包（卸载即删除其全部数据）。
