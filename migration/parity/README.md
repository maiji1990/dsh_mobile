# 新旧实现比对记录（parity）

ADR-0007 要求每个切片在删除 DSHBox 依赖前给出新旧实现一致的证据。本目录存放**真机**采集的影子比对报告，
文件名：`<日期>-<设备>-<Android 版本>-<切片>.jsonl`（例如 `2026-11-11-PTP-AN10-A17-runtime.bundle-verify.jsonl`）。

采集：

```bash
adb shell run-as org.example.dshmobile cat files/dshmobile-runtime/shadow-reports.jsonl
adb shell run-as org.example.dshmobile cat files/dshmobile-runtime/runtime-journal.jsonl
```

判定：每行 `verdict` 为 `Agree` 才算通过；`Diverge` 是迁移阻断（在 docs/MIGRATION.md 登记原因与处理）；
`Inconclusive` 必须注明旧实现失败的原因不在 C# 检查范围内（如 `SANDBOX_RUNNING`）。

桌面侧新旧双跑（不需要设备）见 `tests/TestVectors/` 与 docs/MIGRATION.md 第 5 节。

当前：**尚无真机报告**（2026-11-10 无设备连接）。
