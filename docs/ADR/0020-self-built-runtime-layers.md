# ADR-0020：运行层由本仓库自行构建并发布（替代 DSHBox Release）

- 状态：已接受（2026-11-15；项目负责人：「运行层，自行构建」；托管选「CI + GitHub Release」，guest apt 源保留清华镜像）
- 关联：[ADR-0018](0018-remove-dshbox.md)「不变 / 不做」中「运行层仍取自 DSHBox Release，更换来源需另立 ADR」——即本 ADR；
  [ADR-0019](0019-rename-dshbox-paths-data-migration.md)（`dshmobile` 命名）；[ADR-0014](0014-in-repo-proot-build.md)（自建 PRoot）；
  AGENTS.md 规则 1（不改 DSH 内核）、规则 8（上游升级单独 PR）、规则 10（版本锁定）

## 背景

App 的运行层来自 DSHBox `v1.5.1` Release 的两个文件（`runtime-bundle/runtime-assets.lock` 钉 SHA-256）：

| 文件 | 内容 | DSHBox 的做法 |
|---|---|---|
| `dshapp-runtime-debian-arm64-0.1.0.zip` | `base` / `node` / `android-side` 三层 + `runtime-profile.json` | `tools/build_arm64_runtime_bundle.sh`：debootstrap trixie arm64 + 约 30 个包（含依赖约 170 个）、精简 locale / doc / man、清华镜像；官方 Node 24.19.0 linux-arm64；DSHBox jniLibs 里的 PRoot |
| `0.2.1-rc.5.tar.zst` | DSH 层：`package.json`（依赖 `@deepseek-ai/dsh`）+ `package-lock.json` + `node_modules` | 在 x86_64 上为 arm64 执行 npm 安装 |

各层内的 `.dshbox/env.d/*.sh` 与 bundle 名 `dshapp-runtime-*` 是 DSHBox 的命名。

## 决策

1. **构建脚本在本仓库**：`runtime-bundle/build-runtime.sh`（Linux；arm64 原生，x86_64 需 qemu-user binfmt），输入全部钉在
   `runtime-bundle/runtime-build.lock`：
   - **base**：`mmdebstrap --variant=minbase` trixie arm64，源为 `snapshot.debian.org` 的固定时间点（可复现），包清单与 DSHBox
     `build_base.sh` 相同；定制与 DSHBox 一致：guest `sources.list` = 清华镜像、`resolv.conf` 三个公共 DNS、只留 C / zh_CN locale、
     doc 只留 copyright、删 man、清 apt 列表与缓存。
   - **node**：nodejs.org 官方 `node-v24.19.0-linux-arm64.tar.xz`，按 lock 中的 SHA-256 校验。
   - **android-side**：本仓库 NDK 构建的 PRoot（ADR-0014 的 `artifacts/native/proot/arm64-v8a`），布局同 DSHBox（`bin/proot`、
     `libexec/proot/loader`、`lib/{libtalloc,libandroid-shmem}.so`）。App 仍优先用 APK 内的 `libproot.so`，这一层是回退。
   - **DSH 层**：`runtime-bundle/dsh/{package.json, package-lock.json}`（钉 `@deepseek-ai/dsh` 精确版本；lock 取自 DSHBox 发布物里的
     同名文件，即同一组依赖版本），在 arm64（原生或 qemu）下用 node 层的 Node 执行 `npm ci`。只安装官方包，不改 DSH（规则 1）。
   - 每层自带 `.dshmobile/env.d/<layer>.sh`（内容与 DSHBox 相同的占位符），`runtime-profile.json` 的 `env_file` 指向它；
     bundle 名改为 `dshmobile-runtime-debian-arm64`，版本 `0.2.0`。
   - 打包可复现：`tar --sort=name --mtime=@SOURCE_DATE_EPOCH --owner=0 --group=0 --numeric-owner`、`zstd -19 -T1`、zip 固定时间戳；
     `SOURCE_DATE_EPOCH` 取 snapshot 时间点。
2. **发布**：`.github/workflows/runtime-layers.yml` 手动触发（`workflow_dispatch`），x86_64 作业用 NDK 构建 PRoot，arm64（`ubuntu-24.04-arm`）
   作业构建各层并上传为工作流产物；输入 `release_tag` 非空时以该 tag 创建 GitHub Release（`runtime-<版本>`）。发布由负责人确认后执行。
3. **锁定**：发布后 `runtime-assets.lock` 改指本仓库 Release 的 URL 与 SHA-256；`fetch-runtime.ps1` 同时认 `dshmobile-runtime-*.zip`
   与旧的 `dshapp-runtime-*.zip`（用户手里的旧包仍可导入）。App 的导入入口同样两种名字都认。
4. **兼容**：已安装 DSHBox 层的设备照常运行（`env_file` 按 profile 读取）；导入新 bundle 即替换为自建层（旧的进 `previous/` 可回滚）。

## 不变 / 不做

- 不改 DSH / AgentLoop / Cordis；DSH 版本仍为 `0.2.1-rc.5`。升级 DSH 或 Node 属于上游升级，单独 PR（规则 8）。
- 离线 corepack 缓存（`toolchain-cache/corepack-pnpm-11.1.2.tar.zst`）不是运行层，仍为 ADR-0018 原样复制的 DSHBox 资产；另立切片处理。
- 安全模型不变；guest 命令构建方式不变。

## 后果

- 运行层不再依赖 DSHBox 的发布物；构建只需 Linux + mmdebstrap（或 GitHub arm64 runner）。
- 新层与 DSHBox 层不是逐字节相同（包版本取 snapshot 时间点、npm 原生模块在 arm64 上构建）；以文件清单对比与单测为准，
  guest 内功能的设备证据同 ADR-0018 决策 4（真机跳过，模拟器无法运行 arm64 guest）。

## 验证（2026-11-15，WSL2 x86_64 + qemu-user binfmt 本地构建）

- node / android-side 两次构建 SHA-256 相同（`81ea6b43…`、`190faa42…`）；**base 与 DSH 层的逐字节可复现未验证**（只构建过一次即改了
  `/dev`），留给 CI 两次运行对照。
- 与 DSHBox `v1.5.1` 层对照：node、android-side 文件清单与权限相同；DSH 层为 DSHBox 的子集（只缺 DSHBox 遗留的 koffi `linux_x64`
  构建残留）；base 包集合相同（只缺过渡包 `usr-is-merged`，安全更新版本更新），`/dev` 骨架相同，`/etc/hostname` 为 `localhost`；
  体积约 62 MB（DSHBox 为 166 MB，多出的主要是 `/var/cache` 里遗留的 apt deb）。
- 宿主集成测试 `SelfBuiltRuntimeBundleTests`：自建 bundle 经 App 自己的 `LayerInstaller`（Android 式宿主）安装，三层校验通过、
  `.dshmobile` 哨兵与 `env_file` 存在、`RuntimeInstallCheck` 判定已安装；DSH 层经 `DshLayerInstaller` 按 `SHA256SUMS` 安装，版本正确。
  没有构建产物时该测试输出 NOT RUN。
- 模拟器内导入未做：唯一入口是系统文件选择器，按负责人约束不操作系统对话框。
