using DshMobile.Application.Shadow;

namespace DshMobile.Application.Tests;

/// <summary>Switch slice (ADR-0010): the guest files of the two relays compared after the guest path rewrite.</summary>
public sealed class RelayGuestFilesShadowCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dshm-guestfiles-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_root, "pilot");

    private string Cs => Path.Combine(_root, "pilot-cs");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Listing(string entry, string snapshotState = "GRANTED") => $$"""
        {
          "protocol": 1,
          "entry": "{{entry.Replace("/", "\\/", StringComparison.Ordinal)}}\/bin\/relay",
          "capabilities": [
            {
              "id": "ui.snapshot",
              "systemPermission": "{{snapshotState}}"
            }
          ]
        }

        """;

    private void Publish(string dir, string guestEntry, string script = "script", string usage = "Run {E}/bin/relay", string? listing = null)
    {
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", "relay"), script);
        File.WriteAllText(Path.Combine(dir, "USAGE.md"), usage.Replace("{E}", guestEntry, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dir, "capabilities.json"), listing ?? Listing(guestEntry));
    }

    private ShadowReport Compare() => RelayGuestFilesShadowCheck.Compare(Legacy, Cs, "/opt/pilot", "/opt/pilot-cs", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Agrees_when_only_the_guest_path_differs()
    {
        Publish(Legacy, "/opt/pilot");
        Publish(Cs, "/opt/pilot-cs");

        var report = Compare();

        Assert.True(report.Verdict == ShadowVerdict.Agree, report.Detail);
        Assert.Equal(RelayGuestFilesShadowCheck.Slice, report.Slice);
    }

    [Fact]
    public void Names_the_differing_row_and_field()
    {
        Publish(Legacy, "/opt/pilot");
        Publish(Cs, "/opt/pilot-cs", listing: Listing("/opt/pilot-cs", "MANUAL_ONLY"));

        var report = Compare();

        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("ui.snapshot: systemPermission \"GRANTED\"|\"MANUAL_ONLY\"", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_script_or_manual_diverges()
    {
        Publish(Legacy, "/opt/pilot");
        Publish(Cs, "/opt/pilot-cs", script: "other", usage: "Other {E}");

        var report = Compare();

        Assert.Equal(ShadowVerdict.Diverge, report.Verdict);
        Assert.Contains("bin/relay", report.Detail, StringComparison.Ordinal);
        Assert.Contains("USAGE.md", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Inconclusive_while_a_side_has_not_published()
    {
        Publish(Legacy, "/opt/pilot");

        var report = Compare();

        Assert.Equal(ShadowVerdict.Inconclusive, report.Verdict);
        Assert.Contains("C# side", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_covers_json_escaped_and_plain_paths() =>
        Assert.Equal("\\/opt\\/pilot\\/bin and /opt/pilot/out",
            RelayGuestFilesShadowCheck.Rewrite("\\/opt\\/pilot-cs\\/bin and /opt/pilot-cs/out", "/opt/pilot-cs", "/opt/pilot"));
}
