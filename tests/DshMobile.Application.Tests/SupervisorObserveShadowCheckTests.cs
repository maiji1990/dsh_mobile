using DshMobile.Application.Shadow;
using DshMobile.Runtime.Supervision;

namespace DshMobile.Application.Tests;

public class SupervisorObserveShadowCheckTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly DshHealthSnapshot Ready = new(true, true, 401, null);
    private static readonly DshHealthSnapshot Down = new(false, false, null, "SocketException");

    [Fact]
    public void RunningAndReady_BothSides_IsAgree()
    {
        var r = SupervisorObserveShadowCheck.Compare("RUNNING", "READY", true, new RuntimeProcessSnapshot(200, 3, 300, 2), Ready, 5, At);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("HTTP 401", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AllStopped_BothSides_IsAgree() =>
        Assert.Equal(ShadowVerdict.Agree, SupervisorObserveShadowCheck.Compare("STOPPED", "STOPPED", false, new RuntimeProcessSnapshot(null, 0, null, 0), Down, 5, At).Verdict);

    [Fact]
    public void LegacySaysRunningButNoTreeFound_IsDiverge()
    {
        var r = SupervisorObserveShadowCheck.Compare("RUNNING", "STOPPED", false, new RuntimeProcessSnapshot(null, 0, null, 0), Down, 5, At);
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("sandbox: legacy=up csharp=down", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthDisagreement_IsDiverge() =>
        Assert.Equal(ShadowVerdict.Diverge, SupervisorObserveShadowCheck.Compare("RUNNING", "READY", false, new RuntimeProcessSnapshot(200, 3, 300, 2), Ready, 5, At).Verdict);

    [Fact]
    public void NoProcAccess_IsInconclusive() =>
        Assert.Equal(ShadowVerdict.Inconclusive, SupervisorObserveShadowCheck.Compare("RUNNING", "READY", true, null, Ready, 5, At).Verdict);
}
