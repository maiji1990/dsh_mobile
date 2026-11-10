# ADR-0002：以固定 SHA 子模块 + Java Facade AAR 复用 DSHBox

- 状态：**已被 [ADR-0007](0007-source-ownership-csharp-first.md) 取代**（2026-11-10）。原状态：已接受（2026-11-09）。
- 说明：本 ADR 描述的子模块 + Facade 结构在迁移期间继续作为**过渡实现与比对基线**存在，直到各切片按 ADR-0007 通过真机验收后逐一删除；不得再据此新增对 DSHBox AAR / Java Facade / Kotlin 业务层的长期依赖。
- 来源：方案 2、2.1、6.1 节

## 决策

1. DSHBox 以 **git submodule**（shallow）固定在 `v1.5.1` / `7c9d33c`，路径 `third_party/dshbox`，不修改其文件。
2. 新建独立 Gradle 工程 `native/`：
   - 版本目录直接 `from(files("../third_party/dshbox/gradle/libs.versions.toml"))`，与 DSHBox 共用一份版本事实源。
   - 以 `:dshbox-common` 等名字引用 DSHBox 模块源码目录；**构建输出重定向**到 `native/build/upstream/<module>`，保持子模块工作区干净。
   - `:native-bridge` 是唯一对 .NET 暴露的模块，只有纯 Java 公共类型（无 `suspend`、Flow、Compose）。
3. `native/build-native.*` 生成 `artifacts/native/dsh-native-bridge-release.aar` 与各 DSHBox 模块 AAR，并写 SHA-256；二进制不入库。
4. .NET 侧 `DshMobile.NativeBinding` 只为 `dsh-native-bridge` 生成绑定；DSHBox 模块 AAR `Bind="false"` 仅随包。其 Maven 传递依赖映射为对应 NuGet 绑定包（当前仅 `Xamarin.Kotlin.StdLib`），每引入一个 DSHBox 模块需重新审计 POM。

## 备选与否决

- 整仓 subtree/vendor：体积大、同步审查成本高；保留为后续选项。
- 直接绑定 DSHBox 全部 Kotlin 类：类型数量巨大、Kotlin 语义（协程/Flow）无法良好映射，违背方案 6.1。

## 后果

- DSHBox 升级 = 子模块指针移动 + `UPSTREAMS.md` 记录 + 兼容测试，单独 PR。
- DSHBox 模块内若引用 `R.*` 资源，其 AAR 的资源会随包；命名空间 `com.dshbox.*` 保留（源码归属），**不得**带入品牌图形。
