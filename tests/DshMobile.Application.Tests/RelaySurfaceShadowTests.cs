using DshMobile.Application.Shadow;

namespace DshMobile.Application.Tests;

/// <summary>
/// PR-07 segment 4: the read-only surface shadow over capabilities.json texts shaped like the ones Relay publishes
/// (RelayContainer.capabilitiesJson: one row per registered capability, backend.shizuku with the trusted display).
/// </summary>
public sealed class RelaySurfaceShadowTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static readonly string[] Registered = ["ui.node", "ui.snapshot"];

    private static string Row(string id, bool usable, string? surface = null, string? degrade = null, bool implemented = true, string surfaces = "\"foreground\",\"trusted-display\"") =>
        $$"""{"id":"{{id}}","category":"observe","usable":{{(usable ? "true" : "false")}},"implemented":{{(implemented ? "true" : "false")}},"minSdk":29,"surfaces":[{{surfaces}}],"guide":"ACCESSIBILITY"{{(surface is null ? "" : $",\"surface\":\"{surface}\"")}}{{(degrade is null ? "" : $",\"degradeReason\":\"{degrade}\"")}}}""";

    private static string Doc(string rows, bool running = false, bool bound = false, bool alive = false, int displayId = -1) =>
        """{"protocol":1,"entry":"/opt/pilot/bin/relay","host":{"package":"org.example.dshmobile"},"backend":{"shizuku":{"running":"""
        + B(running) + ""","authorized":false,"bound":""" + B(bound) + ""","shellUid":-1,"version":-1,"trustedDisplay":{"alive":"""
        + B(alive) + ""","displayId":""" + displayId + ""","nodeTree":"unknown"}}},"capabilities":[""" + rows + "]}";

    private static string B(bool b) => b ? "true" : "false";

    private static ShadowReport Check(string? json, string? preference = null, int sdk = 37) =>
        RelaySurfaceShadowCheck.Compare(json, preference, sdk, Registered, At);

    [Fact]
    public void PhoneToday_AccessibilityOff_Agrees_WithoutComparingSurfaces()
    {
        var r = Check(Doc(Row("ui.snapshot", false) + "," + Row("ui.node", false)));
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Equal(RelaySurfaceShadowCheck.Slice, r.Slice);
        Assert.Contains("2/2 rows agree (no Shizuku; surface compared on 0/2 rows", r.Detail, StringComparison.Ordinal);
        Assert.Contains("[preference FOREGROUND, sdk 37]", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessibilityOn_ComparesTheSurface()
    {
        var r = Check(Doc(Row("ui.snapshot", true, "foreground") + "," + Row("ui.node", true, "foreground")));
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("surface compared on every row", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundPreference_WithoutShizuku_FallsBack_AndMustSaySo()
    {
        var said = Doc(Row("ui.snapshot", true, "foreground", "E_SURFACE_NO_SHELL") + "," + Row("ui.node", false, null, "E_SURFACE_NO_SHELL"));
        var r = Check(said, "BACKGROUND_PREFERRED");
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        Assert.Contains("ui.node→foreground (from trusted-display: E_SURFACE_NO_SHELL)", r.Detail, StringComparison.Ordinal);

        var silent = Check(Doc(Row("ui.snapshot", true, "foreground") + "," + Row("ui.node", false)), "BACKGROUND_PREFERRED");
        Assert.Equal(ShadowVerdict.Diverge, silent.Verdict);
        Assert.Contains("degradeReason: legacy none C# E_SURFACE_NO_SHELL", silent.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundPreference_WithShizukuAndDisplay_LandsOnTheTrustedDisplay()
    {
        var r = Check(Doc(Row("ui.snapshot", true, "trusted-display") + "," + Row("ui.node", true, "trusted-display"), running: true, bound: true, alive: true, displayId: 7),
            "BACKGROUND_PREFERRED");
        Assert.Equal(ShadowVerdict.Agree, r.Verdict);
        // Android 12: the API level blocks the trusted display even with Shizuku.
        var old = Check(Doc(Row("ui.snapshot", true, "trusted-display") + "," + Row("ui.node", true, "trusted-display"), running: true, bound: true, alive: true, displayId: 7),
            "BACKGROUND_PREFERRED", sdk: 32);
        Assert.Equal(ShadowVerdict.Diverge, old.Verdict);
        Assert.Contains("surface: legacy trusted-display C# foreground", old.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentDeclaredSurfaces_OrImplemented_Diverge()
    {
        var r = Check(Doc(Row("ui.snapshot", false, surfaces: "\"foreground\"") + "," + Row("ui.node", false, implemented: false)));
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("ui.snapshot surfaces: legacy [foreground] C# [foreground,trusted-display]", r.Detail, StringComparison.Ordinal);
        Assert.Contains("ui.node implemented: legacy False C# True", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceOnAnUnusableRow_OrAMissingRow_Diverges()
    {
        var r = Check(Doc(Row("ui.snapshot", false, "foreground")));
        Assert.Equal(ShadowVerdict.Diverge, r.Verdict);
        Assert.Contains("names surface foreground on an unusable row", r.Detail, StringComparison.Ordinal);
        Assert.Contains("missing [ui.node]", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFile_Garbage_OrUnknownDisplayState_IsInconclusive()
    {
        Assert.Equal(ShadowVerdict.Inconclusive, Check(null).Verdict);
        Assert.Contains("start the relay first", Check(null).Detail, StringComparison.Ordinal);
        Assert.Equal(ShadowVerdict.Inconclusive, Check("{not json").Verdict);
        Assert.Equal(ShadowVerdict.Inconclusive, Check("""{"capabilities":[]}""").Verdict);
        // Relay publishing no row for a registered capability is a real difference, not missing evidence.
        Assert.Equal(ShadowVerdict.Diverge, Check(Doc("")).Verdict);
        var unbound = Check(Doc(Row("ui.snapshot", false) + "," + Row("ui.node", false), running: true, bound: false, alive: false, displayId: 3));
        Assert.Equal(ShadowVerdict.Inconclusive, unbound.Verdict);
        Assert.Contains("unbound", unbound.Detail, StringComparison.Ordinal);
    }
}
