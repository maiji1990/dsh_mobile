using DshMobile.Application.Ui;
using DshMobile.Runtime;
using DshMobile.Runtime.Layers;

namespace DshMobile.Application.Tests;

/// <summary>
/// The runtime page's "安装内置 DSH" row: a successful legacy <c>runtime.prepare</c> may have kept the layer or failed to
/// install it, and the page must say which instead of "done" (found on the 2026-11-16 real-device pass).
/// </summary>
public sealed class PrepareOutcomeTests
{
    private const string Head = RuntimePreparer.NoEmbeddedRuntime + "; ";

    [Fact]
    public void Kept_IsNotAFailure()
    {
        var o = PrepareOutcome.Parse(Head + "DSH kept at 0.2.1-rc.5");
        Assert.Equal(PrepareKind.Kept, o.Kind);
        Assert.Equal("0.2.1-rc.5", o.Version);
        Assert.False(o.Failed);
    }

    [Fact]
    public void Installed_CarriesTheVersion()
    {
        var o = PrepareOutcome.Parse(Head + "DSH 0.2.1-rc.5 installed");
        Assert.Equal(PrepareKind.Installed, o.Kind);
        Assert.Equal("0.2.1-rc.5", o.Version);
        Assert.False(o.Failed);
    }

    [Fact]
    public void ProvisionFailure_IsAFailureWithItsCode()
    {
        var step = BundledDsh.StepText(new DshLayerResult(false, DshLayerErrorCodes.Sha256Mismatch, "expected abc got def", "0.2.0", false, 0));
        var o = PrepareOutcome.Parse(Head + step);
        Assert.Equal(PrepareKind.ProvisionFailed, o.Kind);
        Assert.Equal(DshLayerErrorCodes.Sha256Mismatch, o.Code);
        Assert.Equal("expected abc got def", o.Detail);
        Assert.True(o.Failed);
    }

    [Fact]
    public void StepTexts_RoundTrip()
    {
        Assert.Equal(PrepareKind.Kept, PrepareOutcome.Parse(BundledDsh.StepText(new DshLayerResult(true, "OK", null, "1.0.0", false, 0))).Kind);
        Assert.Equal(PrepareKind.Installed, PrepareOutcome.Parse(BundledDsh.StepText(new DshLayerResult(true, "OK", null, "1.0.0", true, 0))).Kind);
    }

    [Fact]
    public void LayersMissing_IsNotInstalled()
    {
        var o = PrepareOutcome.Parse(Head + RuntimePreparer.LayersMissing);
        Assert.Equal(PrepareKind.LayersMissing, o.Kind);
        Assert.Equal(RuntimeErrorCodes.NotInstalled, o.Code);
        Assert.True(o.Failed);
    }

    [Fact]
    public void NoBaseline_IsBundleNotFound()
    {
        var o = PrepareOutcome.Parse(Head + BundledDsh.NoBaseline);
        Assert.Equal(PrepareKind.NoBaseline, o.Kind);
        Assert.Equal(DshLayerErrorCodes.BundleNotFound, o.Code);
        Assert.True(o.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else")]
    public void Unknown_IsNotClaimedAsSuccessDetail(string? message)
    {
        var o = PrepareOutcome.Parse(message);
        Assert.Equal(PrepareKind.Unknown, o.Kind);
        Assert.False(o.Failed);
    }
}
