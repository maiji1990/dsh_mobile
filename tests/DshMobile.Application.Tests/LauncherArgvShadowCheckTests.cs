using DshMobile.Application.Shadow;

namespace DshMobile.Application.Tests;

public class LauncherArgvShadowCheckTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly string[] Sandbox = ["proot", "--rootfs=/b", "/usr/bin/bash", "-c", "echo keepalive"];
    private static readonly string[] DshFirst = ["proot", "--bind=/c:/opt/dshbox-cache", "node", "bin.js"];
    private static readonly string[] DshRestart = ["proot", "node", "bin.js"];

    [Fact]
    public void IdenticalArgv_IsAgree()
    {
        var r = LauncherArgvShadowCheck.Compare(Sandbox, DshFirst, Sandbox, DshFirst, DshRestart, 1, At);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("first start", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RestartVariant_IsAgreeAndNamed()
    {
        var r = LauncherArgvShadowCheck.Compare(null, DshRestart, Sandbox, DshFirst, DshRestart, 1, At);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("in-place restart", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentToken_IsDivergeWithIndex()
    {
        string[] observed = ["proot", "--rootfs=/other", "/usr/bin/bash", "-c", "echo keepalive"];
        var r = LauncherArgvShadowCheck.Compare(observed, null, Sandbox, DshFirst, DshRestart, 1, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("arg 1", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraLegacyToken_IsDivergeWithLength()
    {
        string[] observed = [.. DshFirst, "--patch"];
        var r = LauncherArgvShadowCheck.Compare(null, observed, Sandbox, DshFirst, DshRestart, 1, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("legacy extra '--patch'", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingRunning_IsInconclusive() =>
        Assert.Equal(ShadowVerdict.Inconclusive, LauncherArgvShadowCheck.Compare(null, null, Sandbox, DshFirst, DshRestart, 1, At).Verdict);
}
