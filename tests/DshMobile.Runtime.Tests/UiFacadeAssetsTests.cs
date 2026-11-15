using DshMobile.Runtime.Automation;

namespace DshMobile.Runtime.Tests;

/// <summary>
/// The ui_* guest scripts the C# owner stages itself (ADR-0016, ui_* execution layer): byte-identical to their source
/// files (moved from the legacy bridge's APK assets in PR-10), staged idempotently, and the self-test command equal to
/// the legacy relay.selftest.
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
        Repo.PathOf("src", "DshMobile.Runtime", "Automation", "Assets", name);

    [Theory]
    [InlineData(UiFacadeAssets.FacadeName)]
    [InlineData(UiFacadeAssets.CheckName)]
    public void Embedded_IsTheSourceFileByteForByte(string name) =>
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
        Assert.Equal("/opt/dshmobile-cache/dshmobile/ui-facade-check.mjs", UiFacadeAssets.GuestCheck);
    }

    /// <summary>
    /// The bridge RuntimeController.kt relay.selftest at 128c2ea (pinned in PR-10): <c>GUEST_UI_CHECK</c>, the command
    /// before its <c>echo DSHMOBILE_EXIT=$?</c> (only there to recover an exit code its runner did not return; the C#
    /// runner returns the process exit code, which is the same last command's code), and the 400-line tail.
    /// </summary>
    [Fact]
    public void SelfTestCommand_IsTheLegacyRelaySelftestCommand()
    {
        Assert.Equal("/opt/dshmobile-cache/dshmobile/ui-facade-check.mjs", UiFacadeAssets.GuestCheck);
        Assert.Equal("/opt/pilot/bin/relay doctor; echo; node /opt/dshmobile-cache/dshmobile/ui-facade-check.mjs --approval-wait-ms 50000",
            UiFacadeAssets.SelfTestCommand);
        Assert.Equal(400, UiFacadeAssets.SelfTestMaxLines);
    }
}
