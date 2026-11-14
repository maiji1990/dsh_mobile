# ADR-0014：PRoot / loader / talloc / libandroid-shmem 在本仓库从源码构建

- 状态：已接受（2026-11-14；用户在「移除 DSHBox、全部 C# 接入」的分步方案中选定第一步「Build PRoot ourselves」）。
  构建、比对与真机验收已完成（[证据](../../migration/parity/2026-11-14-PTP-AN10-A17-proot-self-build.md)），
  `DshProotSource` 默认值已改为 `self`；`dshbox` 保留为回退开关，直到 PR-10。
- 关联：[ADR-0003](0003-license-route-a-gpl3.md)（许可路线）、[ADR-0007](0007-source-ownership-csharp-first.md)
  （最小 NDK 层放在本仓库）、`docs/UPSTREAMS.md`、`docs/MIGRATION.md`

## 背景

APK 里的 `libproot.so`、`libproot-loader.so`、`libtalloc.so`、`libandroid-shmem.so` 目前由 Gradle 从 DSHBox 的
`app/src/main/jniLibs` 原样复制（`:native-bridge:exportRuntimeJars`）。这些文件**不是 DSHBox 构建的**，
而是 termux-packages 的产物：

| 文件 | 来源（由二进制本身确认） |
|---|---|
| `libproot.so` | termux/proot `5.1.107.90`，NDK r29（clang 21），`PROOT_UNBUNDLE_LOADER`，`WITH_LIBANDROID_SHMEM`；Termux 前缀编进默认值 |
| `libproot-loader.so` | 同一 PRoot 的静态 loader（termux 安装为 `libexec/proot/loader`） |
| `libtalloc.so` | talloc `2.4.3`（waf 构建，SONAME `libtalloc.so.2`，RUNPATH `/data/data/com.termux/files/usr/lib`） |
| `libandroid-shmem.so` | termux/libandroid-shmem `0.7`（API 24 → `/dev/ashmem` 路径） |

本仓库无法从源码重现它们；要移除 DSHBox 依赖（最终 PR-10），这四个文件必须先有本仓库可追溯的构建。

## 决策

1. **钉住与 DSHBox 所带二进制相同的上游版本**（`native/proot/sources.lock`：URL + SHA-256 + 归档顶层目录 + 许可证）：
   PRoot `5.1.107.90`（termux-packages proot 配方 commit `96385864`，无补丁）、talloc `2.4.3`（配方 commit `fbc04945`）、
   libandroid-shmem `0.7`。先追求等价，**升级**（termux 当前为 PRoot 5.1.107.102 / talloc 2.5.0）按规则 8 另开 PR。
2. **构建脚本** `native/proot/build.ps1`（Windows 主用）与 `build.sh`（Linux / Git Bash；两者输出逐字节相同）：
   下载或读缓存（`native/.generated/proot/src-cache`，`-Offline` / `PROOT_OFFLINE=1` 只读缓存）→ 校验 SHA-256 → 解包 →
   用锁定的 NDK `27.0.12077973` 为 `arm64-v8a` / `x86_64` 构建 → 自检 → 写 `artifacts/native/proot/<abi>/` 与 `SHA256SUMS`。
   `native/build-native.ps1` / `.sh` 末尾调用它。PRoot 目标文件列表、`build.h` 特性探测（`process_vm`、`seccomp_filter`）、
   `loader-info.c`（arm64 的 pokedata 偏移）、loader 地址（`arch.h` 的 `LOADER_ADDRESS`）全部按上游 `GNUmakefile` 的逻辑在脚本里
   重算，不手抄结果；在源码目录内编译（同上游 `make -C src`），使 `__FILE__` 保持相对路径。
3. **与 termux 构建的差异（逐条登记，均不改上游源码）**：
   - NDK r27（本仓库锁定）而非 r29；API 24（termux 的目标 API，libandroid-shmem 的代码路径取决于它）。
   - talloc 的 waf 配置步骤由手写的 `compat/talloc/config.h` 代替；只编 `talloc.c`（不链接 libreplace，bionic 已提供这些函数）；
     `-fvisibility=hidden`，导出符号与 DSHBox 版**完全相同**（66 个，不导出内部构造函数 `talloc_lib_init`）。
   - SONAME 为 `libtalloc.so` / `libandroid-shmem.so`（与 `libproot.so` 的 NEEDED 一致）；**去掉**指向 Termux 前缀的 RUNPATH；
     `libproot.so` 保持 RUNPATH `$ORIGIN`。
   - 强制包含：libandroid-shmem 用 `fcntl.h`、`sys/ioctl.h` 与 `_PATH_TMP`（代替 termux 打过补丁的 sysroot）；
     PRoot 的 `ashmem_memfd.c` 用 `string.h`（NDK r27 头文件不像 termux sysroot 那样间接引入）；
     PRoot 全体用 `compat/termux-parity.h`（把 `P_tmpdir` 设成 termux 补丁后的值）。
   - 统一加 `-fstack-protector-strong`（loader 除外，与上游相同）、16 KB 页对齐、`relro` / `now` / `noexecstack`、
     `--build-id=sha1`（loader 为 `none`，同上游）、`-s` 剥离。
   - 不构建 `loader32`：DSHBox 不带，32 位 guest 程序照旧不支持。
4. **编进二进制的默认路径保留 termux 值**（`.../libexec/proot/loader`、`loader32`、`.../usr/tmp/`、shmem 的 `ashv_key_%d`），
   以便成为逐项可比的替身。它们只是回退值：旧实现与 C# 的每条启动路径都设置 `PROOT_LOADER` 与 `PROOT_TMP_DIR`
   （`ProotCommandBuilder`、`TerminalCommandBuilder`、`tests/TestVectors/{proot-env,terminal-command}.json`）。
   改成本 App 自己的（或失败即报错的）默认值是行为变更，另行决定。
5. **构建即自检，失败不跳过**：四个文件的 LOAD 段 ≥ 16 KB 对齐；NEEDED 精确等于 DSHBox 版（proot：talloc / android-shmem / libc；
   talloc：libc；shmem：liblog / libc）；`libproot.so` 为 PIE、RUNPATH `$ORIGIN`、除 libc 外的导入全部由 talloc / shmem 提供；
   shmem 导出恰为 `exports.txt` 的 10 个；loader 为静态、入口等于 `LOADER_ADDRESS`；版本串与 termux 默认路径存在；
   二进制中不得出现构建机路径（可重现）。
6. **切换开关** `DshProotSource`（`Directory.Build.props`，`dshbox` | `self`，可 `-p:DshProotSource=self` 覆盖）：
   `self` 时 `DshMobile.NativeBinding` 从 DSHBox jniLibs 中排除这四个文件，改用 `artifacts/native/proot/<abi>/`；
   zstd-jni 仍来自 DSHBox jniLibs。真机验收（旧实现与 C# 宿主各启动沙箱 + DSH、终端、一次性 guest 命令）通过后默认值已改为 `self`；
   遇到问题可用 `-p:DshProotSource=dshbox` 回退。
   DSHBox 子模块与其 jniLibs 导出**不删除**（用户决定：所有 PR 完成后才移除 DSHBox 依赖）。
7. **许可**：PRoot GPL-2.0-or-later、talloc LGPL-3.0-or-later、libandroid-shmem BSD-3-Clause，均未修改源码。
   对应源码 = `sources.lock` 钉住的三个归档 + `native/proot/`（脚本与 compat 头文件）；发布 APK 时随附或提供这些归档。
   talloc 以共享库动态链接，满足 LGPL 的可替换要求。登记见 `docs/UPSTREAMS.md`。

## 结果（2026-11-14，桌面）

- 两个 ABI 都构建通过并通过第 5 条全部自检；连续两次构建、以及 pwsh 与 bash 两个脚本的输出 SHA-256 完全相同。
- 与 DSHBox 版逐文件比对：`libproot.so` 与 `libandroid-shmem.so` 的导出、导入（去掉符号版本）、NEEDED、路径 / 版本字符串相同；
  `libtalloc.so` 导出相同，导入更少（不含 libreplace），SONAME / RUNPATH 按第 3 条不同；
  loader 入口与段布局相同，指令字节不同（编译器版本不同）。`libproot.so` 多了 `DF_1_PIE` 标志（新版 lld 标注，二者本来都是 PIE）。
- `-p:DshProotSource=self` 构建的 APK 中，四个文件与 `artifacts/native/proot/SHA256SUMS` 逐字节一致，zstd-jni 仍为 DSHBox 版。

## 结果（2026-11-14，真机 Honor PTP-AN10 / Android 17）

- shell 用户 A/B：两套二进制跑同一组命令（版本、`-0`、退出码、绑定 + 工作目录、fork/exec 链、link2symlink、sysvipc），输出逐字节相同。
- App 内（自建二进制）：旧实现与 C# 宿主均启动到 DSH READY 并干净停止；`supervisor-observe` / `launcher-argv` /
  `terminal.launcher-argv` / `guest-exec` 影子全部 Agree；交互终端可用；C# PTY 自检 PASS；crash 缓冲区 0 条。
- 启动耗时 A/B（各 3 次）：C# 宿主 17.9–18.3 s（DSHBox）对 16.5–18.5 s（自建）；旧实现 18.6 s ×3 对 17.1–18.6 s，无差异。

## 后续（不在本 ADR 内）

- 评估 libandroid-shmem 以 API ≥ 26 构建（`ASharedMemory`，不再打开 `/dev/ashmem`）：面向新 targetSdk 的 App 直接打开
  `/dev/ashmem` 可能被拒，须先在真机上确认现状（guest 内 SysV 共享内存）再决定，属行为变更。
- 上游升级（PRoot 5.1.107.102、talloc 2.5.0）与编译默认路径改为本 App 自己的值，各自单独 PR。
