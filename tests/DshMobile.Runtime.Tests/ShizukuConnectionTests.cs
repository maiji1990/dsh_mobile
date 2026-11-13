using DshMobile.Runtime.Automation.Relay.Shizuku;

namespace DshMobile.Runtime.Tests;

/// <summary>ShizukuState.kt's ping / requestAuthorization guards, ported as <see cref="ShizukuConnection"/>.</summary>
public sealed class ShizukuConnectionTests
{
    private static ShizukuProbe P(bool preV11 = false, bool alive = true, bool granted = false, bool rationale = false) =>
        new(preV11, alive, granted, rationale, alive ? 13 : -1, alive ? 2000 : -1);

    [Fact]
    public void Decide_FollowsUpstreamGuardOrder()
    {
        Assert.Equal(ShizukuConnectState.NotRunning, ShizukuConnection.Decide(P(alive: false)));
        Assert.Equal(ShizukuConnectState.NotRunning, ShizukuConnection.Decide(P(alive: false, granted: true)));
        Assert.Equal(ShizukuConnectState.Unsupported, ShizukuConnection.Decide(P(preV11: true)));
        Assert.Equal(ShizukuConnectState.Unsupported, ShizukuConnection.Decide(P(preV11: true, granted: true)));
        Assert.Equal(ShizukuConnectState.Granted, ShizukuConnection.Decide(P(granted: true)));
        Assert.Equal(ShizukuConnectState.Granted, ShizukuConnection.Decide(P(granted: true, rationale: true)));
        Assert.Equal(ShizukuConnectState.PermanentlyDenied, ShizukuConnection.Decide(P(rationale: true)));
        Assert.Equal(ShizukuConnectState.CanRequest, ShizukuConnection.Decide(P()));
    }

    [Fact]
    public void Alive_NeverForPreV11()
    {
        Assert.True(ShizukuConnection.Alive(P()));
        Assert.False(ShizukuConnection.Alive(P(preV11: true)));
        Assert.False(ShizukuConnection.Alive(P(alive: false)));
        Assert.Equal(41, ShizukuConnection.PermissionRequestCode);
    }
}
