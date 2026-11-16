using System.Security.Cryptography;
using DshMobile.Runtime.Updates;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// vNext PR-02: the embedded trusted-keys file (shared with release-publish.yml) and the manifest that
/// tooling/release-manifest-build.mjs writes (tests/tooling/release-manifest.test.mjs rebuilds the same bytes).
/// </summary>
public sealed class ReleaseTrustFileTests
{
    private static string Built(params string[] parts) => Repo.PathOf(["tests", "TestVectors", "release-manifest", "built", .. parts]);

    [Fact]
    public void BuiltIn_IsLoadedFromTheEmbeddedFile_AndIsEmptyToday()
    {
        using var s = typeof(ReleaseTrust).Assembly.GetManifestResourceStream(ReleaseTrust.BuiltInResource);
        Assert.NotNull(s);
        var source = File.ReadAllText(Repo.PathOf("src", "DshMobile.Runtime", "Updates", "release-trusted-keys.txt"));
        Assert.Equal(source.ReplaceLineEndings("\n"), new StreamReader(s).ReadToEnd().ReplaceLineEndings("\n"));
        // Parses cleanly (a malformed file would silently fall back to "trust nothing" in the app).
        Assert.Equal(ReleaseTrust.Parse(source).KeyIds, ReleaseTrust.BuiltIn.KeyIds);
        Assert.True(ReleaseTrust.BuiltIn.IsEmpty, "a release key was added: record its keyId in ADR-0022 and update this test");
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlanks_AndRefusesBadKeys()
    {
        var key = "0/7Vy+xzt4e19iEbU63oDxGycevISE5500hV1Q8yu3o=";
        Assert.Equal(["ed25519:c0c914c1caa1fa90"], ReleaseTrust.Parse($"# comment\n\n  {key}  \r\n").KeyIds);
        Assert.Throws<FormatException>(() => ReleaseTrust.Parse("not base64!"));
        Assert.Throws<ArgumentException>(() => ReleaseTrust.Parse(Convert.ToBase64String(new byte[31])));
    }

    [Fact]
    public void ManifestBuiltByTheNodeTool_IsAcceptedByTheApp_AndItsDigestsMatchTheFiles()
    {
        var m = ReleaseManifestParser.Parse(File.ReadAllBytes(Built("release-manifest.json")));
        Assert.Equal(("preview", 2), (m.Channel, m.Artifacts.Count));
        foreach (var a in m.Artifacts)
        {
            var file = Built("artifacts", a.Archive);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))), a.Sha256);
            Assert.Equal(new FileInfo(file).Length, a.Size);
            Assert.Equal(new Uri($"https://github.com/{ReleaseEndpoints.Repository}/releases/download/runtime-0.2.0/{a.Archive}"), a.Url);
        }
        var dsh = Assert.Single(m.Artifacts, a => a.Kind == ReleaseKinds.Dsh);
        Assert.Equal(("dsh-v0.2.1-rc.5", "24.x"), (dsh.SourceTag, dsh.RequiresNode));
    }
}
