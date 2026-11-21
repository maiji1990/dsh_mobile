# ADR-0030：自建 PRoot 启动路径定向优化（statx 快退、可选跳过 close 拦截、只读前缀 dentry 缓存）

- 状态：已接受（2026-11-20；项目负责人选定先独立接入 P0-2 后推进 P1）
- 关联：[ADR-0014](0014-in-repo-proot-build.md)（本仓库自建 PRoot）、[E5 性能汇总](../../artifacts/startup-experiments/E5.summary.md)、
  [2026-11-20 E6 真机报告](../../migration/parity/2026-11-20-PTP-AN10-A17-P0-startup.md)、
  [2026-11-20 T1 生产配对报告](../../migration/parity/2026-11-20-PTP-AN10-A17-T1-production-pairing.md)、
  [AGENTS.md](../../AGENTS.md) 规则 1 / 2 / 4 / 9 / 10 / 11

## 背景

在 Honor PTP-AN10 / Android 17 上完成的 10 份 E5 CPU Profile 与 E6 / T1 真机配对表明：

1. DSH Web Profile 冷启动约 16–18 s 中，约 **11.7 s（63.6%）** 为 PRoot `ptrace` 拦截放大下的同步文件系统系统调用
   （`internalModuleStat` 4.84 s、`lstat`/`realpath` 3.80 s、`fstat` 0.76 s、`open` 0.73 s、`readFileUtf8` 0.52 s、`existsSync` 0.44 s、`close` 0.37 s）。
2. P0 已在宿主侧（`DshHost.cs`，T1 实测 −836 ms）与 Node 只读 FS 垫片侧（`startup-fs-cache-shim.mjs`，E6 实测 −1794 ms，已独立接入生产）
   削减了约 2.5 s 成本，但剩余启动瓶颈仍集中在 PRoot 5.1.107.90 内部的三处高频开销：
   - **`PR_statx` 双重停顿与双重路径翻译**：`seccomp.c` 对 `PR_statx` 固定附加 `FILTER_SYSEXIT`，且 `tracee/statx.c` 的 `handle_statx_syscall()`
     在检查 syscall 返回值前无条件再次 `read_string()` + `translate_path()`。在支持原生 `statx` 的现代内核（如 Android 17 / Linux 6.x）且未启用
     `fake_id0` / `link2symlink` 扩展时，每一次 `statx()` 都在 syscall-exit 白白停顿并重复翻译一整遍路径。
   - **`PR_close` 无条件拦截**：`seccomp.c` 拦截所有 `close()` 仅用于 `auxv_fd`、fake netlink 与 bash 进程替换 `shadow_pipe_read_end`；
     DSH Node 启动期数千次普通只读模块文件关闭全部触发 ptrace 切换。
   - **`canonicalize()` 逐级宿主机 `lstat`**：`path/canon.c` 每次翻译 `/opt/dshapp/runtime/node_modules/...` 都要从 `/` 开始对 6–10 级只读祖先目录
     重复线性扫描挂载表并执行宿主机 `lstat()`，单次冷启动产生数万次冗余宿主机 syscall。

## 决策

1. **保持上游归档锁定不变，补丁可追溯维护**：
   - `native/proot/sources.lock` 继续钉住 `proot 5.1.107.90` 原始归档与 SHA-256，不修改上游归档。
   - 新增 `native/proot/patches/*.patch`（GPL-2.0-or-later，与 PRoot 同许可，登记到 `docs/UPSTREAMS.md`），由
     `native/proot/build.ps1` 与 `native/proot/build.sh` 在解包 PRoot 源码后按字典序经 `git apply` 应用；
     `NativeProotBuildContractTests` 锁定补丁目录与两份脚本同步。
2. **三项最小、低侵入补丁（默认语义安全，高收益项 opt-in 可秒级回退）**：
   - **`01-statx-fast-exit.patch`**（默认生效，语义等价）：
     - 在 `tracee/statx.c` 中：当 `!from_sigsys`（常规 syscall-exit）且返回值不为 `-ENOSYS` 且未加载 `fake_id0` / `link2symlink` 扩展时直接 `return 0`。
     - 在 `syscall/seccomp.c` 中：运行时一次性探测内核支持原生 `statx`（非 `ENOSYS`）且未加载 `fake_id0` / `link2symlink` 扩展时，
       移除 `PR_statx` 的 `FILTER_SYSEXIT` 标志（保留 syscall-enter 路径翻译，消除 syscall-exit 停顿）。
   - **`02-seccomp-optional-skip-close.patch`**（opt-in：`PROOT_NO_CLOSE_INTERCEPT=1`）：
     - 当环境变量 `PROOT_NO_CLOSE_INTERCEPT=1` 设置时，构造 seccomp 过滤表时跳过 `PR_close`，使其在内核态直接 `SECCOMP_RET_ALLOW`。
     - 普通交互 shell 与一次性 guest 命令默认不设置，完整保留 `shadow_pipe_read_end` 与 netlink 清理。
   - **`03-canon-ro-prefix-cache.patch`**（opt-in：`PROOT_RO_CACHE_PREFIXES=/opt/dshapp/runtime:/usr/local`）：
     - 当环境变量 `PROOT_RO_CACHE_PREFIXES` 非空（冒号分隔绝对路径前缀，最多 8 项）、`tracee->glue_type == 0` 且未加载修改 `HOST_PATH` 的扩展
       （`fake_id0` / `link2symlink` / `hidden_files`）时，在 `path/canon.c` 启用单 PRoot 进程生命周期内的固定 16,384 桶只读 dentry 哈希表
       （仅缓存 `DIR` / `NONDIR` / `NONEXIST`）。
     - **严格安全边界**：
       - 仅命中严格前缀匹配（`path == prefix` 或 `prefix + "/"`）且不含 `.` / `..` / `//` 的路径；绝不缓存 `/root/projects`、`/tmp`、`/proc`、`/dev`。
       - 遇到任何符号链接（`S_ISLNK`）一律**不入缓存**，始终走原版 `canonicalize()` 递归展开与逃逸检查。
       - 缓存随单次 PRoot 进程退出自然销毁，不存在跨运行层版本的脏缓存。
3. **验证先于生产默认开启**：
   - 保持 `libproot.so` 既有全部自检契约（16 KB 对齐、NEEDED 精确匹配、PIE、RUNPATH `$ORIGIN`、无构建路径泄漏、静态 loader）。
   - 在诊断构建中新增 `E7` 配对套件（`E7-0`..`E7-4`），在同一 APK、同一设备会话中逐轮交错测出三项优化的独立与叠加配对收益，
     并完成 `dsh-probe` 全 profile、`ui_*` 与文件读写回归后，再以单独切片把 `PROOT_NO_CLOSE_INTERCEPT` / `PROOT_RO_CACHE_PREFIXES`
     写入生产 `ProotCommandBuilder.Environment("dsh")` 并更新 `tests/TestVectors/proot-env.json`。
4. **生产默认只开 03，且只缓存 DIR/NONDIR（2026-11-21 决策）**：
   - `ProotCommandBuilder.Environment("dsh")` 设置 `PROOT_RO_CACHE_PREFIXES=/opt/dshapp/runtime:/usr/local`
     与 `PROOT_RO_CACHE_NONEXIST=0`；单测与 `tests/TestVectors/proot-env.json` 锁定，其他 role（guest / 终端）不设置。
   - **为什么关掉 NONEXIST**：`/usr/local` 在 guest 内仍可写（`npm i -g`、corepack），而缓存项只在 PRoot 进程内有效、
     不会因后续 `mkdir`/`open(O_CREAT)` 失效；缓存 ENOENT 会让「先探测后创建」的路径变成假失败。E7 的收益在
     `NONEXIST=0` 的生产配置下仍然成立（T0/T1 配对 −1 955 ms / −25.1%），所以安全侧不需要付收益代价。
   - **为什么 02 不默认开启**：两轮 E7 都是 8/10、单轮 CI 跨 0（合并 n=20 才显著 −1 172 ms），强度比 03 弱一个量级，
     且跳过 `close` 拦截会同时放过 `shadow_pipe_read_end` 与 netlink 清理路径；继续保留 opt-in（`PROOT_NO_CLOSE_INTERCEPT=1`）。
   - 真机证据：[2026-11-21 P1 生产配对报告](../../migration/parity/2026-11-21-PTP-AN10-A17-P1-proot-fastpath.md)。

## 不变 / 不做

- 不修改 DSH 内核、Cordis 或上游 `link-shim.mjs`（规则 1、7）。
- 不引入 root / `su`、不关闭 seccomp、不削弱权限审批或安全校验（规则 11）。
- 不缓存任何可写用户路径，也不在 PRoot 内猜测或绕过符号链接逃逸检查。

## 后果

- 每次 PRoot 构建额外执行确定性 `git apply`，`build.ps1` 与 `build.sh` 输出仍保持逐字节一致。
- 若未来某设备内核或 ROM 出现兼容异常，可通过去掉环境变量（02/03）或移除对应补丁文件回退，回退成本极低。
- 真机收益已测（2026-11-21）：E7 两轮里 `03` 均为 10/10；生产 `bridge.complete` 从 7 798 ms 降到 5 842 ms
  （相邻块配对 n=12，−1 955 ms / −25.1%，p=0.0005），且 `dsh.first_output` 同步下降，说明是 PRoot 路径翻译变快而不是宿主等待变短。
- 新增的 `PROOT_RO_CACHE_NONEXIST` 是 03 内的可选开关（默认 1，保持 E7 语义）；生产设 0。若将来要实现写入失效，
  可在不改变该默认值的前提下补类型为 DIR 的缓存失效逻辑。
