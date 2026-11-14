# 2026-11-14 · Android 模拟器 · Android 17 (API 37) · 内置 DSH 层供给（`runtime.prepare`）影子与 C# 可选记录

设备：`emulator-5554`，`sdk_gphone16k_x86_64`，Android 17 / SDK 37，页大小 16384。构建：Debug（`-p:EmbedAssembliesIntoApk=true`，
清理 `obj` 后重建），`adb install -r --abi x86_64 -g`，基于 `575c3db`（maiji1990/dsh_mobile#29）+ 未提交的本切片改动。
APK 资产：`assets/dsh/0.2.1-rc.5.tar.zst`（89 MB，`.sha256` 旁路文件）+ `assets/runtime/android-side.tar.zst`。

**为什么模拟器足够：** 本切片只做文件操作（读资产、校验、zstd 解包、换层、写版本记录），不启动 guest，
因此 x86_64 模拟器上 arm64 guest 起不来不影响验证。真机只差 arm64 / 厂商存储差异，未测。

操作全部由 Claude 经 `adb shell input` 点本 App 自己的按钮 / 对话框完成；准备旧状态时只用 `run-as` 改本 App 私有目录里的
`runtime-current/dsh/.dshbox/version`（模拟“已装旧版本”）。16 KB 兼容提示只用 `KEYCODE_BACK` 关闭。

树指纹（`run-as … sh fp.sh`，对 `runtime-current/dsh`：路径列表 / 链接目标 / 文件内容 / 可执行位 各取 md5 前 12 位）：
旧实现导入时装好的基线为 `paths=a85c67987664 links=75dbf42da2ed content=9c3ee60b802d modes=376650f54995`（63,047 个文件）。

## 序列与结果（`DshMobile` 标签，UTC）

| # | 操作 | 实际 | 结论 |
|---|---|---|---|
| 1 | 短按「准备」（默认：旧实现 + 影子），层为 0.2.1-rc.5 | 旧：`…; DSH kept at 0.2.1-rc.5`；影子 `runtime.dsh-provision` **Agree**（steps equal，C# 37 ms） | 通过 |
| 2 | 版本记录改为 `0.0.1` 后短按「准备」 | 旧：`DSH 0.2.1-rc.5 installed`（19.4 s）；影子 **Agree**：`dsh layer: 68212 entries identical`（C# 干跑 12.7 s）；指纹 = 基线；无 `previous/`（继承行为：旧层被删） | 通过 |
| 3 | 版本记录改为 `0.0.1` 后长按「准备」→「C# 准备」 | `runtime.prepare: C# RuntimePreparer (legacy prepare not used)` → `ok (…; DSH 0.2.1-rc.5 installed)`（32.2 s）；**指纹 = 基线**（C# 装出的树与旧实现逐字节一致）；版本记录 `0.2.1-rc.5` | 通过 |
| 4 | 紧接短按「准备」 | 旧实现认可 C# 装的层：`DSH kept at 0.2.1-rc.5`；影子 **Agree** | 通过 |
| 5 | 长按「启动 DSH」→「旧实现（回退）」（沙箱 Running，DSH 在模拟器上 `DSH_NOT_READY`），版本记录改为 `0.0.1`，长按「准备」→「C# 准备」 | `refused, the bundled DSH layer would be replaced while processes are alive` → `RUNTIME_INVALID_TRANSITION`；层未动 | 通过 |
| 6 | 「停止」，再短按「准备」恢复 | `stop: ok (Ready)`；无前台服务、无 `libproot` 进程；旧实现重新安装，影子 **Agree**（68,212 项一致）；指纹 = 基线 | 通过 |

影子报告原文：[2026-11-14-emulator-A17-x86_64-runtime.dsh-provision.jsonl](2026-11-14-emulator-A17-x86_64-runtime.dsh-provision.jsonl)（4 行，全部 `Agree`）。

## 过程中遇到的问题

- 首次安装的构建在 `MainActivity.OnCreate` 空引用崩溃：增量构建留下的 `R.txt`（`btn_info = 0x7f08004c`）与打包布局
  （`0x7f08006f`）不一致，`FindViewById` 返回 null。删除 `obj/Debug` 后重建即一致，与本切片代码无关；记录在此以便复现时识别。

## 未覆盖

- 真机（arm64、厂商存储）；
- 旧导入内部的 `provisionBundledDsh`（导入后由旧桥自行完成）未加影子，只覆盖「准备」按钮与 C# 导入后的准备；
- C# 安装后，旧状态行显示的 DSH 版本要到 App 重启才刷新（旧实现缓存 `dshVersion`）；
- APK 内置完整运行时（`runtime-profile.json` + `base.tar.*` / `.dshb`）的安装未移植，遇到即 `NOT_IMPLEMENTED`（本 APK 不含）。

## 单测 / 向量

- `tests/TestVectors/dsh-layer.json`（14 例）、`runtime-installed.json`（10 例）：旧 DSHBox `LegacyDshLayerParityTest`（2/2）与 C# `DshLayerParityTests`（17/17）双绿；
- Core 65、Runtime 705、Application 206 全部通过。
