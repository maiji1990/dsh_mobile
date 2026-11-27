using System.Diagnostics;
using System.Text;
using DshMobile.Runtime.Guest;
using DshMobile.Runtime.Hosting;
using DshMobile.Runtime.Plugins;

namespace DshMobile.Runtime.Tests;

public sealed class GuestPluginServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dshm-plugins-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private RuntimeLayout CreateLayout() => new(
        Path.Combine(_dir, "files"),
        Path.Combine(_dir, "cache"),
        Path.Combine(_dir, "native"));

    [Theory]
    [InlineData("is-number")]
    [InlineData("lodash.get")]
    [InlineData("@deepseek-ai/dsh-demo")]
    [InlineData("@scope/my-plugin_1.0")]
    public void IsValidPackageName_AcceptsCompliantNames(string name)
    {
        Assert.True(PluginCommandRules.IsValidPackageName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-g")]
    [InlineData("--registry=https://evil.test")]
    [InlineData("../evil")]
    [InlineData("foo..bar")]
    [InlineData("foo;id")]
    [InlineData("foo && whoami")]
    [InlineData("$(cat /etc/passwd)")]
    [InlineData("foo bar")]
    [InlineData("foo\n")]
    [InlineData("pkg@1.2.3")]
    [InlineData("https://evil.test/a.tgz")]
    [InlineData("file:./local")]
    public void IsValidPackageName_RejectsInjectionAndInvalidNames(string name)
    {
        Assert.False(PluginCommandRules.IsValidPackageName(name));
    }

    [Theory]
    [InlineData("my-plugin")]
    [InlineData("my-plugin@1.2.3")]
    [InlineData("@deepseek-ai/dsh-demo@0.2.2-rc.5")]
    [InlineData("@scope/pkg@2.0.0+build.1")]
    public void IsValidInstallSpec_AcceptsNameAndExactVersion(string spec)
    {
        Assert.True(PluginCommandRules.IsValidInstallSpec(spec));
    }

    [Theory]
    [InlineData("my-plugin@^1.2.3")]
    [InlineData("my-plugin@~1.2.3")]
    [InlineData("my-plugin@>=1.0.0")]
    [InlineData("my-plugin@latest")]
    [InlineData("my-plugin; rm -rf /")]
    [InlineData("my-plugin && curl evil")]
    [InlineData("$(touch /tmp/x)")]
    [InlineData("--save-exact")]
    [InlineData("../escape")]
    [InlineData("my-plugin\n")]
    public void IsValidInstallSpec_RejectsRangesFlagsAndInjection(string spec)
    {
        Assert.False(PluginCommandRules.IsValidInstallSpec(spec));
    }

    [Fact]
    public void BuildAddCommand_UsesGuestCommandLineSingleQuoting()
    {
        Assert.True(PluginCommandRules.TryBuildAddCommand("web", "@deepseek-ai/dsh-demo@0.2.2-rc.5", out var line, out var error));
        Assert.Null(error);
        Assert.Contains("'/root/projects/.dsh/dshmobile/bin/dsh'", line, StringComparison.Ordinal);
        Assert.Contains("'plugin' '--profile' 'web' 'add' '@deepseek-ai/dsh-demo@0.2.2-rc.5'", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAndRemove_InvalidInputs_DoNotInvokeGuestRunner()
    {
        var calls = 0;
        var svc = new GuestPluginService(CreateLayout(), (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new GuestProcessResult(true, 0, "", null));
        });

        foreach (var bad in new[] { "foo;id", "foo && ls", "$(id)", "--flag", "../bad", "pkg@^1.0.0", "pkg\n" })
        {
            var addRes = await svc.InstallAsync(bad, CancellationToken.None);
            Assert.False(addRes.Ok);
            Assert.Equal(RuntimeErrorCodes.BadRequest, addRes.Code);

            var remRes = await svc.RemoveAsync(bad, CancellationToken.None);
            Assert.False(remRes.Ok);
            Assert.Equal(RuntimeErrorCodes.BadRequest, remRes.Code);
        }

        var protectedRem = await svc.RemoveAsync("@deepseek-ai/dsh-web-app", CancellationToken.None);
        Assert.False(protectedRem.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedRem.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task InstallAsync_RedactsSecretsOnFailure()
    {
        var svc = new GuestPluginService(CreateLayout(), (_, _, _) =>
            Task.FromResult(new GuestProcessResult(
                false,
                1,
                "npm ERR! Authorization: Bearer secret-jwt-token-xyz\napiKey=sk-live-secret-999999",
                "GUEST_CMD_FAILED")));

        var res = await svc.InstallAsync("demo-plugin@1.0.0", CancellationToken.None);
        Assert.False(res.Ok);
        Assert.NotNull(res.Diagnostic);
        Assert.DoesNotContain("secret-jwt-token-xyz", res.Diagnostic!, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-secret-999999", res.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListInstalledAsync_ParsesPnpmJsonAndDetectsCustomWebUi()
    {
        var layout = CreateLayout();
        var pkgDir = Path.Combine(GuestPluginService.WebProfileHostDir(layout), "node_modules", "@thirdparty", "my-ui-plugin");
        Directory.CreateDirectory(pkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(pkgDir, "package.json"),
            """{"name":"@thirdparty/my-ui-plugin","version":"1.4.2","dsh":{"client":{"platform":"web"}}}""");

        const string stdout = """
            dsh: warning: sample pre-output
            [
              {
                "name": "web-profile",
                "dependencies": {
                  "@thirdparty/my-ui-plugin": { "version": "1.4.2" },
                  "helper-plugin": { "version": "0.9.0" }
                }
              }
            ]
            """;

        var svc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, stdout, null)));

        var res = await svc.ListInstalledAsync(CancellationToken.None);
        Assert.True(res.Ok);
        Assert.Equal(2, res.Packages.Count);
        var uiPkg = Assert.Single(res.Packages, p => p.Name == "@thirdparty/my-ui-plugin");
        Assert.True(uiPkg.HasCustomWebUi);
        Assert.Equal("1.4.2", uiPkg.Version);

        var plainPkg = Assert.Single(res.Packages, p => p.Name == "helper-plugin");
        Assert.False(plainPkg.HasCustomWebUi);
    }

    [Fact]
    public void PluginPatchEditor_PreservesCommentsAndUpdatesDisabledFlagAtomically()
    {
        var patchPath = Path.Combine(_dir, "cordis.patch.yml");
        File.WriteAllText(patchPath, """
            # user comment
            - id: custom-plugin
              disabled: false
            - id: other-js
              config: !!js process.env.FOO
            """);

        var first = PluginPatchEditor.SetPluginDisabled(patchPath, "custom-plugin", disabled: true);
        Assert.True(first.Ok);
        var content1 = File.ReadAllText(patchPath);
        Assert.Contains("# user comment", content1, StringComparison.Ordinal);
        Assert.Contains("disabled: true", content1, StringComparison.Ordinal);
        Assert.Contains("config: !!js process.env.FOO", content1, StringComparison.Ordinal);

        var second = PluginPatchEditor.SetPluginDisabled(patchPath, "new-plugin", disabled: true);
        Assert.True(second.Ok);
        var content2 = File.ReadAllText(patchPath);
        Assert.Contains("- id: new-plugin", content2, StringComparison.Ordinal);
        Assert.False(File.Exists(patchPath + ".tmp"));
    }

    [Fact]
    public async Task SetEnabledAsync_RejectsProtectedCoreEntries()
    {
        var layout = CreateLayout();
        var svc = new GuestPluginService(layout);

        var protectedRes = await svc.SetEnabledAsync("include:connection", "@deepseek-ai/dsh-client-connection", false, CancellationToken.None);
        Assert.False(protectedRes.Ok);
        Assert.Equal(RuntimeErrorCodes.BadRequest, protectedRes.Code);
        Assert.False(File.Exists(GuestPluginService.WebProfilePatchFile(layout)));
    }

    [Fact]
    public async Task ListInstalledAsync_ResolvesPnpmInternalSymlinks_AndExtractsFullMetadata()
    {
        var layout = CreateLayout();
        var nodeModulesDir = GuestPluginService.WebProfileNodeModulesHostDir(layout);
        var pnpmPkgDir = Path.Combine(nodeModulesDir, ".pnpm", "@thirdparty+ui-ext@2.1.0", "node_modules", "@thirdparty", "ui-ext");
        Directory.CreateDirectory(pnpmPkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(pnpmPkgDir, "package.json"),
            """
            {
              "name": "@thirdparty/ui-ext",
              "version": "2.1.0",
              "description": "Custom WebUI extension",
              "author": { "name": "Alice Developer" },
              "license": "MIT",
              "homepage": "https://example.com/ui-ext",
              "repository": { "type": "git", "url": "git+https://github.com/example/ui-ext.git" },
              "dsh": {
                "client": { "platform": "web" },
                "bundle": { "patch": "./cordis.patch.yml" }
              }
            }
            """);

        var scopeDir = Path.Combine(nodeModulesDir, "@thirdparty");
        Directory.CreateDirectory(scopeDir);
        var symlinkPath = Path.Combine(scopeDir, "ui-ext");
        CreateDirectoryLinkForTest(
            symlinkPath,
            relativeTarget: Path.Combine("..", ".pnpm", "@thirdparty+ui-ext@2.1.0", "node_modules", "@thirdparty", "ui-ext"),
            absoluteTarget: pnpmPkgDir);

        // 同时验证不安全 URL（http / javascript / 含凭据）会被拒绝置空。
        var unsafePkgDir = Path.Combine(nodeModulesDir, "plain-helper");
        Directory.CreateDirectory(unsafePkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(unsafePkgDir, "package.json"),
            """
            {
              "name": "plain-helper",
              "version": "0.5.0",
              "author": "Bob",
              "homepage": "javascript:alert(1)",
              "repository": "http://insecure.example.com/repo.git"
            }
            """);

        const string stdout = """
            [
              {
                "name": "web-profile",
                "dependencies": {
                  "@thirdparty/ui-ext": { "version": "2.1.0" },
                  "plain-helper": { "version": "0.5.0" }
                }
              }
            ]
            """;

        var svc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, stdout, null)));

        var res = await svc.ListInstalledAsync(CancellationToken.None);
        Assert.True(res.Ok);

        var uiPkg = Assert.Single(res.Packages, p => p.Name == "@thirdparty/ui-ext");
        Assert.True(uiPkg.HasCustomWebUi);
        Assert.True(uiPkg.HasBundleManifest);
        Assert.Equal("Custom WebUI extension", uiPkg.Description);
        Assert.Equal("Alice Developer", uiPkg.Author);
        Assert.Equal("MIT", uiPkg.License);
        Assert.Equal("https://example.com/ui-ext", uiPkg.HomepageUrl);
        Assert.Equal("https://github.com/example/ui-ext.git", uiPkg.RepositoryUrl);

        var plainPkg = Assert.Single(res.Packages, p => p.Name == "plain-helper");
        Assert.False(plainPkg.HasCustomWebUi);
        Assert.False(plainPkg.HasBundleManifest);
        Assert.Equal("Bob", plainPkg.Author);
        Assert.Null(plainPkg.HomepageUrl);
        Assert.Null(plainPkg.RepositoryUrl);
    }

    [Fact]
    public async Task ListInstalledAsync_RejectsEscapingSymlinksOutsideNodeModulesBoundary()
    {
        var layout = CreateLayout();
        var nodeModulesDir = GuestPluginService.WebProfileNodeModulesHostDir(layout);
        Directory.CreateDirectory(nodeModulesDir);

        // 构造逃逸到 node_modules 边界之外的目录与 manifest。
        var outsideDir = Path.Combine(_dir, "outside-secret-pkg");
        Directory.CreateDirectory(outsideDir);
        await File.WriteAllTextAsync(
            Path.Combine(outsideDir, "package.json"),
            """{"name":"escaped-pkg","version":"1.0.0","dsh":{"client":{"platform":"web"},"bundle":{"patch":"./p.yml"}}}""");

        // 1) 直接逃逸符号链接
        CreateDirectoryLinkForTest(
            Path.Combine(nodeModulesDir, "escaped-pkg"),
            relativeTarget: Path.GetRelativePath(nodeModulesDir, outsideDir),
            absoluteTarget: outsideDir);

        // 2) 中间目录逃逸的多级符号链接：node_modules/nested-escape -> .pnpm/bridge/pkg，而 .pnpm/bridge -> outsideRoot
        var outsideNestedRoot = Path.Combine(_dir, "outside-nested");
        var outsideNestedPkg = Path.Combine(outsideNestedRoot, "pkg");
        Directory.CreateDirectory(outsideNestedPkg);
        await File.WriteAllTextAsync(
            Path.Combine(outsideNestedPkg, "package.json"),
            """{"name":"nested-escape","version":"1.0.0","dsh":{"client":{"platform":"web"}}}""");
        var pnpmDir = Path.Combine(nodeModulesDir, ".pnpm");
        Directory.CreateDirectory(pnpmDir);
        var bridgeLink = Path.Combine(pnpmDir, "bridge");
        CreateDirectoryLinkForTest(
            bridgeLink,
            relativeTarget: Path.GetRelativePath(pnpmDir, outsideNestedRoot),
            absoluteTarget: outsideNestedRoot);
        CreateDirectoryLinkForTest(
            Path.Combine(nodeModulesDir, "nested-escape"),
            relativeTarget: Path.Combine(".pnpm", "bridge", "pkg"),
            absoluteTarget: Path.Combine(bridgeLink, "pkg"));

        const string stdout = """
            [
              {
                "name": "web-profile",
                "dependencies": {
                  "escaped-pkg": { "version": "1.0.0" },
                  "nested-escape": { "version": "1.0.0" }
                }
              }
            ]
            """;

        var svc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, stdout, null)));

        var res = await svc.ListInstalledAsync(CancellationToken.None);
        Assert.True(res.Ok);

        var escaped = Assert.Single(res.Packages, p => p.Name == "escaped-pkg");
        Assert.False(escaped.HasCustomWebUi);
        Assert.False(escaped.HasBundleManifest);

        var nested = Assert.Single(res.Packages, p => p.Name == "nested-escape");
        Assert.False(nested.HasCustomWebUi);
        Assert.False(nested.HasBundleManifest);
    }

    [Fact]
    public async Task InstallAsync_PropagatesNonBundleWarning_FromOutputOrManifest()
    {
        var layout = CreateLayout();

        // 情形 1：dsh plugin add 输出明确告警 declares no dsh.bundle
        var warnSvc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, "warn: package foo-lib declares no dsh.bundle; installed as dependency", null)));
        var warnRes = await warnSvc.InstallAsync("foo-lib@1.0.0", CancellationToken.None);
        Assert.True(warnRes.Ok);
        Assert.False(warnRes.DeclaredBundleManifest);
        Assert.Equal(GuestPluginService.NoBundleWarningSummary, warnRes.WarningSummary);

        // 情形 2：stdout 无提示，但落盘 package.json 未声明 dsh.bundle.patch
        var plainPkgDir = Path.Combine(GuestPluginService.WebProfileNodeModulesHostDir(layout), "quiet-lib");
        Directory.CreateDirectory(plainPkgDir);
        await File.WriteAllTextAsync(Path.Combine(plainPkgDir, "package.json"), """{"name":"quiet-lib","version":"1.2.3"}""");
        var quietSvc = new GuestPluginService(layout, (_, _, _) =>
            Task.FromResult(new GuestProcessResult(true, 0, "Done in 1.2s", null)));
        var quietRes = await quietSvc.InstallAsync("quiet-lib@1.2.3", CancellationToken.None);
        Assert.True(quietRes.Ok);
        Assert.False(quietRes.DeclaredBundleManifest);
        Assert.NotNull(quietRes.WarningSummary);

        // 情形 3：正常声明了 dsh.bundle.patch 的插件包不产生告警
        var bundlePkgDir = Path.Combine(GuestPluginService.WebProfileNodeModulesHostDir(layout), "@thirdparty", "real-bundle");
        Directory.CreateDirectory(bundlePkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(bundlePkgDir, "package.json"),
            """{"name":"@thirdparty/real-bundle","version":"1.0.0","dsh":{"bundle":{"patch":"./cordis.patch.yml"}}}""");
        var bundleRes = await quietSvc.InstallAsync("@thirdparty/real-bundle@1.0.0", CancellationToken.None);
        Assert.True(bundleRes.Ok);
        Assert.True(bundleRes.DeclaredBundleManifest);
        Assert.Null(bundleRes.WarningSummary);
    }

    [Fact]
    public async Task ReadLocalPackageDetailAsync_PrefersLocaleMetaDetail_StripsBomAndFrontMatter()
    {
        var layout = CreateLayout();
        var nodeModulesDir = GuestPluginService.WebProfileNodeModulesHostDir(layout);
        var realPkgDir = Path.Combine(nodeModulesDir, ".pnpm", "demo-plugin@3.0.0", "node_modules", "demo-plugin");
        Directory.CreateDirectory(Path.Combine(realPkgDir, "locale"));
        Directory.CreateDirectory(Path.Combine(realPkgDir, "docs"));

        await File.WriteAllTextAsync(
            Path.Combine(realPkgDir, "package.json"),
            """
            {
              "name": "demo-plugin",
              "version": "3.0.0",
              "description": "Demo plugin description",
              "author": "Demo Team",
              "license": "Apache-2.0",
              "homepage": "https://example.com/demo",
              "repository": { "url": "https://github.com/example/demo" },
              "dsh": { "client": { "entry": "index.js" }, "bundle": { "patch": "patch.yml" } }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(realPkgDir, "locale", "zh-cn.json"),
            """{"meta":{"title":"演示插件","detail":"./docs/detail-zh.md"}}""");
        await File.WriteAllTextAsync(
            Path.Combine(realPkgDir, "docs", "detail-zh.md"),
            "\uFEFF---\ntitle: 演示插件\norder: 1\n---\n\n# 演示插件\n\n这是中文详情文档。",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await File.WriteAllTextAsync(
            Path.Combine(realPkgDir, "README.md"),
            "# Fallback README");

        CreateDirectoryLinkForTest(
            Path.Combine(nodeModulesDir, "demo-plugin"),
            relativeTarget: Path.Combine(".pnpm", "demo-plugin@3.0.0", "node_modules", "demo-plugin"),
            absoluteTarget: realPkgDir);

        var svc = new GuestPluginService(layout);
        var detail = await svc.ReadLocalPackageDetailAsync("demo-plugin", "zh-CN", CancellationToken.None);

        Assert.True(detail.Ok);
        Assert.Equal("3.0.0", detail.Version);
        Assert.Equal("Demo plugin description", detail.Description);
        Assert.Equal("Demo Team", detail.Author);
        Assert.Equal("Apache-2.0", detail.License);
        Assert.Equal("https://example.com/demo", detail.HomepageUrl);
        Assert.Equal("https://github.com/example/demo", detail.RepositoryUrl);
        Assert.True(detail.HasCustomWebUi);
        Assert.True(detail.HasBundleManifest);
        Assert.Equal(GuestLocalPackageDetail.OriginLocaleDetail, detail.DocumentOrigin);
        Assert.Equal("docs/detail-zh.md", detail.DocumentRelativePath);
        Assert.StartsWith("# 演示插件", detail.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("---", detail.Markdown!, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFEFF", detail.Markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadLocalPackageDetailAsync_FallsBackToReadme_AndReadonlyDshLayer()
    {
        var layout = CreateLayout();
        var runtimePkgDir = Path.Combine(GuestPluginService.DshRuntimeNodeModulesHostDir(layout), "@deepseek-ai", "dsh-sample");
        Directory.CreateDirectory(runtimePkgDir);
        await File.WriteAllTextAsync(
            Path.Combine(runtimePkgDir, "package.json"),
            """{"name":"@deepseek-ai/dsh-sample","version":"0.2.2","description":"Built-in sample"}""");
        await File.WriteAllTextAsync(
            Path.Combine(runtimePkgDir, "README.md"),
            "---\nlang: en\n---\n# Built-in Sample\n\nLoaded from readonly layer.");

        var svc = new GuestPluginService(layout);
        var detail = await svc.ReadLocalPackageDetailAsync("@deepseek-ai/dsh-sample", "zh-CN", CancellationToken.None);

        Assert.True(detail.Ok);
        Assert.Equal("0.2.2", detail.Version);
        Assert.Equal("dsh-runtime", detail.PackageRootSource);
        Assert.Equal(GuestLocalPackageDetail.OriginReadme, detail.DocumentOrigin);
        Assert.Equal("# Built-in Sample\n\nLoaded from readonly layer.", detail.Markdown);
    }

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("docs/../../secret.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("https://evil.example/readme.md")]
    [InlineData("file:///etc/passwd")]
    public async Task ReadLocalPackageDetailAsync_RejectsMaliciousMetaDetailPaths(string maliciousDetailPath)
    {
        var layout = CreateLayout();
        var nodeModulesDir = GuestPluginService.WebProfileNodeModulesHostDir(layout);
        var pkgDir = Path.Combine(nodeModulesDir, "malicious-detail");
        Directory.CreateDirectory(Path.Combine(pkgDir, "locale"));
        await File.WriteAllTextAsync(Path.Combine(nodeModulesDir, "secret.md"), "TOP_SECRET_DATA");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "package.json"), """{"name":"malicious-detail","version":"1.0.0"}""");
        await File.WriteAllTextAsync(
            Path.Combine(pkgDir, "locale", "zh-cn.json"),
            "{\"meta\":{\"detail\":" + System.Text.Json.JsonSerializer.Serialize(maliciousDetailPath) + "}}");

        var svc = new GuestPluginService(layout);
        var detail = await svc.ReadLocalPackageDetailAsync("malicious-detail", "zh-CN", CancellationToken.None);

        Assert.True(detail.Ok);
        Assert.Null(detail.Markdown);
        Assert.Equal(GuestLocalPackageDetail.OriginNone, detail.DocumentOrigin);
    }

    [Fact]
    public async Task ReadLocalPackageDetailAsync_RejectsEscapingSymlinkInsidePackage_AndBoundsMaxBytes()
    {
        var layout = CreateLayout();
        var nodeModulesDir = GuestPluginService.WebProfileNodeModulesHostDir(layout);
        var pkgDir = Path.Combine(nodeModulesDir, "link-escape-doc");
        Directory.CreateDirectory(Path.Combine(pkgDir, "locale"));

        var outsideDocsDir = Path.Combine(_dir, "outside-docs");
        Directory.CreateDirectory(outsideDocsDir);
        await File.WriteAllTextAsync(Path.Combine(outsideDocsDir, "secret.md"), "SECRET_OUTSIDE_CONTENT");

        // 包内 docs 目录指向包外目录，应被严格拒绝，随后回退至超大 README 并按 64 KiB 截断。
        CreateDirectoryLinkForTest(
            Path.Combine(pkgDir, "docs"),
            relativeTarget: Path.GetRelativePath(pkgDir, outsideDocsDir),
            absoluteTarget: outsideDocsDir);

        await File.WriteAllTextAsync(Path.Combine(pkgDir, "package.json"), """{"name":"link-escape-doc","version":"1.0.0"}""");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "locale", "en.json"), """{"meta":{"detail":"docs/secret.md"}}""");

        // 构造超过 64 KiB 的中文 README（每字 3 字节）验证截断不会产生乱码字符。
        var oversized = new string('测', 30_000);
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "README.md"), oversized, Encoding.UTF8);

        var svc = new GuestPluginService(layout);
        var detail = await svc.ReadLocalPackageDetailAsync("link-escape-doc", "en", CancellationToken.None);

        Assert.True(detail.Ok);
        Assert.Equal(GuestLocalPackageDetail.OriginReadme, detail.DocumentOrigin);
        Assert.NotNull(detail.Markdown);
        Assert.DoesNotContain("SECRET_OUTSIDE_CONTENT", detail.Markdown!, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(detail.Markdown!) <= GuestPluginService.MaxLocalDetailBytes);
        Assert.DoesNotContain('\uFFFD', detail.Markdown!);
    }

    [Fact]
    public void WebTrimPatchInventory_MatchesEmbeddedPatchAndExcludesCoreControllers()
    {
        var trimmed = WebTrimPatchInventory.TrimmedEntryIds;

        Assert.Contains("modules", trimmed);
        Assert.Contains("ui-layout", trimmed);
        Assert.Contains("hmr", trimmed);
        Assert.DoesNotContain("settings-controller", trimmed);
        Assert.DoesNotContain("plugin-manager", trimmed);
        Assert.DoesNotContain("plugin-inventory", trimmed);
        Assert.True(WebTrimPatchInventory.IsTrimmedEntry("modules"));
        Assert.True(WebTrimPatchInventory.IsTrimmedEntry("include:ui-layout"));
        Assert.False(WebTrimPatchInventory.IsTrimmedEntry("include:settings-controller"));

        var parsed = WebTrimPatchInventory.ParseDisabledEntryIds("""
            # comment
            - id: alpha
              disabled: true
            - id: beta
              disabled: false
            - id: gamma
              disabled: true
            """);
        Assert.Contains("alpha", parsed);
        Assert.DoesNotContain("beta", parsed);
        Assert.Contains("gamma", parsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DshHost_BuildDshArgv_RespectsDynamicUseTrimmedWebDelegate(bool inPlaceRestart)
    {
        var layout = CreateLayout();
        var trimHost = GuestAssetProvisioner.WebTrimPatchHostPath(layout);
        Directory.CreateDirectory(Path.GetDirectoryName(trimHost)!);
        File.WriteAllText(trimHost, "- id: modules\n  disabled: true\n");

        var preferTrimmed = true;
        var options = new DshHostOptions(TrimmedWeb: true, UseTrimmedWeb: () => preferTrimmed);
        var builder = new ProotCommandBuilder(layout);

        var trimmedArgv = DshHost.BuildDshArgv(builder, options, inPlaceRestart, out var effective1);
        Assert.True(effective1);
        Assert.Contains(GuestAssetProvisioner.GuestWebTrimPatch, trimmedArgv);
        Assert.Equal("--no-open", trimmedArgv[^1]);

        preferTrimmed = false;
        var fullArgv = DshHost.BuildDshArgv(builder, options, inPlaceRestart, out var effective2);
        Assert.False(effective2);
        Assert.DoesNotContain(GuestAssetProvisioner.GuestWebTrimPatch, fullArgv);
        Assert.DoesNotContain("--no-open", fullArgv);
    }

    [Fact]
    public async Task DshHost_TryBuildBrowserLaunchUri_EnforcesStrictSafetyGates()
    {
        // 非 Ready 状态一律返回 null
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Stopped, isTrimmedWebActive: false, 3080, "tok-123"));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Starting, isTrimmedWebActive: false, 3080, "tok-123"));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Stopping, isTrimmedWebActive: false, 3080, "tok-123"));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Failed, isTrimmedWebActive: false, 3080, "tok-123"));

        // 精简模式一律返回 null
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: true, 3080, "tok-123"));

        // 令牌缺失或端口非法一律返回 null
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: false, 3080, null));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: false, 3080, "   "));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: false, 0, "tok-123"));
        Assert.Null(DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: false, 70000, "tok-123"));

        // 仅当 Ready + 完整模式 + 合法端口 + 非空令牌时返回 127.0.0.1 URI，且对特殊字符进行安全转义
        var uri = DshHost.TryBuildBrowserLaunchUri(DshHostState.Ready, isTrimmedWebActive: false, 3080, "abc+123/=?&");
        Assert.NotNull(uri);
        Assert.Equal("http", uri!.Scheme);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(3080, uri.Port);
        Assert.Equal("http://127.0.0.1:3080/?token=abc%2B123%2F%3D%3F%26", uri.AbsoluteUri);

        // 未启动的真实 DshHost 实例应返回 null
        await using var host = new DshHost(
            CreateLayout(),
            _ => { },
            time: null,
            terminator: null,
            gracefulStopTimeout: null,
            options: new DshHostOptions(TrimmedWeb: false));
        Assert.Null(host.TryBuildBrowserLaunchUri());
    }

    private static void CreateDirectoryLinkForTest(string linkPath, string relativeTarget, string absoluteTarget)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, relativeTarget);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            // 未开启开发者模式的 Windows 环境回退到 NTFS Junction（同样会被 DirectoryInfo.LinkTarget / ResolveLinkTarget 解析）。
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{absoluteTarget}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new IOException($"mklink /J failed: {proc.StandardError.ReadToEnd()}");
            }
        }
    }
}
