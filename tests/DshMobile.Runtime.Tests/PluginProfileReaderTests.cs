using DshMobile.Runtime.Plugins;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The host-side profile reader on a real (temp) file system. Package shapes follow upstream ProfileReader / installGap:
/// a plain npm package (no <c>dsh</c> — what `dsh plugin add is-number` really installs), declared-but-missing entry,
/// carrier mount, client-only, scoped names.
/// </summary>
public sealed class PluginProfileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-profile-" + Guid.NewGuid().ToString("N"));

    private string Profiles => Path.Combine(_root, "user-data", ".dsh", "profiles");

    private string Web => Path.Combine(Profiles, "web");

    private PluginProfileReader Reader() => new(Profiles, "web", Path.Combine(_root, "runtime-current"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private const string P = "user-data/.dsh/profiles";

    [Fact]
    public void DependencyNames_ReadsKeys_AndTellsMissingOrBrokenApart()
    {
        Assert.Null(Reader().DependencyNames());
        Write($"{P}/web/package.json", "{ broken");
        Assert.Null(Reader().DependencyNames());
        Write($"{P}/web/package.json", """{"name":"dsh-profile-web"}""");
        Assert.Empty(Reader().DependencyNames()!);
        Write($"{P}/web/package.json", "﻿" + """{"dependencies":{"a":"^1.0.0","@s/b":"2.0.0"},"devDependencies":{"c":"1"}}""");
        Assert.Equal(["@s/b", "a"], Reader().DependencyNames()!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PlainNpmPackage_HasNoDshField()
    {
        Write($"{P}/web/node_modules/is-number/package.json", """{"name":"is-number","version":"7.0.0","main":"index.js"}""");
        Write($"{P}/web/node_modules/is-number/index.js", "");
        var f = Reader().Facts("is-number");
        Assert.True(f.Installed);
        Assert.Equal("7.0.0", f.Version);
        Assert.False(f.HasDshField);
        Assert.Contains("没有 dsh 字段", PluginPackageRules.InstallGap(f)!.Zh, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDirectory_AndBrokenManifest()
    {
        Assert.False(Reader().Facts("ghost").Installed);
        Write($"{P}/web/node_modules/bad/package.json", "{");
        var f = Reader().Facts("bad");
        Assert.True(f.Installed);
        Assert.False(f.ManifestReadable);
    }

    [Fact]
    public void BundlePlugin_WithEntry_IsLoadable_WithoutEntry_IsNot()
    {
        Write($"{P}/web/node_modules/@acme/good/package.json", """{"version":"1.0.0","main":"./lib/index.cjs","dsh":{"bundle":{"patch":"./cordis.patch.yml"}}}""");
        Write($"{P}/web/node_modules/@acme/good/lib/index.cjs", "");
        Assert.Null(PluginPackageRules.InstallGap(Reader().Facts("@acme/good")));

        Write($"{P}/web/node_modules/nobuild/package.json", """{"version":"1.0.0","exports":{".":{"import":"./dist/index.mjs"}},"dsh":{"bundle":{}}}""");
        var f = Reader().Facts("nobuild");
        Assert.True(f.HasBundle);
        Assert.False(f.BundleEntryExists);
        Assert.Contains("入口产物文件不存在", PluginPackageRules.InstallGap(f)!.Zh, StringComparison.Ordinal);
    }

    [Fact]
    public void CarrierPackage_MountingAPackageWithAnEntry_IsLoadable()
    {
        Write($"{P}/web/node_modules/carrier/package.json", """{"version":"1.0.0","dsh":{"bundle":{"patch":["patches/a.yml"]}}}""");
        Write($"{P}/web/node_modules/carrier/patches/a.yml", "- name: 'carrier'\n- name: \"mounted\"\n  config: {}\r\n");
        // Hoisted to profiles/node_modules (one of the four upstream resolution roots).
        Write($"{P}/node_modules/mounted/package.json", """{"exports":"./main.js"}""");
        Write($"{P}/node_modules/mounted/main.js", "");
        var f = Reader().Facts("carrier");
        Assert.False(f.BundleEntryExists);
        Assert.True(f.HasCarrierEntry);
        Assert.Null(PluginPackageRules.InstallGap(f));
    }

    [Fact]
    public void CarrierWhoseTargetHasNoEntry_IsNotLoadable()
    {
        Write($"{P}/web/node_modules/carrier/package.json", """{"dsh":{"bundle":"cordis.patch.yml"}}""");
        Write($"{P}/web/node_modules/carrier/cordis.patch.yml", "- name: mounted\n");
        Write($"{P}/node_modules/mounted/package.json", """{"main":"missing.js"}""");
        Assert.False(Reader().Facts("carrier").HasCarrierEntry);
    }

    [Fact]
    public void ClientOnlyPlugin_UsesClientDefault()
    {
        Write($"{P}/web/node_modules/ui-only/package.json", """{"dsh":{"client":{}}}""");
        Write($"{P}/web/node_modules/ui-only/client/client.js", "");
        Assert.Null(PluginPackageRules.InstallGap(Reader().Facts("ui-only")));
    }

    [Fact]
    public void NamesThatAreNotPackageNames_AreNeverResolved()
    {
        Write("user-data/.dsh/profiles/escape/package.json", """{"dsh":{"client":"x.js"}}""");
        Assert.False(Reader().Facts("../escape").Installed);
        Assert.False(Reader().Facts("..").Installed);
    }

    [Fact]
    public void UnknownProfile_IsRejected() =>
        Assert.Throws<ArgumentException>(() => new PluginProfileReader(Profiles, "desktop", _root));
}
