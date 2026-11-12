using DshMobile.Application.Shadow;
using DshMobile.Runtime.Automation;

namespace DshMobile.Application.Tests;

public sealed class UiToolsShadowCheckTests : IDisposable
{
    private const string Pkg = "org.example.dshmobile";
    private const string Enabled = Pkg + "/" + AccessibilitySetting.RelayServiceClass;
    private readonly string _userData = Path.Combine(Path.GetTempPath(), "dshm-uishadow-" + Guid.NewGuid().ToString("N"));

    public UiToolsShadowCheckTests() => Directory.CreateDirectory(_userData);

    public void Dispose() => Directory.Delete(_userData, recursive: true);

    private static UiToolsLegacyView Legacy(string method = "relay.status", bool registered = false, bool conflict = false, bool a11y = false,
        string[]? allowed = null, string guestCli = "/opt/pilot/bin/relay", string server = "ui") =>
        new(method, registered, conflict, a11y, allowed ?? ["ui.snapshot", "ui.node"], guestCli, server);

    private ShadowReport Compare(UiToolsLegacyView legacy, string? setting = null) =>
        UiToolsShadowCheck.Compare(legacy, _userData, setting, Pkg, 0, DateTimeOffset.UnixEpoch);

    [Fact]
    public void NothingRegistered_A11yOff_Agrees_AllowlistOrderIgnored()
    {
        var r = Compare(Legacy());
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Equal(UiToolsShadowCheck.Slice, r.Slice);
        Assert.Contains("after relay.status: registered=False conflict=False a11y=False", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredLayer_A11yOn_Agrees_WithByteIdenticalLayer()
    {
        Assert.True(UiToolsRegistration.Register(_userData));
        var r = Compare(Legacy("relay.start", registered: true, a11y: true), "com.other/.X:" + Enabled);
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("layer byte-identical", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerButDifferentBody_Diverges_OnLayerBytes()
    {
        var path = UiToolsRegistration.OverlayFile(_userData);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, UiToolsRegistration.Marker + "\n- insert: []\n");
        var r = Compare(Legacy(registered: true));
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Equal("after relay.status: layerBytes legacy=render csharp=different", r.Detail);
    }

    [Fact]
    public void ForeignLayer_IsAConflictOnBothSides_AndIsNotTouched()
    {
        var path = UiToolsRegistration.OverlayFile(_userData);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "- id: x\n");
        Assert.Equal(ShadowVerdict.Agree, Compare(Legacy(conflict: true)).Verdict);
        Assert.Equal("- id: x\n", File.ReadAllText(path));
        Assert.Equal(ShadowVerdict.Diverge, Compare(Legacy()).Verdict);
    }

    [Fact]
    public void Disagreements_AreAllNamed()
    {
        var r = Compare(Legacy("relay.stop", registered: true, a11y: true, allowed: ["ui.snapshot", "ui.node", "ui.tap"], guestCli: "/x/relay", server: "ux"));
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        foreach (var name in new[] { "registered legacy=True csharp=False", "layerBytes legacy=render csharp=-", "accessibility legacy=True csharp=False",
                     "allowed legacy=ui.node,ui.snapshot,ui.tap csharp=ui.node,ui.snapshot", "guestCli legacy=/x/relay", "serverName legacy=ux csharp=ui" })
        {
            Assert.Contains(name, r.Detail, StringComparison.Ordinal);
        }
        Assert.StartsWith("after relay.stop: ", r.Detail, StringComparison.Ordinal);
    }
}
