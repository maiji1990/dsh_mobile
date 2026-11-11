using DshMobile.Application.Shadow;
using DshMobile.Runtime;

namespace DshMobile.Application.Tests;

public class GuestExecShadowCheckTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static (string, GuestProcessResult) C(string key, bool ok, string output) => (key, new GuestProcessResult(ok, ok ? 0 : 1, output, ok ? null : "GUEST_CMD_FAILED"));

    [Fact]
    public void SameOutputs_IgnoringSurroundingWhitespace_AreAgree()
    {
        var r = GuestExecShadowCheck.Compare(
            [("arch", true, "aarch64\n"), ("debian", true, "13.6")],
            [C("arch", true, "aarch64"), C("debian", true, " 13.6 \n")], 5, At);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Equal(GuestExecShadowCheck.Slice, r.Slice);
    }

    [Fact]
    public void DifferentOutput_IsDiverge()
    {
        var r = GuestExecShadowCheck.Compare([("node", true, "v24.19.0")], [C("node", true, "v24.18.0")], 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("node:", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyOkButCSharpFailed_IsDiverge()
    {
        var r = GuestExecShadowCheck.Compare([("arch", true, "aarch64")], [C("arch", false, "")], 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("GUEST_CMD_FAILED", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingOrExtraCommands_AreDiverge()
    {
        Assert.Equal(ShadowVerdict.Diverge, GuestExecShadowCheck.Compare([("arch", true, "a"), ("dsh", true, "0.2")], [C("arch", true, "a")], 5, At).Verdict);
        Assert.Equal(ShadowVerdict.Diverge, GuestExecShadowCheck.Compare([("arch", true, "a")], [C("arch", true, "a"), C("x", true, "y")], 5, At).Verdict);
    }
}
