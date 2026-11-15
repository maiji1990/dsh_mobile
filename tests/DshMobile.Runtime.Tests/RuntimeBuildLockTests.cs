using System.Security.Cryptography;
using System.Text.Json;

namespace DshMobile.Runtime.Tests;

/// <summary>ADR-0020: the pinned inputs of the self-built runtime layers agree with the files they pin.</summary>
public sealed class RuntimeBuildLockTests
{
    private static Dictionary<string, string> Lock() =>
        File.ReadAllLines(Repo.PathOf("runtime-bundle", "runtime-build.lock"))
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..], StringComparer.Ordinal);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void DshManifests_MatchTheirPinnedHashes()
    {
        var l = Lock();
        Assert.Equal(l["DSH_PACKAGE_JSON_SHA256"], Sha(Repo.PathOf("runtime-bundle", "dsh", "package.json")));
        Assert.Equal(l["DSH_PACKAGE_LOCK_SHA256"], Sha(Repo.PathOf("runtime-bundle", "dsh", "package-lock.json")));
    }

    [Fact]
    public void DshDependency_IsTheExactLockedVersion()
    {
        var l = Lock();
        using var pkg = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("runtime-bundle", "dsh", "package.json")));
        Assert.Equal(l["DSH_VERSION"], pkg.RootElement.GetProperty("dependencies").GetProperty("@deepseek-ai/dsh").GetString());
        using var lockJson = JsonDocument.Parse(File.ReadAllText(Repo.PathOf("runtime-bundle", "dsh", "package-lock.json")));
        var packages = lockJson.RootElement.GetProperty("packages");
        Assert.Equal(l["DSH_VERSION"], packages.GetProperty("").GetProperty("dependencies").GetProperty("@deepseek-ai/dsh").GetString());
        Assert.Equal(l["DSH_VERSION"], packages.GetProperty("node_modules/@deepseek-ai/dsh").GetProperty("version").GetString());
    }

    [Fact]
    public void Inputs_ArePinned_NotFloating()
    {
        var l = Lock();
        Assert.Matches("^[0-9]{8}T[0-9]{6}Z$", l["DEBIAN_SNAPSHOT"]);
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", l["NODE_VERSION"]);
        Assert.Matches("^[0-9a-f]{64}$", l["NODE_SHA256"]);
        Assert.Equal("arm64", l["ARCH"]);
        Assert.StartsWith("dshmobile-runtime-", l["BUNDLE_NAME"], StringComparison.Ordinal);
        Assert.DoesNotContain(l.Values, v => v.Contains("latest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildScript_WritesTheDshmobileEnvFragments()
    {
        var script = File.ReadAllText(Repo.PathOf("runtime-bundle", "build-runtime.sh"));
        Assert.Contains("\"env_file\": f\".dshmobile/env.d/{n}.sh\"", script, StringComparison.Ordinal);
        Assert.Contains("$dir/.dshmobile/env.d", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".dshbox", script, StringComparison.Ordinal);
    }
}
