# ADR-0019：去掉 `dshbox` 命名（宿主 / guest 路径、资产、垫片）并做数据迁移

- 状态：已接受（2026-11-15；项目负责人：「名称也要改，做数据迁移」，新名称选 `dshmobile`）
- 关联：[ADR-0018](0018-remove-dshbox.md)（移除 DSHBox 依赖）、ADR-0020（自建运行层，后续）、[模拟器证据](../../migration/parity/2026-11-15-emulator-A17-x86_64-adr0019-rename.md)、
  AGENTS.md 规则 3（改变已锁定行为须改向量并说明）/ 规则 4（品牌）/ 规则 11（安全不得削弱）

## 背景

ADR-0018 删除了 DSHBox 源码依赖，但为了让旧数据原样可用，App 仍沿用 DSHBox 的目录与文件名：

| 用途 | 旧（DSHBox） |
|---|---|
| 宿主链接垫片目录 / guest 挂载点 | `files/dshbox` → `/opt/dshbox` |
| guest 工具链缓存挂载点（宿主 `files/guest-cache`） | `/opt/dshbox-cache`（含 `pnpm-store`、`corepack`、`dshmobile/`） |
| 工作区内 App 管理的文件（`dsh` / `pnpm` 包装、DSH 覆盖层） | `user-data/.dsh/dshbox` = guest `/root/projects/.dsh/dshbox` |
| 层哨兵 / DSH 层版本记录 | `runtime-current/<layer>/.dshbox/layer-<layer>.sha256`、`runtime-current/dsh/.dshbox/version`（含 `previous/`） |
| APK 资产与 LD_PRELOAD 垫片 | `assets/dshbox/{link-shim.mjs, libdshbox-link.so}`，停用开关 `DSHBOX_LINK_SHIM_DISABLE` |

## 决策

1. **新名称 `dshmobile`**：`files/dshmobile` → `/opt/dshmobile`；`/opt/dshmobile-cache`；`.dsh/dshmobile`；层内 `.dshmobile/`；
   `assets/dshmobile/{link-shim.mjs, libdshmobile-link.so}`；`DSHMOBILE_LINK_SHIM_DISABLE`。
   - `link-shim.mjs` 不含该名称，字节不变（仍钉 DSHBox 哈希）。
   - `libdshmobile-link.so` 由 `native/link-shim/libdshmobile-link.c`（DSHBox 源码改名 + 改环境变量名，GPL-3.0 声明保留）重新构建，
     新哈希钉在测试与 `src/DshMobile.Android/Assets/README.md`。
   - 不改：运行层 tarball 自带的 `.dshbox/env.d/*.sh`（层内容，由 `runtime-profile.json` 的 `env_file` 指定，按数据读取；ADR-0020 的自建层改为
     `.dshmobile/env.d`）；指向上游源码的来源注释（规则 4 要求保留）；`/opt/dshapp/runtime`（不是 `dshbox`）。
2. **一次性数据迁移 `DshboxNameMigration`**（`DshMobile.Runtime.Migration`），在协调器每个进程第一次同步状态 / 供给 guest 资产之前运行，
   幂等（每步都先检查新旧是否存在），任何一步失败都记入报告、写日志并继续其余步骤（不伪造成功；失败的一步下次启动再试）：
   1. `user-data/.dsh/dshbox` → `user-data/.dsh/dshmobile`：新目录不存在时整体改名；两者都存在时把新目录里没有的条目逐个移过去，
      旧目录变空才删除（**绝不覆盖、绝不删除用户文件**）。
   2. 层哨兵：`runtime-current` 与 `runtime-current/previous` 下每个层的 `.dshbox/layer-*.sha256` 与 `.dshbox/version` 移到
      `.dshmobile/`；`.dshbox/` 里别的内容（`env.d`）原样留下，变空才删除。
   3. 文本里的旧 guest 路径：工作区 `.dsh` 下的 `node_modules/.modules.yaml`（pnpm 记录 `storeDir`，不一致会 `ERR_PNPM_UNEXPECTED_STORE`）
      和 `.dsh/dshmobile` 下的 `*.yml` / `*.yaml` / `*.json`（≤ 1 MiB）中 `/opt/dshbox-cache` → `/opt/dshmobile-cache`、`/opt/dshbox` →
      `/opt/dshmobile`、`/root/projects/.dsh/dshbox` → `/root/projects/.dsh/dshmobile`；只改含旧路径的文件，写临时文件后改名。
   4. 删除 `files/dshbox`（只含 App 写入的垫片，供给器会在 `files/dshmobile` 重写）。
   - 完成且无失败时写标记 `files/dshmobile-runtime/names-migrated-v1`，之后不再扫描。
   - 迁移只在宿主未持有 guest 进程时运行（冷启动时没有进程；协调器的操作锁保证与安装 / 回滚 / 启动互斥）。
3. **向量与钉值**：`tests/TestVectors` 中涉及上述路径的期望值（`dsh-layer`、`runtime-installed`、`install-scenarios`、`terminal-command`、
   `proot-env`、`device-proot-cmdlines`、`ui-tools-registration` 等）与 `UpstreamPinnedValuesTests` 中对应的 DSHBox 字面量按本 ADR 改为新名称，
   并注明「DSHBox 原值为 …（ADR-0019 改名）」。其余向量不变。

## 不变 / 不做

- 安全模型不变：审批、无障碍门禁、SSH 主机密钥固定、不可导出私钥；guest 命令仍 argv + 逐 token 引号。
- 不改 DSH / AgentLoop；DSH 的 `DSH_HOME`（`/root/projects/.dsh`）、配置与会话位置不变。
- SharedPreferences 文件名、Keystore 别名（`dshmobile-ssh-ecdsa-p256`）本来就不含 `dshbox`，不动。

## 后果

- 新装与升级后的设备上不再出现 `dshbox` 目录名（运行层自带的 `.dshbox/env.d` 待 ADR-0020 的自建层替换后消失）。
- 迁移代码是仓库里唯一仍然知道旧名称的地方（`DshboxNameMigration.Legacy*` 常量），并由单测覆盖：全新安装、旧布局、新旧并存、
  部分失败后重试、`.modules.yaml` 改写、哨兵搬移与 `env.d` 保留。
- 风险：插件安装产物里若有我们没扫描到的绝对旧路径，会在下次 `dsh plugin` 时暴露为失败；可重装插件解决。真机验收同 ADR-0018 决策 4（跳过），
  以单测 + 模拟器冒烟（构造旧布局 → 冷启动 → 检查迁移结果）为准。
