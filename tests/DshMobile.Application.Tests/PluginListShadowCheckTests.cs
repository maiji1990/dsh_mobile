using DshMobile.Application.Shadow;
using DshMobile.Runtime.Guest;

namespace DshMobile.Application.Tests;

public class PluginListShadowCheckTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private const string Two = """[{"dependencies":{"a":{"version":"1.0.0"},"b":{"version":"2.0.0"}}}]""";

    [Fact]
    public void SameListDespiteNoise_IsAgree()
    {
        var r = PluginListShadowCheck.Compare(true, "Progress: x\n" + Two, new GuestPluginResult(true, 0, Two, null), 5, At);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("2 plugin(s) identical", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentVersion_IsDivergeWithBothSides()
    {
        var other = """[{"dependencies":{"a":{"version":"1.0.1"},"b":{"version":"2.0.0"}}}]""";
        var r = PluginListShadowCheck.Compare(true, Two, new GuestPluginResult(true, 0, other, null), 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("only legacy: a@1.0.0", r.Detail, StringComparison.Ordinal);
        Assert.Contains("only csharp: a@1.0.1", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpFailure_IsDivergeWithError()
    {
        var r = PluginListShadowCheck.Compare(true, Two, new GuestPluginResult(false, 1, "", "GUEST_CMD_FAILED: guest command exit=1"), 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("csharp error: GUEST_CMD_FAILED", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BothEmptyJsonLists_IsAgree() =>
        Assert.Equal(ShadowVerdict.Agree, PluginListShadowCheck.Compare(true, "[]", new GuestPluginResult(true, 0, "[]", null), 5, At).Verdict);

    [Fact]
    public void BothUnparsable_IsNotMistakenForTwoEmptyLists()
    {
        var r = PluginListShadowCheck.Compare(true, "ERR_PNPM_X", new GuestPluginResult(true, 0, "ERR_PNPM_X", null), 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("json=no", r.Detail, StringComparison.Ordinal);
    }
}
