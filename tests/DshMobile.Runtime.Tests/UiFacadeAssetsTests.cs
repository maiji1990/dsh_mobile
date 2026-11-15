using DshMobile.Runtime.Automation;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The ui_* guest scripts the C# owner stages itself (ADR-0016, ui_* execution layer): byte-identical to the files the
/// legacy bridge copies from APK assets, staged idempotently, and the self-test command equal to legacy relay.selftest.
/// </summary>
public sealed class UiFacadeAssetsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dshm-uifacade-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Source(string name) =>
        Repo.PathOf("native", "native-bridge", "src", "main", "guest-assets", "dshmobile", name);

    [Theory]
    [InlineData(UiFacadeAssets.FacadeName)]
    [InlineData(UiFacadeAssets.CheckName)]
    public void Embedded_IsTheLegacyAssetByteForByte(string name) =>
        Assert.Equal(File.ReadAllBytes(Source(name)), UiFacadeAssets.Read(name));

    [Fact]
    public void Stage_WritesBoth_ThenIsIdempotent_AndRepairsAChangedFile()
    {
        Assert.False(UiFacadeAssets.Intact(_dir));
        Assert.Equal(2, UiFacadeAssets.Stage(_dir));
        Assert.True(UiFacadeAssets.Intact(_dir));
        Assert.Equal(0, UiFacadeAssets.Stage(_dir));

        var facade = Path.Combine(_dir, UiFacadeAssets.HostSubDir, UiFacadeAssets.FacadeName);
        File.WriteAllText(facade, "stale");
        Assert.False(UiFacadeAssets.Intact(_dir));
        Assert.Equal(1, UiFacadeAssets.Stage(_dir));
        Assert.Equal(File.ReadAllBytes(Source(UiFacadeAssets.FacadeName)), File.ReadAllBytes(facade));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, UiFacadeAssets.HostSubDir), "*.tmp"));
    }

    [Fact]
    public void Stage_UnwritableTarget_Throws_SoNothingIsRegistered()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, UiFacadeAssets.HostSubDir), "a file where the directory should be");
        Assert.ThrowsAny<IOException>(() => UiFacadeAssets.Stage(_dir));
    }

    [Fact]
    public void Paths_MatchTheFacadeRegistration()
    {
        Assert.Equal(UiToolsRegistration.GuestFacade,
            $"{RuntimeConstants.GuestCacheDir}/{UiFacadeAssets.HostSubDir}/{UiFacadeAssets.FacadeName}");
        Assert.Equal("/opt/dshbox-cache/dshmobile/ui-facade-check.mjs", UiFacadeAssets.GuestCheck);
    }

    [Fact]
    public void SelfTestCommand_IsTheLegacyRelaySelftestCommand()
    {
        var src = File.ReadAllText(Repo.PathOf("native", "native-bridge", "src", "main", "java", "dshmobile", "nativebridge", "RuntimeController.kt"));
        Assert.Contains($"private const val GUEST_UI_CHECK = \"{UiFacadeAssets.GuestCheck}\"", src, StringComparison.Ordinal);
        // Legacy appends `echo DSHMOBILE_EXIT=$?` only to recover the exit code its runner does not return; the C# runner
        // returns the process exit code, which is the same last command's code.
        var legacy = UiFacadeAssets.SelfTestCommand.Replace(UiFacadeAssets.GuestCheck, "$GUEST_UI_CHECK", StringComparison.Ordinal);
        Assert.Contains($"\"{legacy}; echo $marker${{d}}?\"", src, StringComparison.Ordinal);
        Assert.Contains("lines.size > 400", src, StringComparison.Ordinal);
        Assert.Equal(400, UiFacadeAssets.SelfTestMaxLines);
        // The legacy provisioning copies exactly these two files into the same host directory.
        foreach (var name in new[] { UiFacadeAssets.FacadeName, UiFacadeAssets.CheckName })
        {
            Assert.Contains($"copyAsset(\"dshmobile/{name}\", File(File(config.guestCacheDir, \"dshmobile\"), \"{name}\"))", src, StringComparison.Ordinal);
        }
    }
}
