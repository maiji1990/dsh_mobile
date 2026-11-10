using DshMobile.Application.Shadow;
using DshMobile.Runtime;
using DshMobile.Runtime.Bundle;

namespace DshMobile.Application.Tests;

public class BundleShadowCheckTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static BundleInspection CSharpOk() =>
        new(true, null, null, null, [new LayerVerification("base", "base.tar.zst", 1, "a", "a", true)]);

    private static BundleInspection CSharpFail(string code) => new(false, code, "x", null, []);

    [Fact]
    public void BothAccept_IsAgree() =>
        Assert.Equal(ShadowVerdict.Agree, BundleShadowCheck.Compare(new RuntimeInstallResult(true, "OK", null), CSharpOk(), 1, At).Verdict);

    [Fact]
    public void BothRejectSameUpstreamCode_IsAgree() =>
        Assert.Equal(ShadowVerdict.Agree, BundleShadowCheck.Compare(
            new RuntimeInstallResult(false, "RUNTIME_ERROR", "m", BundleErrorCodes.Sha256Mismatch), CSharpFail(BundleErrorCodes.Sha256Mismatch), 1, At).Verdict);

    [Fact]
    public void BothRejectDifferentCoveredCodes_IsDiverge() =>
        Assert.Equal(ShadowVerdict.Diverge, BundleShadowCheck.Compare(
            new RuntimeInstallResult(false, "RUNTIME_ERROR", "m", BundleErrorCodes.NoProfile), CSharpFail(BundleErrorCodes.Sha256Mismatch), 1, At).Verdict);

    [Fact]
    public void LegacyRejectsForUncoveredReason_IsInconclusive() =>
        Assert.Equal(ShadowVerdict.Inconclusive, BundleShadowCheck.Compare(
            new RuntimeInstallResult(false, "RUNTIME_ERROR", "stop the sandbox", "SANDBOX_RUNNING"), CSharpOk(), 1, At).Verdict);

    [Fact]
    public void LegacyRejectsCoveredReasonButCSharpAccepts_IsDiverge() =>
        Assert.Equal(ShadowVerdict.Diverge, BundleShadowCheck.Compare(
            new RuntimeInstallResult(false, "RUNTIME_ERROR", "m", BundleErrorCodes.MissingLayer), CSharpOk(), 1, At).Verdict);

    [Fact]
    public void LegacyAcceptsCSharpRejects_IsDiverge() =>
        Assert.Equal(ShadowVerdict.Diverge, BundleShadowCheck.Compare(
            new RuntimeInstallResult(true, "OK", null), CSharpFail(BundleErrorCodes.Sha256Mismatch), 1, At).Verdict);

    [Fact]
    public void FileSink_AppendsJsonLines()
    {
        var dir = Directory.CreateTempSubdirectory("dshm-shadow-").FullName;
        try
        {
            var path = Path.Combine(dir, "sub", "shadow.jsonl");
            var sink = new FileShadowReportSink(path);
            sink.Report(BundleShadowCheck.Compare(new RuntimeInstallResult(true, "OK", null), CSharpOk(), 7, At));
            sink.Report(BundleShadowCheck.Compare(new RuntimeInstallResult(true, "OK", null), CSharpOk(), 8, At));
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("\"verdict\":\"Agree\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"slice\":\"runtime.bundle-verify\"", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
