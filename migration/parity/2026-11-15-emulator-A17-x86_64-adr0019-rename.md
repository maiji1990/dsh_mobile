# 2026-11-15 模拟器冒烟：去掉 `dshbox` 命名 + 数据迁移（ADR-0019）

- 设备：Android 模拟器 `sdk_gphone16k_x86_64`，Android 17（SDK 37），16 KB 页（**不是真机**；真机验收按负责人决定跳过，见 ADR-0018 决策 4）
- 起点：PR-10 构建留下的**真实旧布局**（导入过 DSHBox v1.5.1 运行层、准备过 DSH 层、跑过诊断 / 中继 / 终端）；另用 `run-as` 放入一个
  `user-data/.dsh/plugins/smoke/node_modules/.modules.yaml`（`storeDir: /opt/dshbox-cache/pnpm-store/v10`）以覆盖 pnpm 元数据改写
- 构建：`migration/pr11-rename-dshmobile` 工作区，Debug，`dotnet build -t:Install -p:AdbTarget=-s%20emulator-5554`（覆盖安装，数据保留）
- 单测：Core 35、Runtime 736、Application 83、`tests/guest` node 37 全部通过
- 环境限制同 PR-10：运行层为 arm64，guest 命令在 x86_64 上以 signal 4 退出；这里验证的是迁移与新路径，不是 guest 功能
- 没有接受任何系统对话框，没有作答任何审批

## 迁移前（`run-as` 列目录）

```text
files/dshbox
runtime/runtime-current/{base,node,android-side}/.dshbox/{env.d, layer-<name>.sha256}
runtime/runtime-current/dsh/.dshbox/version
user-data/.dsh/dshbox/{bin/{dsh,pnpm}, dsh-official-plugin/}
```

## 冷启动（覆盖安装后第一次）

```text
runtime coordinator: impl=csharp journal=Ready/Stopped gen=9
name migration (ADR-0019): moved .dsh/dshbox -> .dsh/dshmobile; layer state runtime-current/base: 1 file(s); layer state
  runtime-current/node: 1 file(s); layer state runtime-current/android-side: 1 file(s); layer state runtime-current/dsh: 1 file(s);
  guest paths rewritten in 1 file(s); removed files/dshbox (shims are rewritten under files/dshmobile)
runtime (C#): journal=Ready/Stopped
```

状态行仍为 `runtime=Ready · dsh=Stopped · 0.2.1-rc.5`（迁移后的哨兵被安装检查认出）。迁移后：

```text
dshmobile-runtime/names-migrated-v1
runtime/runtime-current/{base,node,android-side}/.dshmobile/layer-<name>.sha256   （.dshbox/env.d 原样保留：层内容）
runtime/runtime-current/dsh/.dshmobile/version                                    （dsh/.dshbox 已空、已删）
user-data/.dsh/dshmobile/{bin, dsh-official-plugin}                               （user-data/.dsh/dshbox 不存在）
.modules.yaml: storeDir: /opt/dshmobile-cache/pnpm-store/v10
files/dshbox 不存在
```

## 迁移后的操作

| 操作 | 结果（日志 `DshMobile`） |
|---|---|
| 诊断 | `guest assets: written: link-shim.mjs, libdshmobile-link.so, dsh-probe.mjs, dsh/pnpm wrappers (2)`；4 条 guest 命令如实 `FAILED GUEST_CMD_FAILED … signal 4` |
| 写回的文件 | `files/dshmobile/libdshmobile-link.so` = `400b9ba7…1730`、`link-shim.mjs` = `c3450567…9b497`（与钉值一致）；`dsh` / `pnpm` 包装不含 `dshbox`，引用 `/opt/dshmobile/link-shim.mjs` |
| 启动 DSH / 停止 | `start: DshHost` → `FAILED DSH_EXITED`（signal 4，同 PR-10）→ `stop: ok (Ready)` |
| 再次冷启动 | 无 `name migration` 日志（标记存在，不再扫描）；`journal=Ready/Stopped` |

整个过程无 `FATAL` / 应用崩溃。
